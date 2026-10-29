---
title: "Lake.Parquet packaging, the change stack and fork docs"
kind: addition
status: fork-only
depends_on: []
---

# 0001 Lake.Parquet packaging, the change stack and fork docs

## What and why

Everything that makes this repository a fork that publishes `Lake.Parquet`, without changing any
upstream file. It is the bottom commit of the stack, so every change is rebased on top of it.

- `README.md`, `docs/vision.md`, `AGENTS.md` and `AI_POLICY.md` at the top level, crediting upstream.
- `docs/maintaining.md`: the guide to the structure, the sync and re-basing every branch onto upstream.
- `docs/changes/`: the rules of the stack, this record, and how to propose a change upstream.
- `eng/lake/Lake.Stack` (C#) and its tests: checks the stack and pull requests into it, reports its
  changes, rebases it onto upstream, promotes a rebased stack, rebuilds the `lake/change/*` branches,
  and moves other branches onto a new stack. The workflows only call it.
- `eng/lake/RenameNamespace.cs`, `eng/lake/Lake.Parquet.targets`, `eng/lake/VerifyLakeAssembly.cs`
  and `eng/lake/PACKAGE_README.md`: the build-time rename from `Parquet` to `Lake.Parquet` and the
  package's identity.
- `.github/workflows/lake.yml`: checks the stack, renames a copy, builds, verifies the rename, runs
  the test suite against it, packs, and publishes on manual dispatch. `lake-ci` is the required check.
- `.github/workflows/lake-sync.yml`: rebases the stack onto upstream every day, and moves `master`
  only after `lake.yml` passes on the result.

The fork needs to ship improvements under its own name without renaming upstream's code, which would
make every rebase conflict, and to stay on top of the latest upstream without rewriting upstream's
history. See [`../vision.md`](../vision.md).

## Evaluation

Built locally from upstream `master` at `658e1ca`: the rename changed 162 of 164 source files and left
no `Parquet` namespace or using directive; the library and the test project build; the assembly's
metadata has 15 namespaces, all under `Lake.Parquet`; and the package is `Lake.Parquet 0.1.0` with
`lib/net8.0` and `lib/net10.0`, upstream's `LICENSE`, and its own README.

With 0002 to 0025 on top (the stack as first built, on `658e1ca`): the rename changed 189 of 191
files, the assembly has 16 namespaces, all under `Lake.Parquet`, and the renamed build passes the whole
suite, 792 passed and 3 skipped on each of net8.0 and net10.0 (`pyarrow` and `pandas` installed).
`sync` was exercised against a simulated upstream: upstream's commits kept their hashes, a
pending `fixup!` was folded in, a change whose code upstream had taken was reported, and a conflicting
upstream edit stopped the rebase with the stack untouched. `rewire --apply --all` was exercised after such a
sync: a branch cut from the previous stack moved only its own two commits onto the new one, a branch
cut from upstream before the stack existed (`feature/variant-encoding`) moved its four, and a branch
already on the new stack was left alone. Every `lake/change/*` branch, built alone on upstream with
its `depends_on`, passes the whole suite. Each of those scenarios, and the record format, `check`,
`check-pr`, `branches` and `promote` (including a refused lease), is now a test in
`eng/lake/Lake.Stack.Tests`, on throwaway repositories: 47 tests.

## Upstream

Not for upstream: it is the fork's packaging. It goes away only if the fork does.

## Compatibility

The namespace and assembly name are `Lake.Parquet` instead of `Parquet`. No behaviour differs.
