---
title: "ParquetOptions.UnadjustedTimestampsAsWallClock: TIMESTAMP columns not UTC-adjusted hold wall-clock times"
kind: code
status: fork-only
depends_on: [0058, 0072]
evidence: failing-test
---

# 0073 ParquetOptions.UnadjustedTimestampsAsWallClock: TIMESTAMP columns not UTC-adjusted hold wall-clock times

## What and why

Lake.NET decision 9, as amended on 1 November 2026: wall-clock semantics are asked for, and the default stays
upstream's.

- **Problem:** a TIMESTAMP with `isAdjustedToUTC = false` is a wall-clock time with no zone (parquet-format
  LogicalTypes.md: "local semantics"). The writer still converted a `DateTimeKind.Local` value to UTC: 12:00 in New
  York was stored as 17:00. The reader returned every value as `Local`. A caller had no way to store or read a wall
  clock as it is. Found while writing Lake.NET's type mapping reference (`docs/lake-parquet/types/datetime.md`).
- **Change:** new option `ParquetOptions.UnadjustedTimestampsAsWallClock` (default `false`, upstream's behaviour).
  When set, for TIMESTAMP columns that are not UTC-adjusted:
  - **on write**, `DataColumnWriter` takes each `DateTime`'s ticks as they are, whatever its `Kind`, before anything
    else sees them. Values, statistics, the page index and bloom filters all hold the wall clock;
  - **on read**, values come back with `DateTimeKind.Unspecified`;
  - UTC-adjusted columns are unaffected.

## Evaluation

- **Tests** (`Types/WallClockTimestampTest`, 5), run under UTC and under America/New_York:
  - `Local`, `Utc` and `Unspecified` values of the same clock time are all stored as that time (checked through the
    physical `long`) and read back `Unspecified`;
  - statistics hold the same value, and the bloom filter holds the stored value;
  - milliseconds and nanoseconds work too;
  - without the option, a `Local` value is still converted to UTC and read back `Local`;
  - a UTC-adjusted column is unchanged by the option.
- **Without the change:** the tests do not compile (no option).
- **Suite:** 1,089 passed, 3 skipped, on net8.0 and net10.0, under America/New_York.

## Upstream

A person may propose it from `lake/change/0073-wall-clock-timestamps-option`.

## Compatibility

None unless asked: with the option off, timestamps are written and read as before.
