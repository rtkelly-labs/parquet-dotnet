---
title: "Empty column chunks"
kind: code
status: fork-only
depends_on: [0025]
---

# 0030 Empty column chunks

## What and why

A row group of no rows, as parquet-cpp writes it, has column chunks with no pages and no page offsets. Finding
a chunk's first page took the minimum of its non-zero offsets, of which there were none, and threw. A chunk
with no values is now read as empty without looking for pages.

## Evaluation

- **Found by** the conformance harness's reverse direction.
- **Test:** `EmptyRowGroupTest`: an empty row group written by parquet-cpp, then a non-empty one; reading
  the empty one throws without the change.
- **Suite:** the whole suite passes with it (with every change of the stack, 809 passed, 3 skipped, on net8.0 and
  net10.0), and the conformance harness's 2,000 cases pass in both directions.

## Upstream

Not yet proposed. A bug in upstream's own code, reproducible on upstream `master` with files from parquet-cpp
(pyarrow and parquet-mr write the same layouts). Its test uses ParquetSharp, which `lake(0025)` adds to the
test project, so it depends on 0025; upstream may prefer committed fixture files. A person proposes it from
`lake/change/0030-empty-column-chunks` (`lake-stack branches`, docs/maintaining.md).

## Compatibility

None.
