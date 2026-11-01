---
title: "Members the public API cannot reach left out of the package, found by the trimmer"
kind: addition
status: fork-only
depends_on: [0051]
---

# 0054 Members the public API cannot reach left out of the package, found by the trimmer

## What and why

Lake.NET decision 6 leaves code nothing in the package calls out of it. `lake(0050)` and `lake(0051)` did that for
members found by reading the code. This change finds them mechanically and leaves out 41 more (the thrift bool-list helpers it first found went back in with `lake(0057)`, whose column index uses them): ILLink, run on the
Lake build in library mode with only the public API as roots (the copy built without its `InternalsVisibleTo`
entries, so internals count only when public code reaches them), removes every method nothing public can reach.
Those methods, mapped back to their declarations by signature, are added to `eng/lake/strip-members.json` with their
syntax hashes:

- serializer-era helpers on `Field` (`BuildClrPath`, `NextDotPropertyPath`, `IsAtomic`, `IsAtomicFieldOrCollectionItem`,
  `IsCollection`, `Order`), on `ParquetClrSchemaExtensions` (CLR type inspection), on the time attributes, and in
  `OtherExtensions`, `StreamExtensions`, `TaskExtensions`, `ThriftExtensions` and `EncodingExtensions`;
- encoder overloads nothing dispatches to: `BitPackedEncoder.Encode8ValuesLE(Span<long>…)` and
  `Decode8ValuesLE(…Span<long>…)`, `DeltaBinaryPackedEncoder.CalculateBitWidth` for `short`, `ushort` and `uint`
  (`lake(0027)` routes them through `int`) and `CanEncodeULongArray`, `ParquetPlainEncoder.Encode(ReadOnlySpan<byte[]>…)`
  and `Decode(…Span<string>…)`;
- `DataColumnReader.GetColumnStatistics`, `MeteredWriteStream.TotalBytesWritten`,
  `RawColumnData`'s level-memory and `ValuesMemory` accessors.

Members the trimmer found unreachable that upstream's own tests call stay, so the renamed test run keeps all of
upstream's tests: `ParquetRowGroupReader.ReadRawColumnDataAsync` (a test helper used across the suite),
`ParquetSchema.Augment` and the `Rename` overrides it calls, `ThriftFooter.Parse` and `Write`,
`StatsCompute.Compute(ReadOnlySpan<string>…)`, `ParquetPlainEncoder.Decode(…Span<byte[]>…)`,
`BitPackedEncoder.Encode8ValuesLE(Span<int>…)` and `RawColumnData.ValuesAsStrings`. So does the generic
`RawColumnData<T>` constructor (leaving it out would leave its field unassigned, an error) and the setter of
`WritingColumn.Statistics` (an accessor, not a member).

The fixup of `lake(0045)` in the same stack makes `StripMembers` keep the preprocessor directives a removed member
carries, which three of these needed.

## Evaluation

- **Lake build:** 287 members left out in all; `Lake.Parquet` builds with every warning an error under
  `IsAotCompatible`; `VerifyLakeAssembly` passes; the renamed test run passes (682 on net8.0 and net10.0, 1 skipped);
  the native AOT suite builds and passes `api` (1,000 of 1,000).
- **Coverage of what ships:** 89.4% of the package's lines (11,663 of 13,051), from 87.9% (11,663 of 13,276): the
  same lines covered, 225 unreachable ones no longer shipped.

## Upstream

Not for upstream: upstream keeps the code.

## Compatibility

None: every member left out is internal, and nothing public reaches it.
