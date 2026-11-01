---
title: "Columns in optional structs and lists are written and read through the typed API"
kind: code
status: fork-only
depends_on: [0039, 0049]
evidence: failing-test
---

# 0062 Columns in optional structs and lists are written and read through the typed API

## What and why

- **Problem:** the typed API (`WriteAsync`, `ReadAsync`) failed on most nested columns. The internal raw reader
  worked, which is why upstream's own nested tests pass. Found by the .NET library cross-check's AOT probe, and the
  same under the JIT:
  - writing values without nulls (`long[]`) to a column under an optional struct or list threw
    `InvalidOperationException: Definition levels are not ready yet`;
  - `ReadAsync<T>(field, T?[], repetitionLevels)` sized its definition levels, and its loop, by the row group's rows.
    A repeated column has one level per value slot, more than it has rows, so it threw
    `Definition levels buffer is too small … (length 2 < numValues 2)`. The message printed the rows, not the slots;
  - the `string` and `byte[]` overloads did the same, and also took a value to be null only at definition level 0. A
    null element in a list (level 2 of 3) read the next value instead. They also skipped levels for a required
    column under an optional parent, so reading one threw.
- **Change:**
  - `WritingColumn` fills definition levels with the column's maximum whenever values come without them and the
    column has levels (every value is present), as it already did for legacy arrays.
  - The three read helpers allocate and loop over the column chunk's `num_values`, read levels whenever the column
    has them (`MaxDefinitionLevel > 0`, not `IsNullable`), and take a value to be null when its level is below the
    maximum.
  - `ReadRawAsync`'s buffer-size messages print `num_values`.

## Evaluation

- **Tests** (`Types/NestedTypedApiTest`, 4), all failing without the change:
  - `long[]` and a required string under an optional struct round-trip;
  - a list of nullable ints (`[1, null, 3], [], [4]`) round-trips with its repetition levels;
  - lists of strings and byte arrays with a null element and a null list read null at both levels;
  - `list_empty_alt.parquet` (an existing fixture) reads through the typed `string` overload.
- **Native AOT:** the cross-check probe's struct and list columns now write and read in the published binary.
- **Suite:** 972 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

The same failures are in Parquet.Net 6.1.0. A person may propose it from
`lake/change/0062-nested-columns-through-the-typed-api`.

## Compatibility

Calls that threw now work. A flat column's behaviour is unchanged: there, `num_values` is the row count.
