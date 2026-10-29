---
title: "Timestamp statistics in the column's unit"
kind: code
status: fork-only
depends_on: []
upstream_unit: "1"
---

# 0002 Timestamp statistics in the column's unit

A bug fix: unit 1 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `7ecfa00` (`ParquetPlainEncoder.TryEncode(DateTime…)`, `TimestampStatisticsTest`).
- **Problem:** min/max of an `INT64` timestamp column are always encoded as milliseconds, whatever the column's unit.
  A `TIMESTAMP(MICROS)` file has bounds 1000× too small, so engines that prune on statistics skip row groups that
  match. Repro: write `DateTimeDataField(... unit: Micros)` with 1970-01-01T00:00:02, then in DuckDB
  `SELECT count(*) FROM 'f.parquet' WHERE ts > TIMESTAMP '1970-01-01 00:00:01'` returns 0 (1 after the fix).
- **Change:** the statistics bytes are produced by the same `Encode(ReadOnlySpan<DateTime>…)` the values go through.
- **Review:** `INT96` and `INT32` (date) paths unchanged; check `TIMESTAMP(NANOS)` too (the test covers millis,
  micros and nanos).

## Evaluation

- **Without the change:** `TimestampStatisticsTest` fails.
- **With it:** pass (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0002-timestamp-statistics-unit` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
