---
title: "A DECIMAL read as System.Decimal is exact or refused, never rounded"
kind: code
status: fork-only
depends_on: []
evidence: failing-test
---

# 0061 A DECIMAL read as System.Decimal is exact or refused, never rounded

## What and why

Lake.NET decision 9: mappings are lossless by default.

- **Problem:** a FIXED_LEN_BYTE_ARRAY or BYTE_ARRAY DECIMAL read as `System.Decimal` was converted as integer part
  plus fraction, added in `decimal` arithmetic. A value with more significant digits than `decimal` holds (96 bits)
  had its fraction rounded away with no error: DECIMAL(38,9) `12345678901234567890123456789.123456789` read as
  `12345678901234567890123456789`. A value whose integer part was beyond `decimal` threw a bare `OverflowException`
  that did not say what to do. Found by the .NET library cross-check: ParquetSharp refuses the first value; Polars
  reads it exactly.
- **Change:** `BigDecimal.ToSystemDecimal` drops the fraction's trailing zeros from the unscaled integer and, if the
  rest fits 96 bits at a scale of at most 28, builds the `decimal` from those bits exactly. Otherwise it throws
  `OverflowException` naming the value and `ParquetOptions.UseBigDecimal`. INT32 and INT64 decimals always fit and
  are unchanged.

## Evaluation

- **Tests** (`Types/DecimalPrecisionTest`, 8), fixture `data/decimal_38_9.parquet` (pyarrow 26, decimal128(38,9)):
  - values that fit read exactly, negatives and a null included;
  - a value beyond `decimal` throws, and the message names `UseBigDecimal`;
  - a 38-digit value written by the library throws rather than rounding, and reads exactly with `UseBigDecimal`;
  - `decimal.MaxValue`, its negative, 96 bits at scale 28, the smallest scale-28 step and 12.5 round-trip.
- **Without the change:** the overflow message test and the rounding test fail.
- **Suite:** all `Decimal` tests pass (169), on net8.0 and net10.0.

## Upstream

Upstream has the same conversion. A person may propose it from `lake/change/0061-decimal-exact-or-refused`.

## Compatibility

A read that silently rounded now throws. Exact values are unchanged; a FIXED_LEN_BYTE_ARRAY decimal comes back
without trailing fractional zeros (`1.5`, not `1.500000000`), as upstream's arithmetic already returned.
