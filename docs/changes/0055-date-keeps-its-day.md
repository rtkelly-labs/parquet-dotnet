---
title: "DateTime written as DATE keeps its date before 1970 and just before midnight"
kind: code
status: fork-only
depends_on: []
evidence: failing-test
---

# 0055 DateTime written as DATE keeps its date before 1970 and just before midnight

## What and why

- **Problem:** `OtherExtensions.ToUnixDays(DateTime)` returned `(int)(value - UnixEpoch).TotalDays`. A cast truncates
  toward zero, so for any instant before 1970 that is not midnight the day count moves toward the epoch: 1969-07-20
  20:17 is -163.15 days, stored as -163, read back as 1969-07-21. And `TotalDays` is a `double`: 2024-02-29
  23:59:59.9999999 is 19782.9999999999988 days, which rounds to 19783.0, the next day. Values and statistics of
  `DATE` columns (and `DateTimeFormat.Date` fields) written from `DateTime` were a day out in both cases. Found by the
  native AOT suite's checks of every `DateTimeDataField` format.
- **Change:** the day count is taken from the value's date alone (`value.Date - UnixEpoch`), a whole number of days,
  so the cast is exact on both sides of 1970.

## Evaluation

- **Test:** `Types/DateBeforeEpochTest` writes five instants (1969-07-20 20:17:40, 1969-12-31 00:00:01, 1900-01-01
  06:00, 2024-02-29 23:59:59.9999999 and 12:00) as `DATE` and reads them back: without the change four come back a day
  late; with it all keep their date.
- **Suite:** 903 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

A bug in upstream's code (the same method is in aloneguid/parquet-dotnet). Not yet proposed: a person proposes it from
`lake/change/0055-date-keeps-its-day`.

## Compatibility

Dates written from a `DateTime` with a time of day before 1970, or within 100 ns of midnight, are stored as their own
date rather than the next one.
