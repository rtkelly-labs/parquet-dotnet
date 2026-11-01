---
title: "A decimal wider than its column's precision is refused, not written into a file that breaks its schema"
kind: code
status: fork-only
depends_on: [0037]
evidence: failing-test
---

# 0065 A decimal wider than its column's precision is refused, not written into a file that breaks its schema

## What and why

- **Problem:** the writer checked a decimal only against its physical type, not its column's DECIMAL(precision, scale).
  `DecimalDataField("x", 5, 2)` accepted `12345.6m` and wrote the unscaled 1234560, seven digits in a column declared
  to hold five. The library reads that back, but the file breaks its own schema, and engines that check precision
  reject or misread it. Only a value too wide for the physical type (`decimal.MaxValue` in an INT64) was refused.
  Found while writing Lake.NET's type mapping reference (`docs/lake-parquet/types/decimal.md`).
- **Change:** every `decimal` written to a DECIMAL column, on INT32, INT64 or FIXED_LEN_BYTE_ARRAY, and statistics
  values, must have at most precision − scale integer digits. Otherwise it throws `ParquetException`, as the
  physical-type overflow already did.
- **Not changed:** digits beyond the scale are still truncated (`1.239m` in decimal(9,2) is 1.23). That is upstream's
  behaviour, which upstream tests (`EndToEndTypeTest.Type_nullable_decimal_writes_and_reads`), and decision 9 keeps
  upstream's default where changing it would change existing results. Making it strict is an open question in
  `types/decimal.md`.

## Evaluation

- **Tests** (`Types/DecimalWriteFitTest`, 14):
  - too many integer digits are refused on INT32, INT64 and FIXED_LEN_BYTE_ARRAY, including decimal(30,30) of 1;
  - truncation beyond the scale is unchanged;
  - edge values that fit round-trip.
- **Without the change:** the five refusal cases fail.
- **Upstream test changed:** `EndToEndTypeTest.Type_int64_decimal_writes_and_reads` wrote 1234567.88 (seven integer
  digits) into decimal(17,12), which holds five. Its value is now 12345.88. It still tests an INT64-backed decimal of
  the same column.
- **Suite:** 995 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

Upstream writes such files. A person may propose it from `lake/change/0065-decimal-precision-enforced`.

## Compatibility

A write that produced an invalid file now throws. Values that fit their column are unchanged.
