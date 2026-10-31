---
title: "Uncalled delta variants and page-index and bloom-filter thrift codecs left out of the package"
kind: addition
status: fork-only
depends_on: [0050, 0027]
---

# 0051 Uncalled delta variants and page-index and bloom-filter thrift codecs left out of the package

## What and why

More code nothing in the package calls, left out by the hashed manifest under Lake.NET decision 6, as `lake(0050)`
did for the big-endian bit-packing kernels:

- **Delta variants for `short`, `ushort` and `uint`** (`Encodings/DeltaBinaryPackedEncoder.Variations.cs`, generated
  from its `.tt`): `EncodeShort`, `DecodeShort`, `FlushShortBlock` and the same for `Ushort` and `Uint`, 9 members.
  `lake(0027)` routes these types through the `int` kernels (the format computes INT32 deltas in 32-bit two's
  complement, which the narrow kernels did not), so nothing calls them since; neither does any test.
- **Thrift codecs of the page index and bloom filters** (`Meta/Parquet.cs`): `Read` and `Write` of `ColumnIndex`,
  `OffsetIndex`, `PageLocation`, `BloomFilterHeader` and its `BloomFilterAlgorithm`, `SplitBlockAlgorithm`,
  `BloomFilterHash`, `XxHash`, `BloomFilterCompression` and `Uncompressed`, 20 members. The library neither reads nor
  writes page indexes or bloom filters (the footer's offsets to them are kept as numbers), and these structures are
  referenced only by one another. The public classes stay, so the API is unchanged; only their internal codecs go.
  When the page index or bloom filters are implemented (Lake.NET's roadmap), their entries leave the manifest in the
  same change.

The entries were generated from the source and hashed with `StripMembers.cs --hashes`. A hash that changes or a
caller that appears fails the Lake build.

## Evaluation

- **Lake build:** 244 members left out in all; `Lake.Parquet` builds with every warning an error under
  `IsAotCompatible`; `VerifyLakeAssembly` passes (234 types); the renamed test run passes (680 on net8.0 and net10.0,
  1 skipped as before); the native AOT suite builds.
- **Coverage of what ships:** the 29 members were 495 instrumented lines (286 delta, 209 thrift), none of which any input could reach.

## Upstream

Not for upstream: upstream keeps the code.

## Compatibility

None: every member left out is internal and uncalled.
