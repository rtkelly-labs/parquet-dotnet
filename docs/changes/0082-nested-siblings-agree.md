---
title: "Nested leaves that disagree with their siblings are refused on write"
kind: code
status: fork-only
depends_on: [0024, 0078, 0079]
evidence: failing-test
---

# 0082 Nested leaves that disagree with their siblings are refused on write

## What and why

Lake.NET decision 11: a write whose sibling leaves disagree on their shared ancestors' levels is refused, since
nothing else would catch it.

- **Problem:** the leaves of a map (its keys and its values), of a struct (its fields), or under a shared list each
  carry their own definition and repetition levels. Nothing checked that they describe the same rows. Two leaves
  whose levels describe different pairs, or a struct null in one field's levels and present in another's, were
  written. The file was corrupt, and readers either failed on it or returned wrong data. The low-level write calls
  (`WriteAllPartsAsync`, and `WriteAsync` with repetition levels) are what generated code uses, so a generator bug
  would pass silently.
- **Change:** `ParquetRowGroupWriter` takes the schema from its `ParquetWriter`. For each leaf with group ancestors
  it builds the leaf's `NestedLayout` (`lake(0079)`) and compares it with the first leaf written under each group
  ancestor it shares:
  - the offsets of each list or map level they share must be equal;
  - the validity of each optional ancestor they share must be equal.

  On a difference it throws `ParquetException`, naming both columns and the ancestor. Columns with no group ancestor
  skip the check. Levels that cannot belong to the leaf at all are refused by `NestedLayout`.

## Evaluation

- **Tests** (`Types/NestedSiblingsTest`, 4):
  - pyarrow's deeply nested file (`data/nested/nested_layout.parquet`), every leaf's levels and values written again,
    passes the check;
  - a map's values describing different pairs from its keys are refused, naming `m/key_value`;
  - a struct's fields disagreeing on where it is null are refused;
  - a list of structs whose leaves sit at different depths (one under an inner list) agrees on what they share and
    is written.
- **Without the change:** the two refusals fail.
- **Suite:** 1,174 passed, 3 skipped, on net8.0 and net10.0. The conformance harness and the native AOT suite write
  nested columns through this check in CI.

## Upstream

A person may propose it with `lake(0079)`.

## Compatibility

Writes that made corrupt files now throw. Consistent writes are unchanged.
