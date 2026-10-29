---
title: "`List<T?>` deserialization drops nulls and empty lists"
kind: code
status: fork-only
depends_on: []
upstream_unit: "4"
---

# 0004 `List<T?>` deserialization drops nulls and empty lists

A bug fix: unit 4 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `ff03dcc` (`FieldAssemblerCompiler.cs`, `NullableListElementTest`).
- **Problem:** `ParquetSerializer` reads `[1, null, 2]` back as `[1, 2]` and `[]` as `null`, for `List<int?>`,
  `List<string?>` and `int?[]`, including files it wrote itself (the levels on disk are right).
- **Change:** the element exists from the repeated level (`ElementLevel`), and an element below the item's maximum
  definition level is added as `default` (null).
- **Review:** the expression-tree compiler is the most delicate code touched. Check non-nullable lists, arrays and
  nested lists of structs still assemble (the whole serializer suite passes). Check the untyped (`Dictionary`) path.

## Evaluation

- **Without the change:** `NullableListElementTest` fails.
- **With it:** pass (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0004-nullable-list-elements` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
