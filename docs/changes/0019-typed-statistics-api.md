---
title: "Typed statistics API"
kind: code
status: fork-only
depends_on: [0007, 0008, 0010, 0018]
upstream_unit: "16"
---

# 0019 Typed statistics API

A feature: unit 16 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** the rest of `35bd5c1` (`DataColumnStatistics` members, `ColumnSortOrder`, `ColumnStatisticsRules`,
  `ParquetRowGroupReader.ReadColumnStatistics`, `StatisticsSortOrderTest`).
- **Change:** `SortOrder`, `HasMinMax`, `MinBytes`/`MaxBytes`, `PhysicalMin`/`PhysicalMax`, `IsMinExact`/`IsMaxExact`,
  `NanCount`, `IsLegacyMinMax`; deprecated `min`/`max` only for signed numeric columns; nothing for unknown orders.
- **Review — behaviour change:** `INT96` (and INTERVAL) statistics no longer decode (`MinValue` null): their order is
  undefined, but someone may read them today. The API surface is a design discussion for upstream.

## Evaluation

- **Without the change:** `TypedStatisticsTest` does not compile.
- **With it:** 725 passed (the whole suite, when the unit was verified on upstream `658e1ca` with 7 + 8 + 9 applied first).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0019-typed-statistics-api` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

INT96 and INTERVAL statistics no longer decode (`MinValue` is null): their order is undefined.
