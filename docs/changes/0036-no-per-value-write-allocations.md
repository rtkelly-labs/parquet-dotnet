---
title: "No allocation per value when writing strings, binaries, dates and times"
kind: code
status: fork-only
depends_on: []
evidence: benchmark   # a performance change: its tests guard behaviour and pass either way
---

# 0036 No allocation per value when writing strings, binaries, dates and times

## What and why

- **Problem:** plain encoding wrote each string's and byte array's length prefix, and each date, timestamp and time
  value, through `BitConverter.GetBytes`: a new array per value (32 bytes of garbage) and, outside
  strings, a stream write per value. The dictionary encoder's table was presized for every row, about 32 bytes a
  row, though a dictionary is only kept when few values are distinct.
- **Change:** a small buffered `ValueWriter` writes the values little-endian into a rented buffer and flushes it in
  blocks (INT96 timestamps, written by `NanoTime`, flush first); string length prefixes are written in place; the
  dictionary table starts at 1,024 entries and grows.

## Evaluation

- **Allocated** per write of 1M strings (1,000 distinct, 10% null), any codec: 28.9 MB to 0.05 MB.
- **Time and allocation** (`Lake.Parquet.Benchmarks`, write): see the numbers below.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0036-no-per-value-write-allocations`.

## Compatibility

None: the bytes written are the same.
