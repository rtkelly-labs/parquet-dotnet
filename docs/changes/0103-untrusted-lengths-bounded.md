---
title: "Footer length, column chunk counts and offsets, and page sizes checked before they size anything"
kind: code
status: fork-only
depends_on: [0005, 0011, 0024, 0030, 0035, 0100, 0101, 0102]
---

# 0103 Footer length, column chunk counts and offsets, and page sizes checked before they size anything

A fix for findings F2, F5 and F6 of [0100](0100-malformed-file-tests.md): lengths, counts and offsets read from the
file were used to seek, slice and allocate before anything checked them against the file.

## What and why

- **Problem:** a hostile footer length, column chunk `num_values` or offset, or page header size ended in
  `ArgumentOutOfRangeException` (`Seek`, `ReadStreamSubStream`, `Slice`), `OverflowException` (`new byte[-1]`),
  `InvalidOperationException` (`Min()` of no offsets), or `OutOfMemoryException` from one allocation sized by the file;
  and a plausible lie allocated it: `uncompressed_page_size = 2^28` made an 800-byte file allocate 268 MB and then read
  successfully. Which of these an `int.MaxValue` size gave depended on the machine (PR #2's CI saw
  `OutOfMemoryException` in `ReadRestAsync` where a local run saw others).
- **Change:** each value is checked where it is read, before it sizes anything, and a value that cannot be right is a
  `ParquetException` (or `EndOfStreamException`/`InvalidDataException` where the data runs out or is corrupt):
  - **footer length** (`ParquetActor.GoBeforeFooterAsync`): between 0 and the file's length less 8 (F2);
  - **column chunk `num_values`** (`ParquetRowGroupReader.GetColumnMetaData`, which both read paths use): between 0 and
    `Array.MaxLength`, since it sizes the column's buffers (F5);
  - **the first page's offset** (`DataColumnReader.ReadAsync`): inside the file; a chunk whose offsets are all 0 no
    longer throws from `Min()` (F5);
  - **page sizes** (`DataColumnReader.ReadAsync`, for every page, plaintext or encrypted): `compressed_page_size`
    between 0 and what is left of the file, `uncompressed_page_size` not negative (F6);
  - **v2 level lengths** (`ReadDataPageV2Async`): not negative, and together no longer than the page (F6);
  - **the uncompressed size is not trusted for more than the data can hold** (`DefaultCompressor`): UNCOMPRESSED checks
    that the page has the bytes before allocating (rather than after, as 0101 did); gzip allocates at most 1032 times
    its input (deflate's limit) and LZ4 at most 255 times (+1 KiB), and the result is sliced to what was decompressed as
    before; a zstd frame whose declared content size is more than 32768 times its input (a 4-byte RLE block holds at
    most 128 KiB) is an `InvalidDataException`. Snappy and zstd already size their output from the compressed data,
    not the header.
- **Review:** the ratio caps change no valid file's result: a page cannot decompress to more than the cap, and output
  was already sliced to what was decompressed. Brotli is left as it was: its expansion has no useful bound (a valid
  page can legitimately be far smaller than its output), and it is not in the corpus. `num_values` is checked against
  `Array.MaxLength`, not against the file: run-length encoding lets a few bytes hold millions of values, so a large
  but representable count (`meta:values=2^28`) still allocates; those 9 cases stay `any` (the allocation fails or
  succeeds depending on the machine's memory), and fixing them needs buffers that grow with the pages rather than one
  sized by the footer.

## Evaluation

- **Malformed-file tests** (`MalformedFileTest`, net8.0 and net10.0, two runs each): known findings **684 → 423**.
  Taken off, by mutation: page `uncompressed=2^28` 37 (they were `any`; now deterministic and clean),
  `compressed=-1` 37, footer length 33, `compressed=intmax` 30
  (including the 14 that PR #2's CI saw fail in `ReadRestAsync`), `uncompressed=intmax` 12, `uncompressed=-1` 12, v2
  `replen`/`deflen` 44, `compressed=0` 11, `meta:values=longmax/intmax/-1` 27, `meta:dataoffset=eof` 9, 7 bit flips and
  2 others. No case that read cleanly before fails now. `any` remains only on the 9 `meta:values=2^28` cases.
- **The whole suite:** net10.0 and net8.0 each 2,816 passed (on `lake(0055)`), 3 skipped (upstream's), 0 failed.
- **Performance:** a few comparisons per column chunk and per page; no copies, no new allocations. Not measurable.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0103-untrusted-lengths-bounded` (`lake-stack branches`, Lake.NET docs/lake-parquet/maintaining.md). The footer, row group and
page checks apply to upstream as they are; the compressor caps sit on code that 0011 and 0035 changed.

## Compatibility

Files with impossible lengths, counts or offsets now fail with `ParquetException` (or `EndOfStreamException`/
`InvalidDataException`) instead of `ArgumentOutOfRangeException`, `OverflowException`, `OutOfMemoryException` or
`InvalidOperationException`. Valid files read as before.
