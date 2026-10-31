---
title: "`DELTA_BINARY_PACKED` for int16, uint16 and uint32 columns"
kind: code
status: fork-only
depends_on: [0025]
---

# 0027 `DELTA_BINARY_PACKED` for int16, uint16 and uint32 columns

## What and why

`short`, `ushort` and `uint` are stored as INT32, whose DELTA_BINARY_PACKED deltas are 32-bit two's complement.
The encoder computed deltas in the values' own width: wrapping at 16 bits for `short`/`ushort`, unsigned for
`uint`. Parquet.Net read its own files back, but other readers did not: `[-32768, 32767, 0]` read back in
parquet-cpp as `[-32768, -32769, -65536]`. The values are now widened to `int` to encode and narrowed after
decoding, which also reads files other writers produce correctly.

## Evaluation

- **Found by** the conformance harness (`lake(0032)`). Reproduced on upstream `master` at `658e1ca`.
- **Test:** `DeltaInt32BackedTypesTest`: extremes of `short` and `ushort`, and `uint` deltas larger than
  `int.MaxValue`, read exactly by parquet-cpp and by the library; they fail without the change.
- **Suite:** the whole suite passes with it (with every change of the stack, 809 passed, 3 skipped, on net8.0 and
  net10.0), and the conformance harness's 2,000 cases pass in both directions.

## Upstream

Not yet proposed. A bug in upstream's own code, reproducible on upstream `master` with files from parquet-cpp
(pyarrow and parquet-mr write the same layouts). Its test uses ParquetSharp, which `lake(0025)` adds to the
test project, so it depends on 0025; upstream may prefer committed fixture files. A person proposes it from
`lake/change/0027-delta-int32-backed-types` (`lake-stack branches`, docs/maintaining.md).

## Compatibility

Int16/uint16/uint32 DELTA_BINARY_PACKED files written by earlier Parquet.Net versions with large deltas are invalid Parquet and now read as other readers read them.
