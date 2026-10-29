---
title: "Legacy list shapes and name-matched deserialization"
kind: code
status: fork-only
depends_on: [0004]
upstream_unit: "15"
---

# 0018 Legacy list shapes and name-matched deserialization

A feature: unit 15 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** parts of `ff03dcc` (`SchemaEncoder.TryBuildList`/`TryBuildStruct`, `ParquetSerializer.MakeForReading`
  and `LogicalPaths`, `LegacyListTest`, `data/legacy/*.parquet`).
- **Problem:** the backward-compatibility rules 2 and 3 of LogicalTypes.md (a repeated group with several fields, or
  one field and named `array` or `<list>_tuple`, *is* the element) were not applied; and when a file's list group
  names differ from the class's (`array`, `bag`), `ParquetSerializer` found no column and silently left the property
  null.
- **Review — behaviour change:** for such files the decoded schema changes from `List<int>` (the inner field as element)
  to `List<struct { item }>` (the spec's reading). Code that relied on the old shape breaks; it was wrong per the
  spec, but upstream must agree. The name fallback in `MakeForReading` is a second, separable PR.

## Evaluation

- **Without the change:** 4 of 6 in `LegacyListTest` fail (the other 2 are shapes that already worked).
- **With it:** 705 passed (the whole suite, when the unit was verified on upstream `658e1ca` with 4 applied first).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0018-legacy-list-shapes` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

Files with legacy list shapes decode to the spec's reading, which differs from upstream's (see Review notes).
