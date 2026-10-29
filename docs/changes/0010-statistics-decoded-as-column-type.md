---
title: "Statistics decoded as the column's type"
kind: code
status: fork-only
depends_on: []
upstream_unit: "9"
---

# 0010 Statistics decoded as the column's type

A bug fix: unit 9 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `35bd5c1` (`ParquetPlainEncoder.TryDecode`).
- **Problem:** `DataColumnStatistics.MinValue/MaxValue` of decimals stored as INT32/INT64 came back as raw unscaled
  integers (the decimal branch was unreachable after the INT32/INT64 branches), unsigned ints as their signed bits
  (a `uint64` max of 2^64−1 as −1), UUIDs (FLBA) as nothing. The UUID part moved to unit 7's patch, since it
  only round-trips with unit 7's byte order.
- **Review — behaviour change:** callers that worked around the old types (e.g. divided by the scale themselves) will
  see different values. Upstream may want this called out in release notes.

## Evaluation

- **Without the change:** 7 of 7 in `StatisticsDecodingTest` fail.
- **With it:** 703 passed (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0010-statistics-decoded-as-column-type` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

`DataColumnStatistics.MinValue`/`MaxValue` change type for decimals stored as INT32/INT64 and for unsigned integers (see Review notes).
