[![AI Assisted](https://img.shields.io/badge/development-AI%20assisted-792ee5.svg?logo=openai&logoColor=white)](AI_POLICY.md)

# Lake.Parquet

**Lake.Parquet is a fork of [Parquet.Net](https://github.com/aloneguid/parquet-dotnet)**, the fully
managed Apache Parquet library by Ivan Gavryliuk ([@aloneguid](https://github.com/aloneguid)) and its
contributors. Almost all of the code here is theirs. This fork is upstream's history, unchanged, with a
short stack of recorded changes on top, rebaselined onto a newer upstream when the maintainer decides to. It publishes the result
to NuGet as [`Lake.Parquet`](https://www.nuget.org/packages/Lake.Parquet), in the `Lake.Parquet`
namespace.

If you do not need one of the changes listed in [`docs/changes/`](docs/changes/), use
[Parquet.Net](https://www.nuget.org/packages/Parquet.Net). It is the original and the better-supported
choice.

## Attribution and licence

Parquet.Net is MIT licensed. [`LICENSE`](LICENSE) is upstream's, unchanged, with upstream's
copyright, and it ships inside the `Lake.Parquet` package. The package's authors field credits
upstream first. Upstream's own documentation is in [`docs/README.md`](docs/README.md) and applies to
Lake.Parquet unchanged apart from the namespace.

## What the fork is for

[Lake.NET](https://github.com/rtkelly-labs/Lake.NET) (Apache Iceberg and DuckLake for .NET) needs a
fully managed Parquet library, and upstream Parquet.Net cannot yet write or read what Iceberg requires.
The changes it carries fall into three groups:

- **Iceberg needs**: field IDs on every field, read back from files (Iceberg resolves columns by ID,
  so nested columns are impossible without them); physical values (lossless `timestamp_ns`, unscaled
  decimals); `fixed[n]`; Parquet modular encryption; typed statistics in each type's sort order; the
  legacy list shapes the Iceberg spec requires readers to accept.
- **Bugs found on the way** that make other engines read Parquet.Net's files wrongly: timestamp
  statistics in the wrong unit (DuckDB prunes away matching rows), UUID statistics in the wrong byte
  order, `List<T?>` losing nulls, `DATA_PAGE_V2` with repetition levels, and more.
- **Performance**: Zstd decompression without double copies, and byte-array and text values decoded
  into one array per page (reads of string and UUID columns about twice as fast). Found by Lake.NET's
  backend benchmark against ParquetSharp, and next by
  [Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator)'s cross-library
  benchmarks, whose open items (exact decimals on .NET 11, a fast path for nullable columns without
  nulls, page checksums, bounded allocations) are listed in its `UPSTREAM_DEPENDENCY_LIMITATIONS.md`
  and come here as changes once they are made and measured.

Each change is:

- **Evaluated against upstream**: tested or benchmarked against the same code without it.
- **Recorded** in [`docs/changes/`](docs/changes/), with whether it has gone upstream.
- **Retired** once upstream has it or an equivalent, whoever raised it there.

The aim is for the list to shrink, not grow. [`docs/vision.md`](docs/vision.md) has the reasoning
and the boundaries.

## Using it

```bash
dotnet add package Lake.Parquet
```

```csharp
using Lake.Parquet;
using Lake.Parquet.Serialization;
```

The API is upstream's plus the recorded changes, minus the reflection-based serializer (below). Code
written against Parquet.Net's reader, writer and schema moves across by changing `Parquet` to
`Lake.Parquet` in its `using` directives. `Lake.Parquet.Serialization` keeps only the attribute types,
which [Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator) recognises. The assembly is `Lake.Parquet.dll`, so
both packages can be referenced side by side, for example to compare them.

Versions follow their own semver, starting at 0.1.0. Each release is a tag, `lake-v<version>`, and
its notes name the upstream commit it is based on.

## How the history is kept

`master` is a **rebased stack**, not a merge history:

```text
aloneguid/parquet-dotnet master   A - B - C - ... - U          upstream's commits, as upstream wrote them
rtkelly-labs/parquet-dotnet master                    \- 0001 - 0002 - ... - 0025
                                                         packaging   one commit per change
```

- **Upstream's history is preserved exactly.** Every upstream commit is in `master` with its own hash,
  author and date; nothing upstream wrote is squashed or rewritten.
- **The fork's own work is one commit per change, on top.** `lake(0001)` is the packaging, CI and docs;
  each later `lake(NNNN)` commit is one change, with its record in `docs/changes/` in the same commit.
  `git log upstream/master..master` is therefore the complete list of what Lake.Parquet adds.
- **The stack is rebaselined deliberately.** Moving onto a newer upstream is a choice the maintainer
  makes, because upstream's changes can break the changes on top: `lake-stack rebaseline` rebases the
  stack, folds in fixups, checks it and runs the whole test suite locally, then stages it on a
  `staging/<name>` branch (`staging/v0.1.1`) for CI to build as it builds `master`. Only once that
  build is green does `--push` promote it to `master`. A change upstream has taken drops out of the stack by itself, and the report says so.
  Nothing rebases on a schedule.
- **Nothing is lost when `master` moves.** Before a rebaseline is published the previous head is tagged
  `lake/stack/<date>-<sha>`, and every release is tagged `lake-v<version>`, so a pinned commit stays
  reachable.

Every change also exists as a branch `lake/change/NNNN-name`: upstream plus that change (and only
the changes it depends on), rebuilt at every rebaseline. Its diff against upstream is exactly what an
upstream pull request would contain.

[`docs/maintaining.md`](docs/maintaining.md) is the guide to this structure: every ref and what
moves it, how to rebaseline onto upstream, re-basing other branches onto a new stack with one command
(the `rewire` command of [`eng/lake/Lake.Stack`](eng/lake/Lake.Stack), the C# tool that runs
the whole process), and resolving a rebase that stops.
[`docs/changes/README.md`](docs/changes/README.md) has the rules for changes and their records.

## How it is tested

Beyond upstream's suite and each change's own tests, a conformance harness generates random schemas, values and
writer options and checks every file against parquet-cpp in both directions (what the library writes, and what
parquet-cpp writes for the library to read), statistics and encryption included; and `lake-stack verify` proves
each change alone on upstream. [`docs/testing.md`](docs/testing.md) has every layer and how to run it.

## How Lake.Parquet is built

The source keeps upstream's namespaces, project names and files: renaming them in the tree would
make every upstream rebase conflict across hundreds of files. The rename happens in CI
([`.github/workflows/lake.yml`](.github/workflows/lake.yml)), on a throwaway copy:

1. [`eng/lake/RenameNamespace.cs`](eng/lake/RenameNamespace.cs) rewrites namespace declarations,
   `using` directives and `global::` names from `Parquet` to `Lake.Parquet`, as syntax rather than
   text, and fails if any survive.
2. [`eng/lake/Lake.Parquet.targets`](eng/lake/Lake.Parquet.targets) is injected into the build
   through MSBuild's `CustomBefore…` hooks. It sets the assembly name, package id, version and
   metadata, and the package README, and leaves the serializer out (below), without editing
   upstream's project file.
3. [`eng/lake/VerifyLakeAssembly.cs`](eng/lake/VerifyLakeAssembly.cs) reads the built assemblies'
   metadata and fails if any namespace is not under `Lake.Parquet`, if any serializer type is in
   them, or if a serialization attribute is missing.
4. Upstream's own test suite, with every change's tests, runs against the renamed build (less the
   serializer's tests), and then the package is packed and its contents checked. The `suite` job runs
   the whole suite, serializer included, on the plain build.

### What Lake.Parquet leaves out

Upstream's `ParquetSerializer` maps classes to columns at run time with reflection and compiled
expression trees: `Serialization/ParquetSerializer.cs`, `Serialization/ParquetClrSchemaExtensions.cs`,
`Serialization/Dremel/` and `Extensions/ExpressionExtensions.cs`, about 2,600 lines. Parquet.SourceGenerator
does the same at compile time, and neither it nor Lake.NET calls the serializer, so the Lake build does
not compile those files. They stay in the tree as upstream wrote them, so a rebaseline never conflicts
on them. [`eng/lake/Strip/`](eng/lake/Strip/) holds the one method the rest of the library used from
them. The attribute types in `Serialization/Attributes/` stay.

With the serializer gone, the trimming and AOT analyzers (`-p:IsAotCompatible=true`) report 14
findings in five files, where they reported 76: the untyped `ReadRawColumnDataBaseAsync` and its writer
counterpart, `FileMerger`, `MapField`, and the CLR type helpers in `Extensions/ParquetClrSchemaExtensions.cs`.
`-p:LakeKeepSerializer=true` builds the package with the serializer, to compare.

Upstream's `build.yml` stays as upstream wrote it. `lake-ci` is the fork's own required check.

## Contributing

- A new change is a pull request whose squash commit is titled `lake(NNNN): title` and that adds its
  record `docs/changes/NNNN-name.md` ([format](docs/changes/README.md)).
- A fix to an existing change is a pull request titled `fixup! lake(NNNN): title`; the next rebaseline
  folds it into that change's commit.
- Prefer upstream. If a change is useful to everyone, it belongs in Parquet.Net; the record links the
  upstream issue or PR.
- **Upstream does not accept AI-generated issues or pull requests**, and blocks accounts that send
  them. Work on this fork can be AI-assisted (see [`AI_POLICY.md`](AI_POLICY.md)). Anything raised
  upstream has to be written by a person.

---

## 🤖 AI Utilization & Transparency

This project leverages AI-assisted software engineering and agentic workflows for rapid development, test scaffolding, and documentation.

* **Human Accountability:** The maintainer reviews all architecture, code changes, and pull requests, assuming 100% responsibility for the correctness, stability, and security of all merged code.
* **Automated Verification:** All features and contributions are validated by automated test suites (.NET 8 & .NET 10), strict type checking, and automated CI pipelines.
* **Community Policy:** For contributor guidelines regarding generative AI tooling, see [**`AI_POLICY.md`**](AI_POLICY.md).
