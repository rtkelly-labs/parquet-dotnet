---
title: "Required maps written `OPTIONAL`"
kind: code
status: fork-only
depends_on: []
upstream_unit: "3"
---

# 0003 Required maps written `OPTIONAL`

A bug fix: unit 3 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `02925d8` (`SchemaEncoder.Encode(MapField…)`, one test in `RawDataFieldTest`).
- **Problem:** `new MapField(..., isNullable: false)` is encoded `OPTIONAL` while its levels are computed as required:
  the levels written disagree with the file's own schema.
- **Change:** repetition from `mapField.IsNullable`, as lists and structs already do.
- **Review:** the test uses `RawDataField` (unit 18); rewrite it with ordinary `DataField`s for an upstream PR.

## Evaluation

- **Without the change:** `RequiredMapTest` fails.
- **With it:** pass (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0003-required-map-repetition` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
