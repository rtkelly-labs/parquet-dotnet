# Maintaining the fork

This repository is upstream Parquet.Net plus the review units in [../upstream-review.md](../upstream-review.md),
and nothing else: no rename, no Lake-specific code. It builds `Parquet.dll` under the `Parquet.Net` package id,
exactly as upstream does.

The fork is published as **Lake.Parquet**, but that identity is applied at build time by the
[Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator) repository (its `lake/` folder
and `.github/workflows/lake.yml`): it checks out a pinned commit of this repository, sets the package and assembly
name, `created_by` and the package metadata, builds Lake.Parquet and builds the source generator against it. Keeping
the identity out of here means:

- upstream merges never conflict with it;
- every unit's code here is exactly what is proposed upstream;
- consumers of the submodule (Iceberg.NET) build the same code as Lake.Parquet, under upstream's name.

Three things move:

- **upstream to the fork**: every upstream Parquet.Net release merged in;
- **the fork to upstream**: the units proposed one at a time, by hand, and dropped as they land;
- **the fork to Lake.Parquet**: Parquet.SourceGenerator's pin moved to a new fork commit, which releases it.

## Repository layout

| Remote | Repository | Role |
|---|---|---|
| `origin` | `rtkelly-labs/parquet-dotnet` | the fork; `master` is what Lake.Parquet is built from |
| `upstream` | `aloneguid/parquet-dotnet` | Parquet.Net; never pushed to, its PRs are opened by hand from branches here |

Keep the GitHub fork relationship: it is what lets a PR to upstream come from this repository.

`master` is **never rebased**: upstream comes in by merge commits, so the commits pinned by Parquet.SourceGenerator
and by Iceberg.NET's submodule stay valid.

Fork-only files, which upstream never touches and so never conflict: `docs/upstream-review.md`, `docs/upstream/`,
`docs/lake/`, `scripts/lake/` and `.github/workflows/lake-upstream-sync.yml`. Everything else is upstream's code with
the units applied.

## Upstream to the fork

Automatic: `.github/workflows/lake-upstream-sync.yml` runs every Monday (and on demand, for a given tag). It merges
new upstream commits into a `sync/upstream-<sha>` branch, records the new base, runs the tests and opens a pull
request with the patch report; on a merge conflict it opens an issue instead. Nothing reaches `master` without a
person merging the PR.

By hand, or to resolve a conflict:

```bash
scripts/lake/sync-upstream.sh            # latest upstream master
scripts/lake/sync-upstream.sh 6.2.0      # an upstream release tag
# on conflict: resolve with the rules below, git commit, then
scripts/lake/sync-upstream.sh --continue
```

The script merges, writes the upstream commit to `docs/lake/upstream-base` (Lake.Parquet's package metadata and
release notes name it), runs the test suite and prints the patch report. Always let it run the tests after
resolving a conflict (`--no-test` is for trying things out): a resolution that compiles can still be wrong.

Conflicts are only ever in unit code:

- **Upstream changed code a unit also changes**: keep both intents; the unit's tests (listed in
  upstream-review.md) say whether the result is right.
- **Upstream merged a unit** (yours, after review, possibly rewritten): take **upstream's** version and drop the
  fork's. Upstream's form is the one that will last; keep any test of the fork's that still adds something.
- **Upstream fixed the same bug differently**: take upstream's fix, keep the fork's test; if it fails, the
  difference is a bug to report upstream (by hand) and to keep in the fork meanwhile.

## The patch report

```bash
scripts/lake/patch-status.sh upstream/master
```

Replays `docs/upstream/series` in order on the given upstream commit:

| Status | Meaning | What to do |
|---|---|---|
| `in upstream` | upstream has exactly this change | retire the unit (below) |
| `applies` | still applies on top of upstream | nothing |
| `needs refresh` | upstream changed the same code, or took the unit in another form | refresh the series (below) |

## The fork to upstream: proposing and retiring units

Propose units in the order of upstream-review.md, one open PR at a time, written and submitted by hand (upstream does
not accept LLM-written PRs, issues or comments). Start each from a branch off upstream's `master`:

```bash
git fetch upstream
git switch -c propose/unit-01 upstream/master
git apply --index docs/upstream/01-timestamp-statistics-unit.patch   # then review, rewrite, commit by hand
```

When upstream merges a unit:

1. The next upstream sync brings it in (resolved toward upstream's version, as above).
2. `patch-status.sh` says `in upstream`. Delete its standalone patch and its `series/` file, and move its row in
   upstream-review.md to a "merged upstream" list with the upstream PR.
3. Units that were based on it (the "Applies to" column) now apply to upstream `master`; regenerate their
   standalone patches against it.

Refreshing the series after a `needs refresh`: on a worktree of the new upstream commit, apply the remaining series
patches in order with `git apply --index --3way`, resolve each conflict, commit, and export each step with
`git diff --binary HEAD~1 HEAD > docs/upstream/series/<name>.patch`. The end state must match the fork's library:
`git diff <series-head> master -- src/Parquet` should be empty.

When the series is empty, the fork is upstream: Lake.Parquet stops being published and its users go back to
Parquet.Net.

## The fork to Lake.Parquet

Lake.Parquet is released from Parquet.SourceGenerator (its `lake/README.md`): move the pin in `lake/fork.json` to the
new fork commit, add a section to `lake/release-notes.md`, and run its "Lake" workflow with "Publish". The same
workflow runs nightly against this repository's `master`, so a fork change that would break the Lake build or the
generated code shows up before it is pinned.

Iceberg.NET builds this repository from its `external/parquet-dotnet` submodule: after a sync, move the submodule
and run its suites with `ICEBERG_PARQUET_BACKEND=parquet-net`.

## One-time setup on GitHub

1. Enable Actions for the repository (for the weekly sync; upstream's own "Build" workflow can stay enabled, it
   builds and tests the fork and never publishes without upstream's NuGet secret).
2. Protect `master`: pull requests only.
