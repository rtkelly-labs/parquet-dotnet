---
title: "Tests: the second round of paths the suite did not run"
kind: addition
status: fork-only
depends_on: [0074, 0079]
---

# 0083 Tests: the second round of paths the suite did not run

## What and why

After `lake(0074)`–`lake(0080)`, the combined coverage of the stripped package was 98.2% (13,472 of 13,714 lines;
the AOT suite's modes plus this suite on the Lake build). This adds fork-owned tests for the reachable remainder.
They are in `src/Parquet.Test/Coverage/` and assert behaviour, not just execution.

`Round2EncodingTest` (43 cases):
- **`BigDecimal`:** scale beyond precision; `FromDecimal` without a precision, and its default scale; buffer sizes for
  precisions 22–35 against the widest value of each; a value wider than its buffer refused.
- **Small types:** bloom filter sizes for probabilities outside (0, 1); empty statistics, and bounds that cannot be
  encoded; `Interval` text; `NanoTime` length; the serialization attributes the source generator reads.
- **`NestedLayout` guards:** short level buffers, levels of another leaf, a legacy repeated field both ways.
- **`RawColumnData`:** says when it has no levels and no strings.
- **Bit packing and RLE:** a short group padded into a short buffer; RLE runs of equal values at widths 9 and 17.
- **Encoder refusals:**
  - delta decoders refuse foreign types;
  - the PLAIN encoder refuses types and physical types that do not go together (dates, times, decimals, timestamps
    without a unit, an unannotated INT64 timestamp), and a decimal statistic too wide for its column;
  - fixed-length pages need their length and whole values.
- **Decoding and hardware:** INT96 statistics as timestamps when asked; converted-only UINT_8; booleans encoded the
  same without hardware acceleration.
- **Thrift and schemas:** a Thrift field of no known type; footer schemas with a broken map container, legacy and
  converted-only types, a TIME without a unit, a column of no physical type.
- **Writing schemas and lookups:** a timestamp field of an unknown unit; a map of nullable values written OPTIONAL;
  footer lookups refusing null and missing unknown paths; schema and field equality, TIME fields by base type.
- **Streams:** `ReadBytesExactly` past the end; the write meter disposing what it wraps.

`Round2FilesTest` (23 cases, whole files):
- **Encryption:**
  - a key of the wrong length;
  - an AAD prefix that is not stored, and a stored one that does not match;
  - an encrypted footer without a key, and with the wrong one;
  - a plaintext footer whose signature does not match, or that has none;
  - a column key from the key retriever, and a column without one hidden;
  - an encrypted module with an invalid length;
  - AAD ordinals over 32,767, truncated modules, an unknown algorithm.
- **Page index and bloom filters:**
  - no column index for a column without an order, or with a page of NaNs only, but an offset index still;
  - an INT96 timestamp bloom filter holding its 12-byte values;
  - bloom filters that are not split block, or not whole blocks, refused;
  - a page index outside the file refused.
- **Corrupt pages:** a page of an unknown type, a data page without its header (v1 and v2), an unknown encoding, a
  second dictionary page.
- **Statistics:** paged chunks of unsigned, binary, `BigDecimal` and `Interval` columns; unsigned 64-bit statistics;
  values longer than the bound limit; a 300,000-character string.
- **Encoding and compression:** unsigned 64-bit delta encoding; compression levels outside the enum for Zstd and LZ4.

## Evaluation

- **Plain build:** 1,236 passed, 3 skipped, on net8.0 and net10.0.
- **Stripped, renamed Lake build:** 1,014 passed, 1 skipped.
- **Defects:** none found in this round.

## Upstream

Not applicable.

## Compatibility

Test-only.
