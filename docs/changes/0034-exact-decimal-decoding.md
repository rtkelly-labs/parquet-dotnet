---
title: "Exact, cheaper decoding of INT32/INT64-backed decimals"
kind: code
status: fork-only
depends_on: [0010]
evidence: benchmark   # exact on net8/net10 either way; the inexactness is on later runtimes
---

# 0034 Exact, cheaper decoding of INT32/INT64-backed decimals

## What and why

- **Problem:** INT32/INT64-backed decimals were decoded as `unscaled * (decimal)Math.Pow(10, -scale)`, one decimal
  multiplication per value by a power of ten converted from double. That conversion was exact only while the
  runtime rounded double-to-decimal conversions to 15 significant digits; later runtimes convert exactly, so the
  factor carries the double's error into every value. Parquet.SourceGenerator works around it with its own
  `ParquetDecimalScale`, which this makes unnecessary.
- **Change:** `ParquetPlainEncoder.Unscaled` builds the decimal from the value's bits and the column's scale,
  `new decimal(lo, mid, 0, negative, scale)`, for whole pages and for single values (statistics).
- **Review:** `long.MinValue` is handled without overflow (magnitude computed as `-(v + 1) + 1` in unsigned). The
  scale of each value is the column's, as before (`0.00` stays `0.00`).

## Evaluation

- **Tests:** `ExactDecimalDecodeTest`: `Unscaled` against parsed literals (scales 0 to 28, `long.MinValue` and
  `MaxValue`), and round trips of INT32 and INT64 decimals at scales 0 to 18 keeping value, scale and text.
- **Performance** (`Lake.Parquet.Benchmarks`, `NullableDecimal18_2`, 1M rows, 10% nulls, with `lake(0035)`):
  39.4 ms before, 29.6 ms after.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0034-exact-decimal-decoding` (`docs/changes/upstreaming.md`).

## Compatibility

None: the same values on runtimes where the old path was exact.
