---
title: "INT96 columns as BigInteger are written and read"
kind: code
status: fork-only
depends_on: []
evidence: failing-test
---

# 0053 INT96 columns as BigInteger are written and read

## What and why

- **Problem:** with `ParquetOptions.TreatBigIntegersAsDates = false`, INT96 columns map to `System.Numerics.BigInteger`,
  and `DataField<BigInteger>` declares one. But `ParquetPlainEncoder` encoded `BigInteger` values with
  `MemoryMarshal.AsBytes` and decoded them through the generic `Decode<T>`, which does the same. `BigInteger` holds an
  array, so both throw `ArgumentException: Cannot use type 'System.Numerics.BigInteger'. Only value types without
  pointers or references are supported` (and had the cast been allowed, it would have written the struct's fields,
  a reference among them, rather than the number). Neither writing nor reading such a column ever worked. Found by
  the native AOT suite's coverage checks.
- **Change:** `Encode(ReadOnlySpan<BigInteger>)` writes each value as INT96's 12 bytes of little-endian two's
  complement (`BigInteger.TryWriteBytes`, sign-extended; a value that does not fit throws `ParquetException`), and a
  `Decode(Span<byte>, Span<BigInteger>)` overload, which overload resolution picks over the generic one, reads them
  back with `new BigInteger(12 bytes)`.

## Evaluation

- **Test:** `Types/Int96BigIntegerTest` round-trips 0, 1, -1, 2^90 and -2^95 through an INT96 column: without the change
  the write throws; with it the values read back and the column is INT96.
- **Suite:** 898 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

A bug in upstream's code (the same encoder is in aloneguid/parquet-dotnet). Not yet proposed: a person proposes it
from `lake/change/0053-int96-bigintegers`.

## Compatibility

Writes and reads that threw now work.
