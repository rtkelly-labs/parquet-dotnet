---
title: "`BYTE_STREAM_SPLIT` for int16 and uint16 columns"
kind: code
status: fork-only
depends_on: [0025]
---

# 0026 `BYTE_STREAM_SPLIT` for int16 and uint16 columns

## What and why

`short` and `ushort` columns are stored as INT32, so BYTE_STREAM_SPLIT splits them into four streams, as `int`.
The encoder split their two in-memory bytes instead, so every such file was invalid for other readers:
parquet-cpp rejects it ("ByteStreamSplit data size not aligned with type INT32 and byte width: 4"), and
files where the sizes happened to align read back as garbage. Reading had the mirror bug, for files written by
others. Values are now widened to `int` (sign- or zero-extended) to encode, and narrowed after decoding.

## Evaluation

- **Found by** the conformance harness (`lake(0032)`): parquet-cpp could not read int16/uint16 columns
  written with `EncodingHint.ByteSplitStream`. Reproduced on upstream `master` at `658e1ca` with no fork change:
  parquet-cpp fails on a three-value `short` column.
- **Test:** `ByteStreamSplitInt16Test`: parquet-cpp reads the exact INT32 values, and the library reads them
  back; both fail without the change.
- **Suite:** the whole suite passes with it (with every change of the stack, 809 passed, 3 skipped, on net8.0 and
  net10.0), and the conformance harness's 2,000 cases pass in both directions.

## Upstream

Not yet proposed. A bug in upstream's own code, reproducible on upstream `master` with files from parquet-cpp
(pyarrow and parquet-mr write the same layouts). Its test uses ParquetSharp, which `lake(0025)` adds to the
test project, so it depends on 0025; upstream may prefer committed fixture files. A person proposes it from
`lake/change/0026-byte-stream-split-int16` (`lake-stack branches`, docs/maintaining.md).

## Compatibility

Int16/uint16 BYTE_STREAM_SPLIT files written by earlier Parquet.Net versions are not valid Parquet (two-byte
streams); they now read as other readers read them, not as before. Files written with the hint opt in to it.
