---
title: "Lake.Parquet ships without the reflection-based serializer"
kind: addition
status: fork-only
depends_on: []
---

# 0042 Lake.Parquet ships without the reflection-based serializer

## What and why

Upstream's `ParquetSerializer` maps classes to columns at run time with reflection and compiled expression
trees (`Serialization/ParquetSerializer.cs`, `Serialization/ParquetClrSchemaExtensions.cs`,
`Serialization/Dremel/`, `Extensions/ExpressionExtensions.cs`: about 2,600 lines). Parquet.SourceGenerator does
the same job at compile time, and neither it nor Lake.NET calls the serializer, so carrying it means maintaining,
testing and shipping code no consumer of the fork uses, and the part of the library the trimming and AOT analyzers
object to most.

`eng/lake/Lake.Parquet.targets` leaves those files out of the Lake build, as it renames the assembly: upstream's
files stay untouched, so rebaselines never conflict on them. `eng/lake/Strip/` holds the one method the rest of
the library used from them (`TimeUnit.Convert`, for `SchemaEncoder`). The attribute types in
`Serialization/Attributes/` stay, since Parquet.SourceGenerator recognises them by name.

- **Tests:** the renamed run leaves out the serializer's tests and the files that use it; a new `suite` job in
  `lake.yml`, required by `lake-ci`, runs the whole suite on the plain build, so tests of other code in those
  files (encryption round trips, the test-file corpus, legacy lists) still run against the fork.
- **Gate:** `VerifyLakeAssembly.cs` fails if a serializer type is in the package or a serialization attribute is
  missing.
- **Escape hatch:** `-p:LakeKeepSerializer=true` builds the package with it, to compare.

`lake(0004)` and the name-matching half of `lake(0018)` fix the serializer only: they stay, as fixes proposed for
upstream, but no longer reach the package.

## Evaluation

- The package: 245 types where it had 290 (net10.0); the renamed suite runs 633 tests, the plain suite all 843.
- Trimming and AOT analyzers (`-p:IsAotCompatible=true`): 14 findings in five files, from 76 in ten. Left:
  `ParquetRowGroupReader.ReadRawColumnDataBaseAsync` and its writer counterpart (`MakeGenericMethod`), `FileMerger`,
  `MapField`, and the CLR type helpers in `Extensions/ParquetClrSchemaExtensions.cs`.

## Upstream

Not for upstream: a packaging decision of the fork's (`docs/vision.md`, "Narrower than upstream in one place").

## Compatibility

Code that uses `ParquetSerializer`, `ParquetClrSchemaExtensions` (schema from a CLR type) or the Dremel types does
not compile against Lake.Parquet; it should stay on Parquet.Net. The reader, writer, schema and every other API
are unchanged.
