---
title: "Pages shorter than their header fail instead of decoding stale pooled bytes"
kind: code
status: fork-only
depends_on: [0005, 0011, 0024, 0035, 0100]
---

# 0101 Pages shorter than their header fail instead of decoding stale pooled bytes

A fix for finding F7 of [0100](0100-malformed-file-tests.md): a page with fewer bytes than its header declares was
decoded with the rest of a rented, uncleared `ArrayPool` buffer standing in for the missing bytes.

## What and why

- **Problem:** page buffers are `MemoryOwner<byte>.Allocate(n)` (CommunityToolkit), which rents from
  `ArrayPool<byte>.Shared` without clearing. Four paths filled such a buffer only partly and returned all of it:
  - `DefaultCompressor.NoneDecompress` (UNCOMPRESSED chunks) rents `uncompressed_page_size` bytes and copies the page's
    `compressed_page_size` bytes, or fewer when the file ends early;
  - `BrotliDecompress` stops at the end of the brotli stream, and `Lz4Decompress` ignored `LZ4Codec.Decode`'s result
    (the number of bytes written, or negative on corrupt input). The compressed bytes themselves are read whole or not
    at all (`ReadRestAsync`, 0035), so only the output side is affected;
  - `DataColumnReader.ReadDataPageV2Async` rents `compressed_page_size` bytes for a v2 page and copies what the file
    has left.

  Whatever an earlier read left in the pooled array was then decoded as levels and values: the outcome changed from run
  to run (an `ArgumentOutOfRangeException` in the RLE decoder, or success with wrong values), and bytes of another file
  read in the same process could surface as this file's values. Example: `map_simple.parquet|flip@9.4` turns a page's
  `compressed_page_size` from 10 into 2; it read cleanly or failed depending on the test order.
- **Change:**
  - UNCOMPRESSED (v1 pages and dictionary pages): fewer bytes than `uncompressed_page_size` is an
    `EndOfStreamException`. For this codec the two sizes are the same by definition, so a valid file never has fewer.
  - v2 pages: fewer bytes in the file than `compressed_page_size` is an `EndOfStreamException`, as
    `ReadBytesExactlyAsync` already does for encrypted pages.
  - Brotli and LZ4: the result is sliced to what was decompressed, as `GzipDecompress` already does (and zstd sizes its
    output by the frame); a negative LZ4 result is an `InvalidDataException`. Slicing, not failing, keeps files whose
    `uncompressed_page_size` overstates the page readable, as upstream's gzip path intends.
- **Review:** `MemoryOwner<T>.Slice` transfers ownership (the existing gzip comment), so the sliced owner is the one to
  dispose. The decoders still trust the counts in the page header; a page that is now shorter than its values fails in
  the decoder (F9), deterministically, instead of reading stale bytes.

## Evaluation

- **Malformed-file tests** (`MalformedFileTest`, net8.0 and net10.0): known findings **715 → 698**. The 17 cases taken
  off are `map_simple.parquet|flip@9.4` (the record's example above, listed as `any` since the rebase onto
  `lake(0049)`) and `compressed=0` edits (`map_simple`, `simplenested`, `byte_stream_split_256`, `snappy-nation.impala` and
  `v2/v2_snappy` pages) that were `any` and now fail cleanly on every run. The other 33 `compressed=0` cases were `any` and now fail the same way on every run (gzip,
  zstd and snappy pages with no bytes decompress to nothing, and the RLE decoder then reads past the end: F9), so they are
  listed with their exception instead of `any`. `any` is left only on the 46 `2^28` cases (huge rents, F5, F6, F8).
  No case that read cleanly before fails now; two runs on each framework agree except for the `2^28` cases.
- **The whole suite:** net10.0 and net8.0 each 2,813 passed (on `lake(0055)`), 4 skipped, 0 failed (pyarrow installed, so the
  pyarrow test ran).
- **Performance:** one integer comparison per page; no copies. Not measurable.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0101-short-pages-fail` (`lake-stack branches`, Lake.NET docs/lake-parquet/maintaining.md). Upstream alone has the same
`NoneDecompress` and Brotli code, and the same LZ4 call (reading its input differently, 0035); its v2 path differs (0005), so the v2 check would be re-applied there.

## Compatibility

A truncated UNCOMPRESSED or v2 page now throws `EndOfStreamException` (an `IOException`) where it used to decode
whatever the pool held, sometimes without an error. Valid files read as before.
