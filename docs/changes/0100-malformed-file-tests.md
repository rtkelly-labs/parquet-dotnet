---
title: "Malformed-file tests for the decoder"
kind: addition
status: fork-only
depends_on: [0002, 0003, 0004, 0005, 0006, 0007, 0008, 0009, 0010, 0011, 0012, 0013, 0014, 0015, 0016, 0017, 0018, 0019, 0020, 0021, 0022, 0023, 0024, 0025, 0026, 0027, 0028, 0029, 0030, 0031, 0034, 0035, 0036, 0037, 0038, 0039, 0040, 0041, 0043, 0047, 0049, 0052, 0053, 0055]
---

# 0100 Malformed-file tests for the decoder

A test-only change. Lake.NET reads Parquet files that other engines wrote, so a corrupt or crafted file must end safely:
read without an error, or fail with an error the caller can catch as "this file is bad", never a crash, a hang or an
unbounded allocation. This adds
the tests that check that, and records what they found. It does not fix the decoder: each finding below is a
candidate fix of its own, to be made as a separate change that upstream could take.

## What and why

- **Test:** `src/Parquet.Test/MalformedFileTest.cs`. Each case opens a file from memory and reads every column of
  every row group (the reader's typed raw read, `ReadRawColumnDataAsync<T>` for the column's CLR type), on its own thread, with a 10 s timeout and a memory budget of
  64 × the file's size + 64 MB (`GC.GetAllocatedBytesForCurrentThread`; the read completes synchronously over a
  `MemoryStream`). It passes on success or on an allowed error: `ParquetException` (and its subclasses),
  `IOException` (including `EndOfStreamException`) or `InvalidDataException`. These are what the library throws on
  purpose for corrupt input: format and magic checks, decryption and signature failures, short reads. Every other
  exception, a timeout, or going over the budget is a finding.
