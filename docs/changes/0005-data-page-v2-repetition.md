---
title: "`DATA_PAGE_V2` with repetition levels"
kind: code
status: fork-only
depends_on: []
upstream_unit: "5"
---

# 0005 `DATA_PAGE_V2` with repetition levels

A bug fix: unit 5 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** `bfd86da`.
- **Problem:** reading a v2 page of a repeated column throws `NotImplementedException`; compressed v2 pages were also
  decompressed to the whole page's size rather than the values section's.
- **Change:** levels come from the byte lengths in the header (v2 has no length prefix), present values are counted
  from the definition levels (`NumNulls` counts empty and null lists too), only the values section is decompressed.
- **Review:** test files `data/v2/*.parquet` were written by parquet-cpp (ParquetSharp 25.0.1.1); upstream may prefer
  fixtures from `apache/parquet-testing`.

## Evaluation

- **Without the change:** `DataPageV2Test` fails (binary fixtures included).
- **With it:** pass (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0005-data-page-v2-repetition` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
