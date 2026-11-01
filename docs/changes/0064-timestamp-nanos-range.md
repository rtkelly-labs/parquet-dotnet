---
title: "A DateTime outside a nanosecond TIMESTAMP's range is refused, not wrapped"
kind: code
status: fork-only
depends_on: [0055]
evidence: failing-test
---

# 0064 A DateTime outside a nanosecond TIMESTAMP's range is refused, not wrapped

## What and why

Lake.NET decision 9: no value is changed unless asked for.

- **Problem:** `ToUnixNanoseconds` multiplied a `DateTime`'s ticks by 100 in unchecked arithmetic. A signed 64-bit
  count of nanoseconds since 1970 reaches only 1677-09-21 00:12:43 to 2262-04-11 23:47:16 UTC. Outside that range the
  product wrapped and was written as another date: 1600-01-01 read back as 2184-07-20, and 9999-12-31 as 1816-03-29.
  Found while writing Lake.NET's type mapping reference (`docs/lake-parquet/types/datetime.md`).
- **Change:** the conversion works from ticks since 1970. A value whose nanosecond count would not fit 64 bits throws
  `ArgumentOutOfRangeException`, giving the range and pointing to microseconds or milliseconds, which hold every
  `DateTime`.

## Evaluation

- **Tests** (`Types/TimestampNanosRangeTest`, 9):
  - 1600-01-01, 9999-12-31, and the days just outside each end are refused;
  - the first and last representable seconds, the epoch, and the tick before it round-trip;
  - 1600-01-01 round-trips in microseconds.
- **Without the change:** the four out-of-range cases fail.
- **Suite:** 995 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

Upstream has the same conversion. A person may propose it from `lake/change/0064-timestamp-nanos-range`.

## Compatibility

A write that produced a wrong date now throws. Values in range are unchanged.
