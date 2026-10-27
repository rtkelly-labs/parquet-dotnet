# Maintaining Lake.Parquet

Lake.Parquet is upstream Parquet.Net plus the review units in [../upstream-review.md](../upstream-review.md). The
job is keeping three things moving:

- **upstream to Lake**: every upstream Parquet.Net release merged in, so Lake users get upstream's fixes;
- **Lake to upstream**: the units proposed upstream one at a time, by hand, and dropped from Lake as they land;
- **Lake to its users**: Iceberg.NET and the source generator updated to each Lake.Parquet release.

## Repository layout

| Remote | Repository | Role |
|---|---|---|
| `origin` | `rtkelly-labs/parquet-dotnet` | Lake.Parquet. `master` is what is released. |
| `upstream` | `aloneguid/parquet-dotnet` | Parquet.Net. Never pushed to; its PRs are opened by hand from branches of `origin`. |

Keep the GitHub fork relationship (it is what lets a PR to upstream come from this repository). Renaming the
repository to `lake-parquet` in its settings keeps it; GitHub redirects the old URL.

`master` is **never rebased**: upstream comes in by merge commits, so published history, tags and the commit
Iceberg.NET's submodule pins stay valid.

## What differs from upstream

Two kinds of difference, kept apart on purpose:

1. **Identity** (Lake-only, never proposed upstream). Kept as small and as separate as possible, so upstream
   merges almost never touch it:

   | File | Lake's change | On conflict |
   |---|---|---|
   | `src/Parquet/Lake.Parquet.targets` | package and assembly name, metadata, upstream base | Lake-only file: no conflicts |
   | `src/Parquet/Parquet.csproj` | one line at the end: `<Import Project="Lake.Parquet.targets" />` | take upstream's file, keep the import as its last line |
   | `src/Parquet/File/ThriftFooter.cs` | `created_by` says `Lake.Parquet` (two lines) | take upstream's change, keep `Lake.Parquet` |
   | `docs/lake/`, `scripts/lake/`, `.github/workflows/lake*.yml` | Lake-only | no conflicts |

   Upstream's `docs/README.md`, `docs/release-notes.md` and `.github/workflows/build.yml` are left exactly as
   upstream has them, so they always merge cleanly. Lake has its own README, release notes and workflow.

2. **The review units**: the actual code changes, each one a patch in `docs/upstream/` (standalone) and
   `docs/upstream/series/` (in order). These are what conflict when upstream changes the same code, and what goes
   away as upstream accepts them.

The C# namespace stays `Parquet` for this reason: renaming it would touch every file, every upstream merge would
conflict everywhere, and no unit could be proposed upstream as is.

## Upstream to Lake: merging an upstream release

Automatic: `.github/workflows/lake-upstream-sync.yml` runs every Monday (and on demand, for a given tag). It
merges new upstream commits into a `sync/upstream-<sha>` branch, records the new base, runs the tests and opens a
pull request with the patch report; on a merge conflict it opens an issue instead. Nothing reaches `master`
without a person merging the PR.

By hand, or to resolve a conflict:

```bash
scripts/lake/sync-upstream.sh            # latest upstream master
scripts/lake/sync-upstream.sh 6.2.0      # an upstream release tag
# on conflict: resolve with the table above and the rules below, git commit, then
scripts/lake/sync-upstream.sh --continue
```

The script merges, then sets `LakeUpstreamCommit` and `LakeUpstreamVersion` in `Lake.Parquet.targets`, runs the
test suite and prints the patch report. Always let it run the tests after resolving a conflict (`--no-test` is
for trying things out): a resolution that compiles can still be wrong.

Resolving conflicts in unit code:

- **Upstream changed code a unit also changes**: keep both intents; the unit's tests (listed in
  upstream-review.md) say whether the result is right.
- **Upstream merged a unit** (yours, after review, possibly rewritten): take **upstream's** version of that code
  and drop Lake's. Upstream's form is the one that will last; keep any test of Lake's that still adds something.
- **Upstream fixed the same bug differently**: take upstream's fix, keep Lake's test; if the test fails, the
  difference is a bug to report upstream (by hand) and to keep in Lake meanwhile.

## The patch report

