# Lake.Parquet: Vision

## Why the fork exists

Parquet.SourceGenerator and Lake.NET both read and write Parquet through Parquet.Net. Building them
keeps turning up things that are best fixed in the library itself: allocation amplification from
untrusted footer counts, a nullable read path with no fast route when a chunk has no nulls, page
checksums that are never verified, UTF-8 columns that can only be read as `string`, and, from .NET 11,
decimals that decode with the error of a `double`. Parquet.SourceGenerator's cross-library benchmarks
also show how much faster a decoder can be (Polars decodes the same files in about a third of the
time).

Upstream is the right home for all of it. But upstream decides its own priorities and does not
accept AI-assisted contributions, and the projects that need these fixes cannot wait for them. The
fork is where a fix can be made, measured and shipped, while it is offered upstream.

## What it is

- **Upstream, plus a short list of recorded changes.** Every line that differs from upstream belongs
  to a change in [`changes/`](changes/), with what it does, why, how it was evaluated against
  upstream, and its upstream status.
- **Compatible with upstream by default.** Same API, same behaviour, same file format. A change that
  alters behaviour says so in its record, and the fork's compatibility suite runs the same tests
  against both libraries, so a difference is a decision, never a surprise.
- **Built to be retired.** A change is written as if it were going upstream: minimal, in upstream's
  style, without fork-only abstractions. When upstream has it, or solves the same problem another
  way, the change is removed and so is its record, whoever raised it upstream.
- **A separate package and namespace.** `Lake.Parquet` can sit beside `Parquet.Net` in one process,
  which is what makes side-by-side compatibility tests and benchmarks possible, and nobody gets it by
  accident.

## What it is not

- Not a rewrite, and not a competitor. It does not add features upstream would not take, and it does
  not reformat, restructure or modernise upstream's code.
- Not a place for consumer-specific behaviour. Anything that only Parquet.SourceGenerator or Lake.NET
  needs stays in those projects.
- Not permanent. A fork that only grows has failed; success is an empty `changes/` directory.

## How a change gets in

1. **Evidence first.** A failing test or a benchmark against upstream (Parquet.SourceGenerator's
   cross-library suite, or a test here) shows the problem.
2. **The smallest fix**, in upstream's style, on its own `lake/change/` branch cut from upstream, so
   the branch is exactly what an upstream PR would contain, with a record.
3. **Evaluated against upstream**: the same tests or benchmarks, before and after, in the record.
4. **Offered upstream** where it is generally useful, by a person (upstream's rules), or noted as
   proposed when someone else has raised the same thing. Others may well reach the same conclusion
   independently; the record tracks the outcome either way.
5. **Retired** when upstream releases it.

## Relationship to the other projects

| Project | Relationship |
|---|---|
| [aloneguid/parquet-dotnet](https://github.com/aloneguid/parquet-dotnet) | Upstream. Merged in, never rewritten. |
| [Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator) | Finds problems and measures fixes (cross-library benchmarks, regression suite). May consume Lake.Parquet. |
| [Lake.NET](https://github.com/rtkelly-labs/Lake.NET) | Apache Iceberg for .NET. May consume Lake.Parquet. |
| [rig](https://github.com/rtkelly13/rig) | Governs the fork: merge policy, change records, attribution and sync (`docs/forks.md`). |
