---
title: "Snappy, gzip and LZ4 pages read into rented memory; Zstd compressed into it"
kind: code
status: fork-only
depends_on: [0011]
evidence: benchmark   # a performance change: its tests guard behaviour and pass either way
---

# 0035 Snappy, gzip and LZ4 pages read into rented memory; Zstd compressed into it

## What and why

- **Problem:** Snappy (the default codec), gzip and LZ4 decompression read each compressed page with
  `Stream.ToByteArray`, which grows a `MemoryStream` and copies it out: about three times the page in garbage for
  every page read. Zstd compression let ZstdSharp allocate the output per page, then copied it.
- **Change:** the page is read into a rented buffer (`ReadRestAsync`, as Zstd decompression already did since
  `lake(0011)`); Snappy decompresses into a buffer of the length its header gives; gzip reads from a stream over the
  rented buffer; Zstd compresses straight into rented memory of `GetCompressBound` size, and Snappy into rented
  memory of `GetMaxCompressedLength` size (`CompressToMemory` zeroed its buffer on every page).

## Evaluation

- **Allocated** per read of 1M required `int64` (one page): Snappy 22.8 MB to 0.01 MB; gzip and LZ4 also 0.01 MB.
  Per write, Zstd: 8.05 MB to 0.01 MB.
- **Time** (`Lake.Parquet.Benchmarks`, Snappy): `RequiredInt64` 16.1 to 11.7 ms; `NullableDouble_10pcNulls` 18.7 to
  14.9 ms; `NullableDouble_NoNulls` 18.8 to 11.8 ms; `NullableTimestampMicros` 33.3 to 24.1 ms. Allocated per read:
  about 22 MB to 9 KB in each.
- **Tests:** the suite's compression tests (every codec, both page versions) and the conformance harness (every
  codec in both directions) guard behaviour.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0035-rented-decompression-buffers`.

## Compatibility

None.
