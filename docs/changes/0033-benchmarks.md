---
title: "Benchmarks of the calls Parquet.SourceGenerator's generated code makes"
kind: addition
status: fork-only
depends_on: []
---

# 0033 Benchmarks of the calls Parquet.SourceGenerator's generated code makes

## What and why

`eng/lake/Lake.Parquet.Benchmarks` (BenchmarkDotNet): million-row columns read and written through the calls
generated readers and writers make: required columns through `ReadRawAsync` into a reused buffer, nullable ones
through `ReadAsync` into `T?[]` (doubles with and without nulls, microsecond timestamps, `decimal(18,2)`), strings
as `ReadOnlyMemory<char>` with definition levels then a UTF-8 length check and a string each, and the same strings
as their stored UTF-8 bytes (`lake(0017)`; fork only, upstream rejects it). Writes go into one reused stream so
what is measured is the library.

The `ParquetProject` property builds it against another checkout, so the same benchmarks compare the fork with
upstream (`docs/testing.md`, "Benchmarks"). Performance changes (`evidence: benchmark`) cite these numbers.

## Evaluation

Its first run found that reading a Snappy column (the default codec) allocated about three times the page size
per read, and writing strings, binaries, dates or times allocated 32 bytes per value: `lake(0035)`, `lake(0036)`.

## Upstream

Not for upstream: it lives in the fork's `eng/lake/`.

## Compatibility

None: tooling only.
