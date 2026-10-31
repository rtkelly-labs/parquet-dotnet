---
title: "Uncalled big-endian bit-packing kernels left out of the package"
kind: addition
status: fork-only
depends_on: [0045]
---

# 0050 Uncalled big-endian bit-packing kernels left out of the package

## What and why

`Encodings/BitPackedEncoder.cs` is generated from `BitPackedEncoder.tt` with one pack and one unpack routine per bit
width (0 to 32 for `int`, 0 to 64 for `long`) in two bit orders. Little-endian (LSB-first) is the order every current
encoding uses: the RLE/bit-packed hybrid (levels, dictionary indexes) and DELTA_BINARY_PACKED. Big-endian
(MSB-first) is only for the deprecated `BIT_PACKED` encoding (4), which the library never writes and whose pages the
reader refuses (`DataColumnReader`: `case Encoding.BIT_PACKED: // 4 (deprecated)` throws "not supported"; levels are
always decoded as the RLE hybrid). Nothing in the library calls the big-endian routines; only upstream's own encoder
test does. They were 6,632 lines, a third of the package, that no consumer can run.

Lake.NET decision 6 (as extended) leaves code nothing in the package calls out of it, the way `lake(0045)` leaves out
what only the serializer used. This change adds the 204 big-endian members (the eight `*8ValuesBE` dispatchers and
the 196 width-specific `Pack8*ValuesBE*` / `Unpack8*ValuesBE*` routines) to `eng/lake/strip-members.json`, each with
its syntax hash, and the two big-endian cases of upstream's `BitPackedEncoderTest`, so the renamed test run keeps
that test's little-endian cases. Upstream's files are untouched; the plain build and its suite keep everything.

The entries were generated from the source (every `static` member whose name contains `ValuesBE`) and hashed with
`dotnet run eng/lake/StripMembers.cs -- . <entries> --hashes`, never written by hand. If upstream changes one of them
the hash fails the Lake build; if anything starts calling one, the Lake build fails to compile.

## Evaluation

- **Lake build:** 215 members left out; `Lake.Parquet` builds with no warning as an error under `IsAotCompatible`;
  `VerifyLakeAssembly` passes (234 types, all under `Lake.Parquet`); the renamed test run passes (680 on net8.0 and
  net10.0, 1 skipped as before).
- **Native AOT:** the suite publishes with no trimming or AOT warning and passes `api` (979 of 979).
- **Coverage of what ships**, by the native suite's modes on the JIT build (`dotnet-coverage`, assembly
  instrumented): 83.3% of the package's lines (11,450 of 13,746), from 56.2% (11,450 of 20,378). The covered lines
  are the same; the uncalled ones are no longer shipped.

## Upstream

Not for upstream: upstream keeps the routines, and its own test covers them.

## Compatibility

None: the members were internal (`BitPackedEncoder` is an internal class) and nothing called them.
