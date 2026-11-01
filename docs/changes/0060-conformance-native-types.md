---
title: "The conformance harness covers Half, TimeOnly and TimeSpan"
kind: addition
status: fork-only
depends_on: [0048, 0059]
---

# 0060 The conformance harness covers Half, TimeOnly and TimeSpan

## What and why

`lake(0059)` reads and writes FLOAT16 as `System.Half` and TIME as `TimeOnly` and `TimeSpan`. This adds them to the
conformance harness (`eng/lake/Lake.Parquet.Conformance`), so parquet-cpp checks them in both directions and the
native AOT suite, which shares the harness's generator, runs them natively:

- kinds `float16` (NaN, ±0, ±∞, the extremes and random values), `timeonly(millis|micros|nanos)` and
  `timespan(millis|micros)` (midnight, the last instant of the day in the unit, and random times), declared with
  `DataField<Half>` and `TimeDataField(name, unit, typeof(TimeOnly|TimeSpan))`;
- `Order.Float16`: bounds of FIXED_LEN_BYTE_ARRAY(2) compared as halves, NaN left out, a zero min -0 and max +0;
- TIME read back through the default integer mapping and converted, as DATE already is for `DateOnly`;
- the parquet-cpp columns for the reverse direction: FIXED_LEN_BYTE_ARRAY(2) with FLOAT16, and TIME (INT32
  milliseconds, INT64 micro- or nanoseconds), adjusted to UTC as `TimeDataField` writes it.

## Evaluation

2,000 generated cases written by the library, read by parquet-cpp with the same physical values and the library's
statistics checked, and 2,000 written by parquet-cpp, read back exactly: all pass, every shape and codec.

## Upstream

Not for upstream: the harness is the fork's.

## Compatibility

None: tests only.
