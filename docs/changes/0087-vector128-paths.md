---
title: "Booleans bit-pack with 128-bit vectors; BYTE_STREAM_SPLIT floats and doubles decode unrolled everywhere"
kind: code
status: fork-only
depends_on: [0026]
evidence: benchmark
---

# 0087 Booleans bit-pack with 128-bit vectors; BYTE_STREAM_SPLIT floats and doubles decode unrolled everywhere

## What and why

- **Problem:** the library's two intrinsics paths were gated on 256- and 512-bit vectors. Native AOT's default
  instruction set (x64: SSE4.2 and POPCNT, no AVX2) accelerates only `Vector128`: measured, a default `PublishAot`
  binary reports `Vector256.IsHardwareAccelerated` false, and `IlcInstructionSet=x86-64-v3` or `-v4` is needed for more.
  Arm64's vectors are 128 bits under the JIT too. On all of these:
  - **Boolean PLAIN encoding** fell back to the per-bit scalar loop;
  - **`BYTE_STREAM_SPLIT` decoding** of floats and doubles fell back to the generic per-value decoder. Its
    "Vector256" paths use no vector instructions at all: they are unrolled scalar deinterleaves, and the gate kept
    them from everyone without AVX2.
- **Change:**
  - `ParquetPlainEncoder.EncodeHwx(bool)` gains a `Vector128` branch after its 512- and 256-bit ones. It compares 16
    booleans at a time with zero, takes their sign bits as two bytes, and writes all the bits to one pooled buffer
    and the stream once.
  - `ByteStreamSplitEncoder.DecodeHwx` takes its unrolled float and double decoders on every instruction set.

## Evaluation

- **BenchmarkDotNet** (`eng/lake/Lake.Parquet.Benchmarks`, `VectorPathBenchmarks`, a million values, uncompressed),
  fork against `master` at `8b7364fa`, linux-x64, .NET 10:

  | `DOTNET_EnableAVX2=0` (128-bit vectors, as native AOT's default and Arm64) | master | fork |
  |---|---|---|
  | WriteBooleans | 4,731 µs | **104.9 µs** (45×) |
  | ReadByteStreamSplitFloats | 9,981 µs | **5,259 µs** (1.9×) |
  | ReadByteStreamSplitDoubles | 12,866 µs | **6,950 µs** (1.85×) |

  | AVX2 on | master | fork |
  |---|---|---|
  | WriteBooleans | 227.0 µs | 226.9 µs |
  | ReadByteStreamSplitFloats | 5,206 µs | 5,070 µs |
  | ReadByteStreamSplitDoubles | 7,239 µs | 7,089 µs |

  Allocations are unchanged.
- **Native AOT, default instruction set**, a stopwatch probe before the change (two million values): writing a
  boolean column took 10.66 ms against 0.68 ms with `IlcInstructionSet=x86-64-v3`; reading a byte-stream-split float
  column took 20.3 ms against 9.8 ms. This change is what closes that gap for default AOT builds.
- **Correctness:**
  - the suite passes with AVX2 on and with `DOTNET_EnableAVX2=0`, which routes booleans through the new branch.
    There, `lake(0085)`'s tests compare it with the scalar encoder for 1 to 1,031 values;
  - byte-stream-split round trips cover the decoders.
- **Suite:** 1,266 passed, 3 skipped, on net10.0, both ways.

## Upstream

Upstream has the same gates. A person may propose it from `lake/change/0087-vector128-paths`, with the benchmarks.

## Compatibility

None: the bytes written are the same.
