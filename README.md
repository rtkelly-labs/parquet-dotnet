[![AI Assisted](https://img.shields.io/badge/development-AI%20assisted-792ee5.svg?logo=openai&logoColor=white)](AI_POLICY.md)

# Lake.Parquet

**Lake.Parquet is a fork of [Parquet.Net](https://github.com/aloneguid/parquet-dotnet)**, the fully
managed Apache Parquet library by Ivan Gavryliuk ([@aloneguid](https://github.com/aloneguid)) and its
contributors. Almost all of the code here is theirs. This fork carries upstream unchanged, plus a
small set of improvements that were each measured against upstream and recorded, and publishes the
result to NuGet as [`Lake.Parquet`](https://www.nuget.org/packages/Lake.Parquet), in the
`Lake.Parquet` namespace.

If you do not need one of the changes listed in [`docs/changes/`](docs/changes/), use
[Parquet.Net](https://www.nuget.org/packages/Parquet.Net). It is the original and the better-supported
choice.

## Attribution and licence

Parquet.Net is MIT licensed. [`LICENSE`](LICENSE) is upstream's, unchanged, with upstream's
copyright, and it ships inside the `Lake.Parquet` package. The package's authors field credits
upstream first. Upstream's own documentation is in [`docs/README.md`](docs/README.md) and applies to
Lake.Parquet unchanged apart from the namespace.

## What the fork is for

The fork exists so that improvements found while building
[Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator) and
[Lake.NET](https://github.com/rtkelly-labs/Lake.NET) can ship before, or without, upstream taking
them. Each one is:

- **Evaluated against upstream**: benchmarked or tested against the same code without it.
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

The API is upstream's. Code written against Parquet.Net moves across by changing `Parquet` to
`Lake.Parquet` in its `using` directives. The assembly is `Lake.Parquet.dll`, so both packages can be
referenced side by side, for example to compare them.

Versions follow their own semver, starting at 0.1.0. Each release notes the upstream commit it is
based on.

## How Lake.Parquet is built

The source in this repository keeps upstream's namespaces, project names and files. Renaming them in
the tree would make every upstream merge conflict across hundreds of files. The rename happens in
CI ([`.github/workflows/lake.yml`](.github/workflows/lake.yml)), on a throwaway copy:

1. [`eng/lake/RenameNamespace.cs`](eng/lake/RenameNamespace.cs) rewrites namespace declarations,
   `using` directives and `global::` names from `Parquet` to `Lake.Parquet`, as syntax rather than
   text, and fails if any survive.
2. [`eng/lake/Lake.Parquet.targets`](eng/lake/Lake.Parquet.targets) is injected into the build
   through MSBuild's `CustomBefore…` hooks. It sets the assembly name, package id, version and
   metadata, and the package README, without editing upstream's project file.
3. [`eng/lake/VerifyLakeAssembly.cs`](eng/lake/VerifyLakeAssembly.cs) reads the built assemblies'
   metadata and fails if any namespace is not under `Lake.Parquet`.
4. Upstream's own test suite runs against the renamed build, and then the package is packed and its
   contents checked.

Upstream's `build.yml` stays as upstream wrote it and still runs on every PR. `lake-ci` is the
fork's own required check.

## Keeping up with upstream

Upstream is merged in with a merge commit through a sync PR (rig's `repo-governance fork-sync`). It
is never rebased or squashed, so the fork's history keeps upstream's commits and the next sync brings
only the difference. The fork's own changes are squash-merged, one PR and one record each. The rules
are in rig's [`docs/forks.md`](https://github.com/rtkelly13/rig/blob/main/docs/forks.md), and
`repo-governance fork-status` checks them.

## Contributing

- Every change on top of upstream comes through a PR with a record in `docs/changes/`
  ([format](docs/changes/README.md)).
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
