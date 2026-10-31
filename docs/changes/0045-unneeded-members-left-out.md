---
title: "Members only the serializer used left out of the package, by a hashed manifest"
kind: addition
status: fork-only
depends_on: [0042]
---

# 0045 Members only the serializer used left out of the package, by a hashed manifest

## What and why

`lake(0042)` left the reflection-based serializer's files out of Lake.Parquet. Some of what it used lives in files
the rest of the library needs, and `Utils/FileMerger.cs` was used by nothing else but its own test; all of it stayed
compiled, shipped and on the trimming/AOT analyzers' list.

- `Utils/FileMerger.cs` is left out with the serializer's files, and its test with theirs.
- `eng/lake/StripMembers.cs` (Roslyn, on the CI copy before the rename) removes the members named in
  `eng/lake/strip-members.json`: `ParquetRowGroupReader.ReadRawColumnDataBaseAsync` (the untyped raw read, through
  `MakeGenericMethod`), the writer's two `RawColumnData` overloads (`FileMerger`'s), `MapField.CreateSimpleDictionary`
  and five CLR type helpers. Each entry names the member by file, type, name and parameter types, with a hash of its
  syntax (whitespace and comments normalised): if upstream edits, renames or removes one, the build fails and names
  it, so nothing new is removed unseen. `--hashes` prints the current hashes after a review.
- `VerifyLakeAssembly.cs` fails if any of those members or `FileMerger` is in the package.

Upstream's files stay untouched, as for the serializer, so rebaselines never conflict on them.

## Evaluation

- The package: 234 types (net10.0), from 245 after `lake(0042)` and 290 before it. The renamed suite runs 634 tests;
  the plain suite still runs every test, these members' included.
- Trimming and AOT analyzers (`-p:IsAotCompatible=true`): 3 findings, from 12 after `lake(0042)` and 76 before it.
  All three are in the CLR type helpers `DataField` uses when it is built from a type.

## Upstream

Not for upstream: a packaging decision of the fork's (`docs/vision.md`).

## Compatibility

Code that calls `ReadRawColumnDataBaseAsync` or uses `FileMerger` does not compile against Lake.Parquet; the typed
`ReadRawColumnDataAsync<T>`, `ReadRawAsync<T>` and the rest of the reader and writer are unchanged.
