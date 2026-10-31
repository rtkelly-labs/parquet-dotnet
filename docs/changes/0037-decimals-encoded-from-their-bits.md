---
title: "INT32/INT64-backed decimals encoded from their bits"
kind: code
status: fork-only
depends_on: [0036]
evidence: benchmark   # a performance change: its tests guard behaviour and pass either way
---

# 0037 INT32/INT64-backed decimals encoded from their bits

## What and why

- **Problem:** each decimal was encoded as `(long)(value * (decimal)Math.Pow(10, scale))`: a decimal
  multiplication, a rounding and a conversion per value, then a stream write per value. In a profile of writing
  1M `decimal(18,2)` values they were 40% of the time.
- **Change:** `ParquetPlainEncoder.ToUnscaled` reads the value's bits; when it has no more decimal places than the
  column and fits in 64 bits (the usual case) the unscaled value is its mantissa times a power of ten from a table,
  an integer multiplication. Anything else (more places than the column, which truncates, or too large) takes the
  decimal arithmetic as before, with the same `ParquetException` on overflow. Values go through the buffered
  `ValueWriter` of `lake(0036)`.

## Evaluation

- **Tests:** `DecimalEncodeTest`: against the decimal arithmetic for edge values (negative zero, truncation,
  18-digit and overflowing values) and 100,000 random decimals and scales.
- **Profile harness** (`profile write-decimal`, 1M `decimal(18,2)`, 10% null, Snappy): 85 ms to 54 ms per write.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0037-decimals-encoded-from-their-bits`.

## Compatibility

None: the same bytes for every value.
