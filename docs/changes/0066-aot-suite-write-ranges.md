---
title: "The native AOT suite covers refused writes and the default decimal"
kind: addition
status: fork-only
depends_on: [0048, 0064, 0065]
---

# 0066 The native AOT suite covers refused writes and the default decimal

## What and why

`lake(0064)` and `lake(0065)` add refusals the native AOT suite (`lake(0048)`) did not reach. A measurement of
upstream's tests found one behaviour only they covered: the DECIMAL a bare `DataField<decimal>` writes. This adds all
three to the suite's `api` mode:

- **Nanosecond timestamps:** a TIMESTAMP(NANOS) of 1600-01-01 is refused, and the last representable second
  round-trips.
- **Decimal precision:** a decimal wider than its column's precision is refused on INT32, INT64 and
  FIXED_LEN_BYTE_ARRAY.
- **The default decimal:** a bare `DataField<decimal>` writes FIXED_LEN_BYTE_ARRAY(16) DECIMAL(38,18), and its values
  round-trip.

## Evaluation

- **Native AOT, linux-x64:** the suite publishes with every trimming and AOT warning an error, and `api` passes all
  its checks, the new ones included.

## Upstream

Not applicable: the suite is the fork's.

## Compatibility

Test-only.
