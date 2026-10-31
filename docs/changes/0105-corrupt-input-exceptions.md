---
title: "Corrupt metadata, pages and codecs throw ParquetException; junk statistics are ignored"
kind: code
status: fork-only
depends_on: [0010, 0011, 0019, 0100, 0103, 0104]
---

# 0105 Corrupt metadata, pages and codecs throw ParquetException; junk statistics are ignored

A fix for findings F3, F10, F11 and F12 of [0100](0100-malformed-file-tests.md): errors the library raises on purpose
for a corrupt file, and errors from below it, were outside the family a caller catches as "this file is bad"
(`ParquetException`, `IOException`, `InvalidDataException`).

## What and why

- **Problem and change**, one boundary each:
  - **F3, corrupt Thrift:** `ThriftProtocolException` derived from `Exception`. It now derives from `ParquetException`
    (same name, namespace and constructors), so `catch(ParquetException)` catches a corrupt footer or page header, and
    code catching `ThriftProtocolException` or `Exception` is unaffected. A footer or page header cut off by the end
    of the stream is not this exception: it throws `EndOfStreamException` (0102), an `IOException` like every other
    short read, and the class's documentation says so.
  - **F10, page structure** (`DataColumnReader`): an unknown page type (or an index page inside a chunk) threw
    `NotSupportedException`, a second dictionary page `InvalidOperationException`, and a dictionary page without its
    sub-header `NullReferenceException`. Each is now a `ParquetException` naming the column. Unknown page types fail
    rather than being skipped: skipping would be allowed by the format for non-data pages, but a reader that
    silently drops pages can return a column with values missing, so failing is the conservative choice.
  - **F11, unknown codec** (`DefaultCompressor.Decompress`): `NotSupportedException` became `ParquetException`, with
    the same message. The file names a codec the format does not define; to the caller that is a bad file. (LZO,
    which the format defines and this library does not implement, still throws `NotImplementedException`; no case
    reaches it.)
  - **F12, below the library:** ZstdSharp's `ZstdException` for a corrupt frame is wrapped in `InvalidDataException`
    (as gzip's and Snappier's own errors already are), at the codec switch.
  - **F12, statistics:** min/max bytes that do not decode as the column's type (`BitConverter` on too few bytes)
    failed the whole column read with `ArgumentException`, although statistics are optional and the read does not use
    them. Decision: such statistics are **ignored**: `Min` and `Max` are null (unknown), and `MinBytes`/`MaxBytes` keep
    the raw bytes. This is the conservative behaviour for every consumer: a reader of values loses nothing, and a
    consumer that prunes with statistics (Lake.NET) treats null as "unknown" and keeps the row group, where a decoded
    guess could prune rows away. The same applies to `GetStatistics`.
- **Review:** encoding/type mismatches (F11's `ByteStreamSplitEncoder.SafeSizeOf`, `DeltaBinaryPackedEncoder.Decode`)
  no longer occur in the corpus on this base: 0026–0028 decode those encodings for more types and 0104 rejects the
  remaining combinations earlier. Brotli's own errors are not wrapped (no case uses brotli).

## Evaluation

- **Malformed-file tests** (`MalformedFileTest`, net8.0 and net10.0, two runs each): known findings **289 → 124**.
  Taken off: 165 cases: 82 unknown or index page types, 47 Thrift errors, 12 junk statistics, 9 unknown codecs,
  6 second dictionary pages, 6 Zstd errors, 3 missing dictionary headers. F3 and F10–F12 have no cases left. Left: the
  115 schema cases of F4 and the 9 `meta:values=2^28` cases (`any`). No case that read cleanly before fails now.
- **The whole suite:** net10.0 and net8.0 each 2,825 passed (on `lake(0055)`), 3 skipped (upstream's), 0 failed.
- **Performance:** none on the read path: the changes are on error paths, plus a try block around Zstd decompression
  and statistics decoding, which costs nothing when nothing is thrown.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0105-corrupt-input-exceptions` (`lake-stack branches`, Lake.NET docs/lake-parquet/maintaining.md). The base-class change of
`ThriftProtocolException` is the part most worth proposing on its own; the statistics decision is the part to discuss.

## Compatibility

Behaviour differs from upstream for corrupt input only: an unknown page type, a second dictionary page, a missing
dictionary header and an unknown codec throw `ParquetException` instead of `NotSupportedException`,
`InvalidOperationException` or `NullReferenceException`; a corrupt Zstd frame throws `InvalidDataException` instead of
`ZstdException`; `ThriftProtocolException` is now also a `ParquetException`. Statistics whose min/max bytes do not
decode give null `Min`/`Max` instead of failing the read. Valid files read as before.
