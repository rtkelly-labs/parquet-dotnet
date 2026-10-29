---
title: "`ColumnDescriptor.SortOrder`"
kind: code
status: fork-only
depends_on: [0019, 0020]
upstream_unit: "17b"
---

# 0021 `ColumnDescriptor.SortOrder`

A feature: unit 17b of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Change:** `ColumnDescriptor.SortOrder`, the sort order of each leaf column, from its type and the file's `column_orders`. Split from unit 17 because it needs unit 16's public `ColumnSortOrder`.

## Evaluation

- **Without the change:** `ColumnDescriptor.SortOrder` does not compile.
- **With it:** 788 passed, net8.0 and net10.0 (the whole suite, when the unit was verified on upstream `658e1ca` with 16 + 17 (made on all units but 21) applied first).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0021-column-sort-order` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
