---
title: "Tests: error branches and reachable paths the suite did not run"
kind: addition
status: fork-only
depends_on: [0062, 0067]
---

# 0074 Tests: error branches and reachable paths the suite did not run

## What and why

Lake.NET decision 6, as amended on 1 November 2026: every line the package ships is run by the regression suite, and
a line the public API cannot reach is stripped or tested from the inside. A coverage measurement (the native AOT
suite's modes plus the xunit suite, on the stripped build at `1429aa1c`) listed the reachable lines nothing ran. This
adds fork-owned tests for them under `src/Parquet.Test/Coverage/` (60). Each test asserts the behaviour, not just
that the line runs:

- **`StreamWrappersTest`:**
  - the write meter around unseekable outputs counts every kind of write, passes timeouts and disposal through, and
    refuses reads and seeks;
  - both read-only substreams read, seek and bound-check their window, and refuse writes and resizing;
  - the stream substream refuses a null, unseekable or negative window.
- **`SchemaApiTest`:**
  - field paths append, compare and index;
  - a list declared from a CLR type gets its element;
  - a list takes one item and a map two; a map key cannot be nullable; a schema needs a field;
  - schemas and fields compare, with the differences named;
  - a loose field cannot be written;
  - raw fields take their physical type's CLR type and refuse INT96 and unsized fixed arrays; an unknown time unit is
    refused;
  - column descriptors describe each leaf.
- **`CompressionAndIoTest`:**
  - Zstd and LZ4 at every compression level;
  - LZO is not implemented and unknown codecs are refused;
  - a Gzip page shorter than its header says, a Zstd frame without a content size, and an empty Zstd frame decode;
  - a page shorter than its stream claims is an error;
  - the writer refuses read-only streams, a null schema, and appends to empty, unseekable or encrypted files;
  - footer metadata round-trips; the schema reads alone from a path and a stream;
  - the row group writer refuses a null field, a mistyped value array and a repeated column without levels;
  - raw reads check each buffer;
  - a corrupt bloom filter header is refused.
- **`EncodingEdgesTest`:**
  - RLE runs of three- and four-byte values decode; widths over four bytes are refused; empty runs end decoding;
  - byte stream split decodes without hardware acceleration, refuses short input, and takes `Half` columns;
  - delta encodings refuse types they do not take;
  - statistics values decode by converted type, INT96 as `BigInteger`, and INT96 of the wrong width;
  - the plain encoder refuses a `BigInteger` beyond 96 bits, a zero-width decimal, a mis-sized UUID and decimals on a
    non-decimal type;
  - fixed-length strings (STRING on FIXED_LEN_BYTE_ARRAY) read as strings;
  - unsigned raw columns keep unsigned statistics, and none for a chunk of nulls.
- **`ColumnBuffersTest`:**
  - the internal column buffers refuse parts they do not hold;
  - the schema element helpers answer false for no element;
  - the exceptions carry their message and cause.

## Evaluation

All 60 pass on net8.0 and net10.0, in under a second. Suite: 1,149 passed, 3 skipped.

Writing them found three defects. They are not encoded in tests; each is for its own change:

- `RleBitpackedHybridEncoder.WriteRle4` writes its fourth byte as `value >> 32`. C# masks an `int` shift count, so
  that is `value >> 0`, and dictionary indexes of 2^24 or more are written corrupted;
- a second `ParquetWriter.DisposeAsync` writes the footer again;
- `ParquetWriter.CreateAsync(schema, null)` throws `NullReferenceException`, since the base constructor reads
  `output.CanSeek` before the null check.

## Upstream

Not applicable.

## Compatibility

Test-only.
