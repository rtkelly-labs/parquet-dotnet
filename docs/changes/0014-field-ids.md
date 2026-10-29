---
title: "Field IDs on every field, read back"
kind: code
status: fork-only
depends_on: [0003]
upstream_unit: "11"
---

# 0014 Field IDs on every field, read back

A feature: unit 11 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** `927bf99`.
- **Problem:** `DataField.FieldId` was written but never read (a file written with `FieldId = 7` reads back −1), and
  structs, lists and maps had none.
- **Change:** `FieldId` moved from `DataField` to `Field`; decoded for every field; written on list, map and struct
  groups.
- **Review — compatibility:** source-compatible (`DataField` inherits it). Binary compatibility of moving a property
  to a base class should hold (member references resolve through base types) but I have not tested an assembly
  compiled against the old version. Check `Field.Equals` does not need to consider it.

## Evaluation

- **Without the change:** `FieldIdTest` does not compile.
- **With it:** 699 passed (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0014-field-ids` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

`FieldId` moves from `DataField` to `Field` (source compatible); files now read field IDs back.
