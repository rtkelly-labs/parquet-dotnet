---
title: "The native AOT suite covers DECIMAL on BYTE_ARRAY, null-only chunks and long decimal columns"
kind: addition
status: fork-only
depends_on: [0048, 0069]
---

# 0070 The native AOT suite covers DECIMAL on BYTE_ARRAY, null-only chunks and long decimal columns

## What and why

A coverage measurement of the regression suite (the AOT modes plus the xunit suite on the stripped build) found
reachable layouts that nothing ran. This adds them to the native AOT suite's `api` mode:

- **DECIMAL(4,2) on BYTE_ARRAY:** `data/byte_array_decimal.parquet`, written by HVR 5.3.0 and in upstream's test data
  but read by no test, read as `decimal` and as `BigDecimal`.
- **Chunks of nulls only:** `int`, `uint`, `ulong`, decimal(18,2), `BigDecimal`, `Interval` and `byte[]` columns.
  Their values read back null, and their statistics have no bounds (`lake(0069)`).
- **Long decimal columns:** decimal(9,2) and decimal(18,2) columns of 100,000 values, longer than the encoder's write
  buffer.

## Evaluation

- **Native AOT, linux-x64:** the suite publishes with every trimming and AOT warning an error, and `api` passes all
  its checks, the new ones included.

## Upstream

Not applicable: the suite is the fork's.

## Compatibility

Test-only.