- **Corpus, 1,909 cases:**
  - apache/parquet-testing's `bad_data/` (23 files, 14 of them in `variants/`), vendored unchanged in
    `hostile/bad_data/` (Apache-2.0, commit `eedfad3`; see its README).
  - Deterministic mutations of eleven good fixtures: `simplenested` (struct), `map_simple` (map),
    `malformed/zstd-nested` (zstd, list, nulls, two row groups; new, written by Parquet.Net, 4 KB), `v2/v2_snappy`
    (data page v2), `types/alltypes_dictionary.gzip` (gzip, dictionary, INT96), `snappy-nation.impala`,
    `delta_length_byte_array`, `delta_binary_packed`, `byte_stream_split_256`, and `encryption/uniform` and
    `encryption/plaintext_footer` read with their keys (footer key ASCII `0123456789012345`, column `name` key ASCII
    `1234567890123450`, as upstream's `EncryptionTest`). The fixtures are inputs identified by their bytes (below);
    `zstd-nested` is checked in, not regenerated. The mutations (exact generation rules below): truncation at about 27 offsets per file
    (both ends, the footer, page header boundaries, seeded); about 32 seeded single-bit flips per file (footer, page
    headers, anywhere); 11 footer lengths (0, ±1, `int.MinValue`/`MaxValue`, the file's length, off by one, doubled); 44
    hostile edits of the footer's Thrift metadata (row and value counts, offsets, sizes, null counts, codec,
    physical types, type lengths, `num_children`, dropped schema elements and column chunks, junk statistics); and
    25 edits of up to six page headers per file (page sizes, value counts, page type, encoding, missing
    sub-header, v2 level lengths and null counts), with everything after an edited header and the footer's offsets
    moved to match, so only the edited field differs. The seed is 260026, mixed with each source's name; a case id
    such as `v2/v2_snappy.parquet|page946:v2.replen=intmax` names the same bytes on every run, and the failure
    messages of `Reads_or_fails_cleanly` and `Known_findings_fail_the_same_way` print it and the seed.
    `Identity_rewrites_read_cleanly` checks that each fixture, and each with its footer or its first or last page
    header rewritten unchanged, reads cleanly.
- **Fixtures** (under `src/Parquet.Test/data`; all but `malformed/zstd-nested` are upstream's):

| Fixture | Bytes | SHA-256 |
|---|---|---|
| `simplenested.parquet` | 1055 | `0c205e643fddaae3ed4a601c646717edb087369b4518698bbf2c69b5b63e87e3` |
| `map_simple.parquet` | 798 | `f46efa1adaeb6a98865ebeeeeb9b0d0d637a13415f3d294c5f34d1e665971e21` |
| `malformed/zstd-nested.parquet` | 4010 | `7d9b34b5b6a389737c12b351eb4d2faf4ba30966c6f6c78824b0a8513f0df543` |
| `v2/v2_snappy.parquet` | 59832 | `f5d62d80a023a6f6ecb727f95a11cdf4ebaec0d0804155f7785650614805ddbb` |
| `types/alltypes_dictionary.gzip.parquet` | 2566 | `5cabf8a1cf3197714554bd9134fbcd0b97cf6001a423a7d15118996ccabf465e` |
| `snappy-nation.impala.parquet` | 2160 | `4eed430b005bb4cc56fd8beea1328076c0a3f7c19ab3c2cac4bd3917e0b6b8dd` |
| `delta_length_byte_array.parquet` | 3072 | `efef768997748d27596a5c001ccd24eae42adf2825ca4d909e8fa8eb1d89f386` |
| `delta_binary_packed.parquet` | 72971 | `d1c2173fe97255959e3d087b3fa5b7b5c27b2aac135337b2896772d7bbdc31b4` |
| `byte_stream_split_256.parquet` | 1023 | `3b28c839799563a46bfbd87b9903538c96e2a13b0fc8847667dc42c8c3c0df9d` |
| `encryption/uniform.parquet` | 7202 | `435cfb97226afe658e1265392a8d8f06c2693fba4a22b9915832dbaf90e2739d` |
| `encryption/plaintext_footer.parquet` | 5809 | `1af463b4802df599c8e85ea387529e3795800051ecb69a7ff9c16bfc70f251c2` |

- **Mutation rules** (`Mutations`, in this order; `b` is the file, `len` its length, `F`/`FL` the footer's start and
  length from the last 8 bytes, pages the page headers found by walking each column chunk from its dictionary (or
  first data) page offset, each with its start and header length `(s, n)`):
  - **Random:** one `System.Random(260026 ^ h)` per source, where `h` is the 32-bit FNV-1a hash of the source's
    relative path (start `2166136261`, for each UTF-16 char `h = (h ^ c) * 16777619`, unchecked), drawn in the order
    given here. A seeded `System.Random` is .NET's compatibility generator (Knuth's subtractive generator, as in
    .NET Framework; `Next(n)` is `(int)(Sample() * n)` and `Next(a, b)` is `a + (int)(Sample() * (b - a))`); net8.0
    and net10.0 give the same sequence, which the one known list on both frameworks checks. .NET documents that
    this sequence may change between major versions: if a runtime update changes it, the case ids move and
    `known.tsv` is regenerated (above).
  - **Truncation** `trunc@k` (`b[..k]`): the sorted, distinct set of 0, 1, 3, 4, 8, `len-1`, `len-4`, `len-5`,
    `len-8`, `len-9`, `F-1`, `F`, `F+1`, `F+FL/2`; for each of the first six pages `s+1` and `s+n`; and six
    `Next(9, F)`. Offsets outside `[0, len)` are dropped.
  - **Bit flips** `flip@p.k` (byte `p` XOR `1 << k`): twelve `(Next(F, len-8), Next(8))`; then, if there are pages,
    twelve of a page `Next(pages)` at `(s + Next(n), Next(8))`; then eight (twenty when there are no pages)
    `(Next(4, F), Next(8))`. Duplicates collapse (a sorted set), so a file can have fewer than 32.
  - **Footer length** `footerlen=v`: the 4 bytes at `len-8` set to each distinct value of 0, 1, -1, `int.MinValue`,
    `int.MaxValue`, `len`, `len-7`, `len-8`, `FL-1`, `FL+1`, `FL*2`.
  - **Footer edits** `meta:<edit>` and **page-header edits** `page<i>:<edit>`: only when the footer is plaintext and
    the columns are not encrypted. Each footer edit is applied to a copy of the parsed footer, which is rewritten after
    the unchanged body. Page-header edits are applied to pages 0, 1, 2, the last, and two `Next(pages)`, those that
    exist; an edit that does not apply to a page's type is skipped; the edited header is re-serialised, the bytes
    after it shifted by the difference in its length, and the footer's offsets, and its column chunk's compressed and uncompressed totals, moved to match. The edits, in the
    order generated (`MetaEdits()`, `PageEdits()`; a *leaf* is a schema element after the root with no children):

| Footer edit (`meta:…`) | Change |
|---|---|
| `rows=max` | file `num_rows` = `long.MaxValue` |
| `rg.rows=max` / `rg.rows=-1` | every row group's `num_rows` = `long.MaxValue` / -1 |
| `values=intmax` / `values=2^28` / `values=longmax` / `values=-1` / `values=0` / `values+1` | every column chunk's `num_values` = `int.MaxValue` / 2^28 / `long.MaxValue` / -1 / 0 / itself + 1 |
| `dataoffset=-1` / `dataoffset=eof` / `dataoffset=footer` | every chunk's `data_page_offset` = -1 / 2^40 / 4, and `dictionary_page_offset` unset |
| `dictoffset=-1` / `dictoffset=eof` | every chunk's `dictionary_page_offset` = -1 / 2^40 |
| `compsize=longmax` / `compsize=-1` | every chunk's `total_compressed_size` = `long.MaxValue` / -1 |
| `nullcount=longmax` / `nullcount=-1` / `nullcount=values+1` | every chunk's statistics (created if absent) `null_count` = `long.MaxValue` / -1 / `num_values` + 1 |
| `codec=99` / `coltype=99` | every chunk's `codec` / physical `type` = 99 |
| `leaftype=99` / `leaftype=flba` / `leaftype=int32` / `leaftype=bytearray` | every leaf's `type` = 99 / FIXED_LEN_BYTE_ARRAY / INT32 / BYTE_ARRAY |
| `typelength=-1` / `typelength=intmax` | every leaf's `type_length` = -1 / `int.MaxValue` |
| `flba.typelength=0` | every leaf's `type` = FIXED_LEN_BYTE_ARRAY and `type_length` = 0 |
| `children=intmax` / `children=-1` / `children=0` / `children+1` | the root's `num_children` = `int.MaxValue` / -1 / 0 / itself + 1 |
| `children.self` | the last schema element's `num_children` = 1 |
| `schema.droplast` / `schema.empty` | the last schema element removed / the schema list emptied |
| `leafrep=repeated` / `leafrep=required` | every leaf's `repetition_type` = REPEATED / REQUIRED |
| `decimal.scale>precision` | every leaf's `converted_type` = DECIMAL, `precision` = 2, `scale` = 40 |
| `columns.droplast` / `columns.empty` | each row group's last column chunk removed / its column chunks emptied |
| `columns.metadata=null` | every column chunk's `meta_data` unset |
| `path=missing` / `path=empty` | every chunk's `path_in_schema` = `[no, such, column]` / `[]` |
| `stats.min=junk` | every chunk's statistics: `min_value` = `[1]`, `max_value` = bytes 1..17, `min` = `[1]`, `max` = `[2]` |

| Page-header edit (`page<i>:…`) | Change | Applies when the page has |
|---|---|---|
| `compressed=-1` / `compressed=0` / `compressed=intmax` | `compressed_page_size` = -1 / 0 / `int.MaxValue` | always |
| `uncompressed=-1` / `uncompressed=0` / `uncompressed=intmax` / `uncompressed=2^28` | `uncompressed_page_size` = -1 / 0 / `int.MaxValue` / 2^28 | always |
| `type=99` / `type=index` | `type` = 99 / INDEX_PAGE | always |
| `values=intmax` / `values=-1` / `values=0` | `num_values` of the sub-header (data v1, else v2, else dictionary) = `int.MaxValue` / -1 / 0 | a sub-header is set |
| `encoding=99` / `encoding=delta` / `encoding=dict` / `encoding=bss` | `encoding` of that sub-header = 99 / DELTA_BINARY_PACKED / RLE_DICTIONARY / BYTE_STREAM_SPLIT | a sub-header is set |
| `subheader=null` | the data v1, data v2 and dictionary sub-headers all unset | always |
| `v2.deflen=intmax` / `v2.deflen=-1` | `definition_levels_byte_length` = `int.MaxValue` / -1 | a data page v2 |
| `v2.replen=intmax` / `v2.replen=-1` | `repetition_levels_byte_length` = `int.MaxValue` / -1 | a data page v2 |
| `v2.nulls=intmax` / `v2.nulls=-1` | `num_nulls` = `int.MaxValue` / -1 | a data page v2 |
| `v2.rows=-1` | `num_rows` = -1 | a data page v2 |
| `v2.compressed=false` | `is_compressed` = false | a data page v2 |

- **Known findings:** `data/malformed/known.tsv` lists the cases that fail (714 on `lake(0042)`: 713
  findings and the hang; 715 on `lake(0055)`, see Evaluation), with what each does instead and
  the library frame it fails in. They are run by `Known_findings_fail_the_same_way`, which passes while a case fails
  the same way or once it reads cleanly (then it should be taken off the list), and fails if it starts failing
  differently. A case may list alternatives (`a|b`, where .NET 8 and .NET 10 differ) or `any` (the outcome depends
  on the shared `ArrayPool`'s state: F7, and huge rents that the pool may or may not serve). The one hang is not run
  (`Known_hangs`, skipped with its reason). New cases that fail make `Reads_or_fails_cleanly` fail. To regenerate the
  list, run the class with `PARQUET_MALFORMED_KNOWN_OUT=<path>` on both target frameworks and merge.
- **Review:** the harness rewrites footers and page headers with the library's own internal Thrift classes
  (`FileMetaData`, `PageHeader`), so it needs `InternalsVisibleTo`, as other tests here do. It reads each column through the internal typed
  `ReadRawColumnDataAsync<T>`, made generic over the column's CLR type by the test itself, rather than the public
  `ReadRawColumnDataBaseAsync`, which does the same through reflection: Lake.Parquet leaves that member out of the
  package (`lake(0045)`), and the class has to compile and run against both builds. The known list is keyed by
  case id, so a change to any of the eleven fixtures or to the mutation code reshuffles it; regenerate it then.
  Leaf mutations (`leaftype=…`, `typelength=…`, `leafrep=…`, `decimal.…`) apply to every leaf of the schema:
  `Leaves` skips the root before filtering (`Schema.Skip(1).Where(…)`), not after, which would have dropped the first
  leaf and left single-leaf fixtures unchanged.
- **Where the hostile files live:** `src/Parquet.Test/hostile/bad_data`, read from the source tree (the test finds
  its own directory with `[CallerFilePath]`, so the test project, an upstream file, is unchanged), not under `data/`. The Native AOT corpus check (`lake(0048)`,
  `eng/lake/Lake.Parquet.AotTests`) decodes every `.parquet` file under `src/Parquet.Test/data` and fails on any
  refusal it does not list; these files are hostile by design and are judged here instead. Keeping them out of
  `data/` leaves that check unchanged, so this change does not depend on `lake(0048)` and `lake-stack verify` can
  replay it, and the changes built on it, on upstream. Case ids are relative to `hostile/`, so they read
  `bad_data/…` as before.

## Evaluation

- **Result** (on `lake(0042)`): 1,909 cases. 1,195 pass `Reads_or_fails_cleanly`; 713 known findings pass
  `Known_findings_fail_the_same_way`; 1 known hang is skipped. Every known outcome is the same on every run and machine
  except the 83 `any` cases; in particular the `int.MaxValue` sizes fail on an allocation over `Array.MaxLength`,
  which no amount of memory serves. The whole class passes on net8.0 and net10.0, Debug and Release, and on the renamed
  Lake.Parquet build as CI runs it; it takes about 40-70 s. Rebased onto `lake(0055)` (data pages, type discovery,
  members left out of the package, required columns read as nullable, FLOAT16 read as bytes, INT96 as BigInteger, dates before
  1970), every case has the same outcome; only the
  frames in `known.tsv` moved, by the lines `lake(0043)`, `lake(0047)`, `lake(0052)` and `lake(0053)` changed in the
  encoders and schema code.
  One case joined the list as `any`: `map_simple.parquet|flip@9.4` turns a page's `compressed_page_size` from 10 into
  2, so the page decodes stale pooled bytes (F7). It read cleanly in eight runs and failed in one full-suite run on
  net8.0 (`ArgumentOutOfRangeException` in `RleBitpackedHybridEncoder.Decode`), with whatever the other tests left in
  the shared pool; it was as order-dependent before the rebase. The list has 715 cases here.
- **The whole suite** (`dotnet test src/Parquet.Test/Parquet.Test.csproj`): net10.0 and net8.0 each 2,813 passed (on `lake(0055)`),
  4 skipped (3 skipped upstream, plus the known hang), 0 failed (pyarrow installed).
- **bad_data/:** 4 of the 23 are findings (below): `ARROW-GH-41321`, `ARROW-RS-GH-6229-DICTHEADER`,
  `ARROW-RS-GH-6229-LEVELS` and `PARQUET-1481`. Of the other 19, none is a finding: `ARROW-GH-41317` fails cleanly (`IOException`, short read) and
  `ARROW-GH-47662` (nulls in a required column) cleanly (`InvalidDataException`). `ARROW-GH-43605`,
  `ARROW-GH-45185` (repetition levels starting at 1) and `ARROW-RS-GH-11261-FLBA-DICT` read without an error, so they
  are accepted rather than rejected; whether the values are then right is not checked here. The 14 `variants/` files
  read cleanly because Parquet.Net does not decode variant values (they are binary columns to it).
- **What it throws:** the library's deliberate errors for bad input are `ParquetException`, `IOException`/
  `EndOfStreamException` and `InvalidDataException`, and those are what 1,195 cases end in on `lake(0042)`, or success. It also
  throws, on purpose, `ThriftProtocolException` (which is not a `ParquetException`) and `NotSupportedException`
  (unknown page type, codec or encoding), and these are counted as findings (F3, F10, F11): a caller that catches
  `ParquetException` does not catch them.

### Findings

Grouped by cause, with the innermost library frame (Debug build, .NET 10), the number of cases, and an example
case. Frames, lines and counts are as first measured on `lake(0025)`; `known.tsv` has each case's frame on
`lake(0055)`. On `lake(0042)` 37 of those cases read cleanly (the `encoding=delta`/`encoding=bss` page edits and the
`leaftype=flba`/`bytearray` and `flba.typelength=0` edits of some fixtures: `lake(0026)`–`lake(0028)` decode those
encodings for more types), and 19 are new: 14 `compressed=intmax` cases fail in `DefaultCompressor.ReadRestAsync`
(`lake(0035)`: snappy, gzip and LZ4 pages read into rented memory sized by the page, F6), and 5 leaf mutations of
`map_simple` and `v2/v2_snappy` that the harness used to skip (F4, F8, F9). Every case is in `known.tsv`. "OOM" means `OutOfMemoryException` from a single
allocation sized by a value read from the file (`int.MaxValue` elements); none of these allocated the memory, but
smaller hostile values do (F6).

| # | Finding | Frame(s) | Cases | Example |
|---|---|---|---|---|
| F1 | **Hang.** A Thrift varint cut off by the end of the stream never ends: `(byte)ReadByte()` turns end of stream (-1) into `0xFF`, a continuation byte, and the loop has no length limit. Here a footer length of 1 makes the footer the last byte of the signature. | `ThriftCompactProtocolReader.ReadVarInt32`/`ReadVarInt64` | 1 (skipped) | `encryption/plaintext_footer.parquet\|footerlen=1` |
| F2 | The footer length is not checked against the file. Too large or negative: `ArgumentOutOfRangeException` from `Seek` on .NET 10 (`IOException` on .NET 8); -1: `OverflowException` (`new byte[-1]`). | `ParquetActor.GoBeforeFooterAsync:80`; `StreamExtensions.ReadBytesExactlyAsync:68` | 22; 11 | `simplenested.parquet\|footerlen=2147483647`; `…\|footerlen=-1` |
| F3 | Corrupt Thrift throws `ThriftProtocolException`, which derives from `Exception`, not `ParquetException`. | `ThriftCompactProtocolReader.SkipField:213` | 59 | `byte_stream_split_256.parquet\|flip@16.5` |
| F4 | The schema is not validated: an empty schema list (`schema[0]`), `num_children` past the end of the list, a root without fields, a decimal with scale > precision, an unknown physical type (PARQUET-1481). | `ThriftFooter.ThriftSchemaTree..ctor:302` (AOORE), `ThriftSchemaTree.BuildSchema:358` (AOORE), `ParquetSchema..ctor:46` (ArgumentException), `DecimalDataField..ctor:47` (ArgumentException), `SchemaEncoder.TryBuildDataField:332` (NotSupported) | 39, 40, 19, 7, 8 | `v2/v2_snappy.parquet\|meta:schema.empty`, `…\|meta:children+1`, `…\|meta:children=0`, `…\|meta:decimal.scale>precision`, `bad_data/PARQUET-1481.parquet` |
| F5 | Column chunk metadata is trusted. `num_values` is cast to `int` and used to size the column's buffers (negative or > `int.MaxValue`: AOORE; `int.MaxValue`: OOM). A data page offset past the end: AOORE from `Seek`. All page offsets zero: `Min()` of an empty sequence. | `ParquetRowGroupReader.ReadRawColumnDataAsync:146`; `DataColumnReader.ReadAsync:81`; `DataColumnReader.GetFileOffset:138` | 18 + 9 OOM; 9; 3 | `…\|meta:values=longmax`, `…\|meta:values=intmax`; `…\|meta:dataoffset=eof`; `types/alltypes_dictionary.gzip.parquet\|flip@1431.7` |
| F6 | **Page sizes are trusted for allocation and slicing.** `compressed_page_size` and `uncompressed_page_size` size buffers before anything is read: negative gives AOORE, `int.MaxValue` OOM, and a plausible lie allocates it: `uncompressed_page_size = 2^28` makes an 800-byte file allocate 268 MB and then read successfully (`map_simple.parquet\|page0:uncompressed=2^28`: the page buffer), and the string decoder sizes one `char[]` by the page. The zstd path sizes its input by a sub-stream whose length is not clamped to the file. v2 pages slice levels by `definition/repetition_levels_byte_length` without checking them against the page. | `ReadStreamSubStream..ctor:18`; `DefaultCompressor.NoneDecompress:46`, `GzipDecompress:83`, `ZstdDecompress:206`; `DataColumnReader.ReadDataPageV2Async:198/210/218`; `ParquetPlainEncoder.Decode:1423` | 26; 7 + 7 OOM, 6 + 5 OOM, 5 OOM; 57 + 11 OOM | `…\|page0:compressed=-1`, `…\|page0:uncompressed=intmax`, `malformed/zstd-nested.parquet\|page0:compressed=intmax`, `v2/v2_snappy.parquet\|page946:v2.replen=intmax` |
| F7 | **Stale pooled memory is decoded.** When a page has fewer bytes than its `uncompressed_page_size` (for UNCOMPRESSED chunks, or a short read), `NoneDecompress` rents a buffer of the claimed size from `ArrayPool.Shared` (not cleared) and fills only what is there; the rest is whatever an earlier read left in the pooled array, and it is decoded as levels and values. The outcome changes from run to run (AOORE in the RLE decoder, or success), and bytes of another file read in the same process can surface as this file's values. | `DefaultCompressor.NoneDecompress:46`, then e.g. `RleBitpackedHybridEncoder.Decode:129` | 37 (`compressed=0`; with the 46 `2^28` cases of F5, F6 and F8, 83 are `any`) | `map_simple.parquet\|page1:compressed=0` |
| F8 | Page value counts are trusted: the dictionary page's `num_values` (negative: ARROW-RS-GH-6229-DICTHEADER) and a data page's `num_values` size buffers and slices. | `ReadingColumn.AllocateDictionary:85`; `ReadingColumn.AllocateOrGetDictionaryIndexes:91`; `DeltaLengthByteArrayEncoder.Decode:44/47`; `ParquetPlainEncoder.Decode[T]:165–222`; `ByteStreamSplitEncoder.DecodeHwx:82` (ArgumentException); `DataColumnReader.ReadColumn:327` | 4 + 3 OOM; 8 + 8 OOM; 2 + 1 OOM; 18; 8; 5 | `bad_data/ARROW-RS-GH-6229-DICTHEADER.parquet`, `snappy-nation.impala.parquet\|page2:values=-1`, `delta_length_byte_array.parquet\|page0:values=-1` |
| F9 | Levels, dictionary indexes and length prefixes are not bounds-checked while decoding: RLE/bit-packed runs past the buffer, a dictionary index past the dictionary (ARROW-RS-GH-6229-LEVELS), a ULEB128 past the buffer (ARROW-GH-41321), delta blocks past the buffer, byte-array lengths past the page. | `RleBitpackedHybridEncoder.Decode:129/130`, `ReadRle:188`, `DecodeBitpacked:206/210`; `ReadingColumn.Checkpoint:111`; `EncodingExtensions.ULEB128Decode:79`; `DeltaBinaryPackedEncoder.DecodeLong:785`, `DecodeInt:459`; `ParquetPlainEncoder.Decode:1036/1039/1426/1428` | 14 + 8, 7, 2; 6; 3; 10, 1; 4 + 10 | `map_simple.parquet\|page0:uncompressed=0`, `bad_data/ARROW-RS-GH-6229-LEVELS.parquet`, `bad_data/ARROW-GH-41321.parquet`, `delta_binary_packed.parquet\|page0:values=-1` |
| F10 | Page structure: an unknown page type or an index page in a chunk throws `NotSupportedException`; a second dictionary page `InvalidOperationException`; a dictionary page without its sub-header `NullReferenceException`. | `DataColumnReader.ReadAsync:99`; `ReadDictionaryPageAsync:124`; `ReadDictionaryPageAsync:130` | 85; 6; 3 | `…\|page9:type=99`; `v2/v2_snappy.parquet\|page946:values=0`; `snappy-nation.impala.parquet\|page1:subheader=null` |
| F11 | An unknown codec, or an encoding that does not fit the column's type, throws `NotSupportedException`. Deliberate, and also what a valid file using something unsupported gets; listed because it is outside `ParquetException`. | `DefaultCompressor.Decompress:297`; `ByteStreamSplitEncoder.SafeSizeOf:40`; `DeltaBinaryPackedEncoder.Decode[T]:169` | 9; 17; 20 | `…\|meta:codec=99`; `…\|page0:encoding=bss`; `…\|page0:encoding=delta` |
| F12 | Errors from below are not wrapped: ZstdSharp's `ZstdException` for a corrupt frame; `ArgumentException` from decoding junk min/max statistics in the footer, which fails the column read although statistics are optional. | `DefaultCompressor.ZstdDecompress:213/216`; `ParquetPlainEncoder.TryDecode:327/339/352` | 6; 12 | `malformed/zstd-nested.parquet\|flip@1558.1`; `…\|meta:stats.min=junk` |

No case threw `InvalidCastException`. No case crashed the process.

What a fix looks like, for whoever takes these on: F1 (throw on end of stream, cap a varint at 5/10 bytes) and F7
(check that a page yields exactly its declared size, or slice to what it yields) first; then validate the footer
length (F2), the schema tree (F4) and the counts, sizes and offsets read from the footer and page headers against
the file and the column chunk before allocating (F5, F6, F8; the vision's "allocation amplification from untrusted
footer counts"); then bounds checks in the decoders (F9), and one exception family for bad input (F3, F10–F12:
`ParquetException`, or wrapping).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0100-malformed-file-tests` (`lake-stack branches`, Lake.NET docs/lake-parquet/maintaining.md). The tests themselves are a
candidate (the known list would need regenerating on upstream alone, where 0011, 0012 and 0024 are absent); more
useful upstream are the fixes for F1–F12, as changes of their own, each with its cases taken off the list.

## Compatibility

None: tests and test data.