```bash
scripts/lake/patch-status.sh upstream/master
```

Replays `docs/upstream/series` in order on the given upstream commit:

| Status | Meaning | What to do |
|---|---|---|
| `in upstream` | upstream has exactly this change | remove the unit (below) |
| `applies` | still applies on top of upstream | nothing |
| `needs refresh` | upstream changed the same code, or took the unit in another form | refresh the series (below) |

The Lake.Parquet workflow prints it on every push (job "Upstream patch series", which reports but never fails
the build).

## Lake to upstream: proposing and retiring units

Propose units in the order of upstream-review.md, one open PR at a time, written and submitted by hand (upstream
does not accept LLM-written PRs, issues or comments). Start each from a branch off **upstream's** `master` with
the unit's standalone patch:

```bash
git fetch upstream
git switch -c propose/unit-01 upstream/master
git apply --index docs/upstream/01-timestamp-statistics-unit.patch   # then review, rewrite, commit by hand
```

When upstream merges a unit:

1. Wait for the next upstream sync to bring it in (resolving toward upstream's version, as above).
2. `patch-status.sh` now says `in upstream`. Delete the unit's standalone patch and its `series/` file, and move
   its row in upstream-review.md to a "merged upstream" list with the upstream PR.
3. If later units were based on it (the "Applies to" column), their standalone patches now apply to upstream
   `master` directly; regenerate them against it.

Refreshing the series after a `needs refresh`: on a worktree of the new upstream commit, apply the remaining
series patches in order with `git apply --index --3way`, resolve each conflict, commit, and export each step with
`git diff --binary HEAD~1 HEAD > docs/upstream/series/<name>.patch`. The standalone patches are regenerated the
same way against their base. The end state must match Lake's library code; check with
`git diff <series-head> master -- src/Parquet`, which should show only the identity lines above.

When the series is empty, Lake.Parquet is upstream plus its identity: at that point stop publishing it and point
users back to Parquet.Net.

## Releasing Lake.Parquet

1. Merge the sync PR (or feature work) into `master`.
2. Add a section at the top of `docs/lake/release-notes.md`; its heading is the version. Lake.Parquet follows
   semantic versioning of **its own** API, independent of upstream's numbers:
   - patch: fixes, or an upstream release with fixes only;
   - minor: new API (Lake's or upstream's), or an upstream minor release;
   - major: anything breaking, including an upstream major release.
   Name the upstream base in the section ("Based on Parquet.Net 6.2.0").
3. Run the "Lake.Parquet" workflow from the Actions tab with "Publish" ticked. It tests on Linux, Windows and
   macOS, builds the package (version from the heading, `Globals.Version` stamped), pushes it to NuGet with the
   `LAKE_NUGET_API_KEY` secret and creates the GitHub release `lake-v<version>`.

## Lake to its users

- **Iceberg.NET** builds the fork from its `external/parquet-dotnet` submodule. After a Lake release: move the
  submodule to the release commit, run its suites on the Parquet.Net backend (`ICEBERG_PARQUET_BACKEND=parquet-net`),
  commit. Once Lake.Parquet is on NuGet, Iceberg.NET can swap the submodule for a `Lake.Parquet` package
  reference and let Dependabot propose updates.
- **Lake.Parquet.SourceGenerator** (planned, in the Parquet.SourceGenerator repository) depends on a version range
  of Lake.Parquet; its CI tests against the published package and, nightly, against this repository's `master`,
  so a Lake change that breaks the generator shows up before a release.

## One-time setup on GitHub

1. Enable Actions for the repository.
2. Disable upstream's "Build" workflow (Actions, Build, "Disable workflow"): it is kept unchanged for merging,
   but Lake is built and released by the "Lake.Parquet" workflow.
3. Create an environment `lake-release` with the secret `LAKE_NUGET_API_KEY` (a NuGet key scoped to `Lake.*`),
   and required reviewers if releases should need approval.
4. Make `master` the Lake.Parquet branch: merge this work into it, and protect it (pull requests, the Lake.Parquet
   checks).
5. Optional: rename the repository to `lake-parquet`, then update the submodule URL in Iceberg.NET's
   `.gitmodules`.
