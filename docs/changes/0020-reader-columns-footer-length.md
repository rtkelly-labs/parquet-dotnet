---
title: "`ParquetReader.Columns`, `FooterLength`"
kind: code
status: fork-only
depends_on: []
upstream_unit: "17"
---

# 0020 `ParquetReader.Columns`, `FooterLength`

A feature: unit 17 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** `da1574b`. A `ColumnDescriptor` per leaf, straight from the thrift schema.
- **Review:** the name `ColumnDescriptor` matches ParquetSharp's; check it does not clash with anything upstream.

## Evaluation

- **Without the change:** `ColumnDescriptorTest` does not compile.
- **With it:** 697 passed (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0020-reader-columns-footer-length` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
