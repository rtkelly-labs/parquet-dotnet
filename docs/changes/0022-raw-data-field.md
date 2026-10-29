---
title: "`RawDataField`"
kind: code
status: fork-only
depends_on: [0002, 0003, 0007, 0008, 0010, 0015, 0017, 0019]
upstream_unit: "18"
---

# 0022 `RawDataField`

A feature: unit 18 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** the `RawDataField` parts of `f12c75b`, `02925d8` and `326b529` (squash: `326b529` fixes a bug in
  `f12c75b`, where byte-array raw fields became repeated because `typeof(byte[])` is discovered as an array).
- **Change:** a column declared exactly as stored (physical type, logical and converted type, length), read and
  written as physical values; text columns also as strings.
- **Review:** the most "table-format specific" unit; upstream may prefer extending `DataField` instead. It is what
  makes Parquet.Net usable by Iceberg/DuckLake without CLR-type mapping.

## Evaluation

- **Without the change:** `RawDataFieldTest` does not compile.
- **With it:** 750 passed (the whole suite, when the unit was verified on upstream `658e1ca` with 1, 3, 7, 8, 9, 12, 14, 16 applied first).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0022-raw-data-field` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
