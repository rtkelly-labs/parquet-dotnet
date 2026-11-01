---
title: "A decimal with more fractional digits than its column's scale is refused unless rounding is asked for"
kind: code
status: fork-only
depends_on: [0058, 0065]
evidence: failing-test
---

# 0072 A decimal with more fractional digits than its column's scale is refused unless rounding is asked for

## What and why

Lake.NET decision 9, as amended on 1 November 2026: a value the file cannot hold exactly is refused on write rather
than changed, even where upstream changes it.

- **Problem:** a `decimal` with more fractional digits than its column's scale was truncated without a word:
  `DecimalDataField("x", 9, 2)` wrote `1.239m` as 1.23. Upstream does this on purpose and tests it.
  `lake(0065)` left it in place.
- **Change:**
  - `DataColumnWriter` checks every `decimal` written to a column: if rounding it to the column's scale would change
    it, the write throws `ParquetException`, naming the value, the column's precision and scale, and the option.
    Trailing zeros are not digits lost (`1.2300m` fits decimal(5,2)).
  - New option `ParquetOptions.RoundDecimalsToScale` (default `false`) restores upstream's truncation.
  - `BigDecimal` values carry their own scale and are unchanged.

## Evaluation

- **Tests** (`Types/DecimalWriteFitTest`):
  - digits beyond the scale are refused on INT32, INT64 and FIXED_LEN_BYTE_ARRAY, and the message names the option;
  - with the option, they are truncated as before.
- **Without the change:** the refusal cases fail.
- **Upstream test changed:** `EndToEndTypeTest.Type_nullable_decimal_writes_and_reads` asserts upstream's truncation
  (10.44 into decimal(4,1) reads 10.4). It now passes `RoundDecimalsToScale = true`, so it still tests that
  behaviour.
- **Suite:** 1,084 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

A behaviour change upstream chose against. Not for upstream unless a person proposes the option alone.

## Compatibility

A write that truncated now throws, unless `RoundDecimalsToScale` is set. Values that fit their column are unchanged.
