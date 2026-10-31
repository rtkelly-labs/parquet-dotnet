---
title: "Timestamps and dates decoded straight to ticks"
kind: code
status: fork-only
depends_on: []
evidence: benchmark   # a performance change: its tests guard behaviour and pass either way
---

# 0038 Timestamps and dates decoded straight to ticks

## What and why

- **Problem:** INT64 timestamps were decoded per value through the logical type's property chain (unit and UTC flag
  looked up for every value), then `epoch.AddMilliseconds(double)`, `AddTicks` and `DateTime.SpecifyKind`; INT32
  dates through `AddDays(double)`. In a profile of reading 1M microsecond timestamps the decode loop was a third of
  the time.
- **Change:** the unit and kind are read once per page, and each value becomes `new DateTime(epochTicks + value *
  ticksPerUnit, kind)` (nanoseconds divided by 100, truncating as before), checked so that out-of-range values
  still throw.

## Evaluation

- **Tests:** `TimestampDecodeTest`: every unit, UTC-adjusted or not, and the legacy converted types, against the
  previous formula, before and after the epoch, kinds included.
- **Profile harness** (`profile read-timestamp`, 1M nullable microsecond timestamps, with `lake(0039)`): 25.5 ms to
  17.4 ms per read.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0038-timestamps-decoded-to-ticks`.

## Compatibility

A value outside `DateTime`'s range throws `OverflowException` where it threw `ArgumentOutOfRangeException`. Valid
values decode to the same `DateTime` and kind.
