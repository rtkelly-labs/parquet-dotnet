---
title: "`ParquetOptions.WriteStatistics`"
kind: code
status: fork-only
depends_on: []
upstream_unit: "13"
---

# 0016 `ParquetOptions.WriteStatistics`

A feature: unit 13 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `54efd9a` (`ParquetOptions`, `DataColumnWriter`, `WriteStatisticsOptionTest` in
  `TimestampStatisticsTest.cs`).
- **Review:** the footer's `Statistics` object is set to null explicitly (it is pre-created otherwise).

## Evaluation

- **Without the change:** `WriteStatisticsOptionTest` does not compile.
- **With it:** 698 passed (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0016-write-statistics-option` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
