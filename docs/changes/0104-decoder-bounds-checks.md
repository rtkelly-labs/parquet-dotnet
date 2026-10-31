---
title: "Page value counts and decoder runs checked against the data they index"
kind: code
status: fork-only
depends_on: [0005, 0012, 0026, 0027, 0100, 0103]
---

# 0104 Page value counts and decoder runs checked against the data they index

A fix for findings F8 and F9 of [0100](0100-malformed-file-tests.md): value counts in page headers, and lengths,
runs and indexes inside the encoded data, were used without checking them against the buffers they index.

## What and why

- **Problem:** a page's `num_values` sized and sliced the dictionary, the dictionary indexes and the values (negative:
  `ArgumentOutOfRangeException`; `int.MaxValue`: `OutOfMemoryException`); and the decoders read past their input or
  output on a corrupt run: an RLE/bit-packed run or a 4-byte level length past the buffer, a ULEB128 varint past the
  end (ARROW-GH-41321), a dictionary index past the dictionary (ARROW-RS-GH-6229-LEVELS), a byte-array length past the
  page, a DELTA_BINARY_PACKED header with zero miniblocks or a huge block. These threw `IndexOutOfRangeException`,
  `ArgumentOutOfRangeException`, `ArgumentException` or `OutOfMemoryException`, and DELTA_LENGTH_BYTE_ARRAY used
  lengths past those it decoded (the rest of an `ArrayPool` rental, as in F7).
