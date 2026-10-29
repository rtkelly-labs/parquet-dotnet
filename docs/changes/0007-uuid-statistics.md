---
title: "UUID statistics byte order and ordering"
kind: code
status: fork-only
depends_on: []
upstream_unit: "7"
---

# 0007 UUID statistics byte order and ordering

A bug fix: unit 7 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `7ecfa00` (`TryEncode(Guid)`: `ToByteArray(bigEndian: true)`) and part of `f12c75b`
  (`StatsCompute.Compute(ReadOnlySpan<Guid>…)` compares big-endian bytes).
- **Problem:** UUID bounds were written in .NET's mixed-endian `Guid` layout while the values use RFC 4122 big-endian
  bytes, and min/max were chosen with `Guid.CompareTo`, which is not byte order either.
- **Change:** bounds written and chosen in big-endian byte order, and read back (`TryDecode`, UUID part of `35bd5c1`)
  as the `Guid`s they are, so a round trip gives the values written.
- **Review:** `UuidStatisticsTest` and `RawDataFieldTest.Strings_compare_by_code_point_and_uuids_by_their_bytes`.

## Evaluation

- **Without the change:** 3 of 3 in `UuidStatisticsTest` fail.
- **With it:** 699 passed (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0007-uuid-statistics` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

UUID statistics change byte order; files written before keep the old, wrong bounds.
