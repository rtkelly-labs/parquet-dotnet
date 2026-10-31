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
- **Benchmarks** (`Lake.Parquet.Benchmarks`, `WriteBenchmarks.Strings_HighCardinality`, 1M strings, 500,000
  distinct, 10% null, into a reused stream): upstream 381 ms and 38.2 MB allocated, the fork with this change
  340 ms and 10.0 MB (what is left is the definition levels and the page, in rented memory that the run's
  first pass allocates). Required and nullable numeric writes allocate 8 KB either way.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0036-no-per-value-write-allocations`.

## Compatibility

None: the bytes written are the same.