- **Change** (bounds checks where a value is read; no copies):
  - **counts** (`DataColumnReader`): a data page's `num_values` (v1 and v2) is between 0 and what is left of the column
    chunk (the buffers hold the chunk's values, so more could not be read anyway); a v2 page's present-value count is
    between 0 and its `num_values`; a dictionary page's `num_values` fits in the page at the physical type's
    plain width (a bit for BOOLEAN, 4 bytes for INT32, FLOAT and a BYTE_ARRAY's length prefix, 8 for INT64 and
    DOUBLE, 12 for INT96, the type length for FIXED_LEN_BYTE_ARRAY; ARROW-RS-GH-6229-DICTHEADER), and the page must
    decode to exactly that many entries: entries not decoded were left as the pooled buffer held them, and a data page
    could index them. `ParquetException`.
  - **dictionary indexes** (`ReadingColumn.Checkpoint`): each index is inside the dictionary, and indexes need a
    dictionary: a dictionary-encoded data page in a chunk without a dictionary page left its values unwritten (pooled
    memory), and for an optional or repeated column the definition levels still completed the read, so it returned
    them. `ParquetException`.
  - **RLE/bit-packed hybrid** (`RleBitpackedHybridEncoder`): the length prefix (4 bytes, then a length inside the
    buffer), an RLE run's value and count, and a bit-packed run's group count (negative, or overflowing) are checked.
    A bit-packed run whose last group is short decodes what is there, as upstream intended with its `Math.Min`; a run
    missing whole groups is `InvalidDataException`, because returning fewer values let an optional column's read
    complete from its definition levels with indexes never written (found by review on the stack's PR). At bit width 0
    a run's values are cleared to 0: `Decode8ValuesLE` writes nothing at width 0, so upstream returned pooled memory as
    the values. Tests: `DecoderBoundsTest.Bit_packed_run_missing_its_bytes_fails` and
    `Bit_packed_run_of_width_zero_decodes_zeros` fail without this.
  - **ULEB128** (`EncodingExtensions.ULEB128Decode`): end of data is `EndOfStreamException`.
  - **plain byte arrays and strings** (`ParquetPlainEncoder`): each 4-byte length prefix and each value is inside the
    page, and a fixed length is not negative. `InvalidDataException`, as the existing FLBA check.
  - **BYTE_STREAM_SPLIT**: "source span is too small" is `InvalidDataException` instead of `ArgumentException` (a short
    page is bad data, not a bad argument).
  - **DELTA_LENGTH_BYTE_ARRAY**: values are made from the lengths decoded, not from `valueCount` lengths; each length is
    inside the page. `InvalidDataException`.
  - **DELTA_BINARY_PACKED** (`DeltaBinaryPackedEncoder.CheckHeader`, before the generated decoders run): the header's
    total count fits the destination; the block size is a non-zero multiple of 128 and a miniblock a multiple of 32
    values (the format's rules); a miniblock of more than 64 Ki values (and more than
    the page's count) is refused rather than allocated; the miniblocks' bit widths (a byte each) fit in what is left of
    the page. The checks sit in the hand-written dispatcher, so the T4
    template and its six generated variations are untouched. `InvalidDataException`. The generated decoders step over a
    block's bit-width list whole even when the page ends inside it, so a later block cut off there returned a consumed
    length past the page; DELTA_LENGTH_BYTE_ARRAY and DELTA_BYTE_ARRAY, which slice the page at that length, check it
    (`InvalidDataException`) instead of throwing `ArgumentOutOfRangeException`.
- **Review:** the count check uses `ReadingColumn.ValuesRead`, which counts definition levels for optional and repeated
  fields and values for required ones, the same quantity the read loop compares with the chunk's `num_values`.
  Parquet.Net's own DELTA_BINARY_PACKED writer uses blocks of 1024 values in 32 miniblocks, inside the rules.
- **Tests:** `DecoderBoundsTest`, for corrupt pages the corpus does not generate (each fails without the change): a
  dictionary-encoded string column whose footer loses its `dictionary_page_offset` (it read back pooled memory as
  values); its dictionary page claiming 3 and 5 entries where it holds 2 in 17 bytes (both read cleanly before); and
  DELTA_LENGTH_BYTE_ARRAY and DELTA_BYTE_ARRAY lengths cut off in the first and in a later block's bit widths
  (`ArgumentOutOfRangeException` before).

## Evaluation

- **Malformed-file tests** (`MalformedFileTest`, net8.0 and net10.0, two runs each): known findings **423 → 289**.
  Taken off: 134 cases, every one whose innermost frame was a decoder or a count (59 `ArgumentOutOfRangeException`, 53
  `IndexOutOfRangeException`, 12 `OutOfMemoryException`, 8 `ArgumentException`, and 2 that now fail earlier), among
  them the three bad_data files ARROW-GH-41321, ARROW-RS-GH-6229-DICTHEADER and ARROW-RS-GH-6229-LEVELS; by mutation,
  page `values=-1/intmax/0` 65, `uncompressed=0`/`compressed=0` 21, wrong encodings 8, footer edits 26, one `v2.compressed=false` page and 10 bit flips.
  F8 and F9 have no cases left. No case that read cleanly before fails now, and the dictionary width, decoded-count,
  missing-dictionary and bit-width checks change no case's outcome.
- **The whole suite:** net10.0 and net8.0 each 2,825 passed (on `lake(0055)`), 3 skipped (upstream's), 0 failed.
- **Performance:** no performance claim is made; no BenchmarkDotNet comparison against upstream was run. The checks
  are one comparison per value or run, beside the bounds check the JIT already makes. An informal Stopwatch read of
  8 M rows (an int32 dictionary column, a string column, a nullable int64 column with a third nulls; snappy, written
  by pyarrow; 23 timed reads after 2 warm-ups, three runs each) gave medians of 657–747 ms with the change and
  748–767 ms without, inside the run-to-run noise. A claim would need `eng/lake/Lake.Parquet.Benchmarks` numbers.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0104-decoder-bounds-checks` (`lake-stack branches`, Lake.NET docs/lake-parquet/maintaining.md). The decoder checks apply to
upstream as they are; the byte-array lines differ there (0012), and the count check sits beside 0005's v2 code.

## Compatibility

Corrupt pages now fail with `ParquetException`, `InvalidDataException` or `EndOfStreamException` instead of
`IndexOutOfRangeException`, `ArgumentOutOfRangeException`, `ArgumentException` (BYTE_STREAM_SPLIT) or
`OutOfMemoryException`. A DELTA_BINARY_PACKED page whose header breaks the format's block rules, which other readers
reject too, is refused. Valid files read as before.
