---
title: "RLE-encoded boolean pages"
kind: code
status: fork-only
depends_on: [0025]
---

# 0029 RLE-encoded boolean pages

## What and why

parquet-cpp writes booleans in data page v2 with the RLE encoding, which is one bit wide and, in a data page,
prefixed with its length. The reader decoded them with the column's type length as the bit width (0 for
booleans) and without the prefix, and divided by zero.

## Evaluation

- **Found by** the conformance harness's reverse direction.
- **Test:** `RleBooleanPagesTest`: a v2 boolean column written by parquet-cpp reads back; it throws
  `DivideByZeroException` without the change.
- **Suite:** the whole suite passes with it (with every change of the stack, 809 passed, 3 skipped, on net8.0 and
  net10.0), and the conformance harness's 2,000 cases pass in both directions.

## Upstream

Not yet proposed. A bug in upstream's own code, reproducible on upstream `master` with files from parquet-cpp
(pyarrow and parquet-mr write the same layouts). Its test uses ParquetSharp, which `lake(0025)` adds to the
test project, so it depends on 0025; upstream may prefer committed fixture files. A person proposes it from
`lake/change/0029-rle-booleans` (`lake-stack branches`, docs/maintaining.md).

## Compatibility

None: pages that failed now read.
