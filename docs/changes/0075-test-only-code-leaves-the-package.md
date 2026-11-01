---
title: "Code only tests use leaves the package; the manifest can name branches"
kind: addition
status: fork-only
depends_on: [0045, 0054]
---

# 0075 Code only tests use leaves the package; the manifest can name branches

## What and why

Lake.NET decision 6, as extended on 1 November 2026: a line the package ships is run by the regression suite, and
code only tests (or the left-out serializer) reach is left out.

- **`ParquetRowGroupReader.ReadRawColumnDataAsync` and `RawColumnData` leave the package.** The helper is internal,
  and only upstream's tests call it. `RawColumnData` is public, but nothing in the package produces one once the
  helper is gone: the serializer, `FileMerger` and `RawColumnData`'s write overloads are already left out.
  - The helper joins `eng/lake/strip-members.json`.
  - `Lake.Parquet.targets` leaves `Data/RawColumnData.cs` out of the package and compiles it into the renamed test
    assembly instead.
  - `src/Parquet.Test/LakeRawColumnDataReader.cs`, a fork-owned extension method built on the public `ReadRawAsync`,
    stands in for the helper there. In the plain build the library's own instance method wins, so the extension is
    never called.

  All 126 test cases that read through the helper keep running against the stripped package.
- **`StatsCompute.Compute(ReadOnlySpan<string>…)` leaves the package.** A `string` field's values are
  `ReadOnlyMemory<char>` in the core API, so only the serializer reached it. Its upstream test,
  `Extensions/StatsComputeTest.cs`, leaves the renamed run (as the serializer's tests did); the plain `suite` job
  keeps it.
- **`StripMembers.cs` can name branches.**
  - An entry with `"branch"` removes, from an otherwise kept member, the if statement with that condition (an else-if
    is spliced out of its chain; a leading if gives way to its else) or the switch section with those labels.
  - The whole member's hash still guards it.
  - The tool now refuses a manifest naming the same member or branch twice; one such duplicate entry, from this
    change's first draft, is gone.
  - Tried on RLE's width-0 encoder: upstream's `RleEncoderTest` encodes `{0}` at width 0, so that code is run and
    stays.

## Evaluation

- **The renamed build's suite** (CI's `build` job, run locally): 867 passed, 1 skipped, on net8.0 and net10.0, with
  267 members left out.
- **Branch stripping**, checked on a scratch file: an else-if spliced from its chain, a leading if replaced by its
  else, a lone if removed, a switch section removed, the code still compiling.
- **Native AOT:** unchanged; the suite does not use these members.

## Upstream

Not applicable.

## Compatibility

`RawColumnData` is no longer in Lake.Parquet. Nothing in the package returned one to a caller.
