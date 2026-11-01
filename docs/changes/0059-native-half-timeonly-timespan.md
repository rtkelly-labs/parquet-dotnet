---
title: "Half for FLOAT16, and TimeOnly and TimeSpan for TIME, through the core API"
kind: code
status: fork-only
depends_on: [0002, 0003, 0007, 0008, 0010, 0012, 0014, 0015, 0016, 0017, 0018, 0022, 0023, 0024, 0034, 0036, 0037, 0038, 0040, 0043, 0047, 0052, 0053, 0058]
evidence: failing-test
---

# 0059 Half for FLOAT16, and TimeOnly and TimeSpan for TIME, through the core API

## What and why

Lake.NET decision 9: .NET types with a matching Parquet type are supported through the core API, losslessly by
default; a mapping that could lose precision or change what existing code reads is asked for.

- **Problem:** the core API had no way to read or write FLOAT16 as `System.Half` (`lake(0052)` made such files
  readable, as two bytes per value), and none to read or write TIME as `TimeOnly` or `TimeSpan`: upstream reads TIME as
  the unit's integer (its 7b6d8a98, "may lose precision"), `DataField<TimeSpan>` is refused and `DataField<TimeOnly>`
  is a plain `long`. Upstream's `TimeOnly`/`TimeSpan` encoders were left behind for the reflection serializer, and
  ignored the NANOS unit (TIME in nanoseconds was written as microseconds).
- **Change:**
  - **FLOAT16 ↔ `Half`.** `DataField<Half>` writes FIXED_LEN_BYTE_ARRAY(2) with the FLOAT16 logical type (the
    half's IEEE 754 bits, little-endian), and FLOAT16 columns read as `Half` (the two bytes stay available through the
    physical type, `lake(0017)`). Statistics follow the floating-point rules (NaN never a bound but counted, a zero
    min -0 and max +0) and decode as `Half`.
  - **TIME ↔ `TimeOnly` / `TimeSpan`, asked for.** `new TimeDataField(name, precision, typeof(TimeOnly))` (or
    `typeof(TimeSpan)`) writes TIME in that unit from those values; `ParquetOptions.TimeMapping` (`Integer`, the
    default and upstream's, `TimeOnly` or `TimeSpan`) says what TIME columns, legacy `TIME_MILLIS`/`TIME_MICROS`
    included, read as, statistics too. A `TimeSpan` written to TIME must be a time of day (0 to under 24 h) or is
    refused. Nanoseconds are written as ticks × 100 and read rounded down to a tick, the one lossy step, only when
    asked for. `DataField<TimeOnly>` is unchanged.
  - The encoders store TIME in INT64 in the column's unit, micro- or nanoseconds, instead of always microseconds.

## Evaluation

- **Conformance against parquet-cpp, both directions:** `lake(0060)` adds the types to the conformance harness
  (2,000 cases each way pass) and so to the native AOT suite.
- **Tests** (`Types/NativeTypesTest`, 13): `Half` round-trips bit for bit (NaN, -0, ∞), its statistics are halves
  with the NaN counted and zero bounds signed; `TimeOnly` and `TimeSpan` round-trip in each unit, read as the unit's
  integer without the mapping; a `TimeSpan` outside a day is refused; `TimeDataField` refuses other types and
  `DataField<TimeOnly>` is still `long`; a legacy `TIME_MILLIS` column reads as `TimeOnly`; and pyarrow's time32[ms],
  time64[us], time64[ns] and float16 (`data/time_and_half.parquet`) read natively, nanoseconds rounded down to ticks.
  `Reader/Float16Test` now reads `float16.parquet` as `Half`, and as its bytes through the physical type.
- **Suite:** 960 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

Upstream chose integers for TIME; `Half` for FLOAT16 is what its schema code intended. A person may propose the
`Half` part, and the TIME mapping as an option, from `lake/change/0059-native-half-timeonly-timespan`.

## Compatibility

FLOAT16 columns read as `Half` rather than two-byte arrays (`lake(0052)`, unreleased). Nothing else changes unless
asked for: TIME still reads as integers and `DataField<TimeOnly>` is still `long`.
