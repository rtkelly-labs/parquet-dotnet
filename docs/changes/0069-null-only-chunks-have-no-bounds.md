---
title: "A column chunk of nulls only is written without statistics bounds, not min = max = 0"
kind: code
status: fork-only
depends_on: [0008, 0059]
evidence: failing-test
---

# 0069 A column chunk of nulls only is written without statistics bounds, not min = max = 0

## What and why

- **Problem:** for a chunk whose values are all null, integer, decimal and INT96 statistics took the min and max of
  no values, which came out as the type's default: min = max = 0. The footer told every reader that the column
  holds 0. An engine answering `min(x)` from statistics answers 0 for a column of nulls. Pruning on `x = 0` keeps the
  row group, and pruning on `x > 0` drops a group it should drop either way, so only the claimed values are wrong. The
  format says a chunk with no non-null values has no bounds. Affected: `sbyte`, `byte`, `short`, `ushort`, `int`,
  `uint`, `long`, `ulong`, decimals on INT32, INT64 and FIXED_LEN_BYTE_ARRAY, and `BigInteger` on INT96. The
  floating-point, boolean, string, timestamp and UUID paths already left the bounds out. Found by a measurement of what
  the regression suite covers: no test wrote a chunk of nulls only.
- **Change:** `StatsCompute.Compute<T>` leaves the bounds unset for no values, as the other paths do. The null count
  is unchanged.

## Evaluation

- **Tests** (`Types/NullOnlyChunkStatisticsTest`, 15):
  - each affected type, plus double, timestamp and UUID as controls, writes a chunk of three nulls;
  - the footer has no `min_value`, `max_value`, `min` or `max`, and a null count of 3;
  - the typed statistics have no bounds.
- **Without the change:** the twelve affected types fail.
- **Suite:** all pass on net8.0 and net10.0.

## Upstream

Upstream writes the zeros. A person may propose it from `lake/change/0069-null-only-chunks-have-no-bounds`.

## Compatibility

Files with null-only chunks lose bounds that were wrong. Nothing else changes.
