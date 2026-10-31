---
title: "Logical types under `DELTA_*` and `BYTE_STREAM_SPLIT` encodings"
kind: code
status: fork-only
depends_on: [0025]
---

# 0028 Logical types under `DELTA_*` and `BYTE_STREAM_SPLIT` encodings

## What and why

Other writers use DELTA_BINARY_PACKED, BYTE_STREAM_SPLIT and DELTA_BYTE_ARRAY for columns whose CLR type is not
their physical type: parquet-mr's v2 writer delta-encodes every INT32 and INT64 column, timestamps and decimals
included. Those decoders only produce physical values, so reading a timestamp, date, decimal, int8/uint8, UUID
or fixed-length column so encoded threw `NotSupportedException`. Such pages are now decoded to their physical
values, laid out as PLAIN, and converted by the PLAIN decoder, as PLAIN pages are.

## Evaluation

- **Found by** the conformance harness's reverse direction (parquet-cpp writes, the library reads): every
  such column with a non-plain encoding failed.
- **Test:** `ForeignEncodingsTest`: timestamps, dates and decimals under delta and byte stream split, int8
  under delta, UUIDs under byte stream split and delta byte array, all written by parquet-cpp; they throw
  without the change.
- **Suite:** the whole suite passes with it (with every change of the stack, 809 passed, 3 skipped, on net8.0 and
  net10.0), and the conformance harness's 2,000 cases pass in both directions.

## Upstream

Not yet proposed. A bug in upstream's own code, reproducible on upstream `master` with files from parquet-cpp
(pyarrow and parquet-mr write the same layouts). Its test uses ParquetSharp, which `lake(0025)` adds to the
test project, so it depends on 0025; upstream may prefer committed fixture files. A person proposes it from
`lake/change/0028-logical-types-under-delta-and-byte-stream-split` (`lake-stack branches`, docs/maintaining.md).

## Compatibility

None: pages that failed now read.
