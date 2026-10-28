---
title: Lake.Parquet packaging and fork scaffolding
pr: 1
status: fork-only
---

# 0001 Lake.Parquet packaging and fork scaffolding

## What

Everything that makes this repository a fork that publishes `Lake.Parquet`, without changing any
upstream file:

- `README.md`, `docs/vision.md`, `AGENTS.md` and `AI_POLICY.md` at the top level, crediting upstream.
- `docs/changes/` (this directory) for the record of every change on top of upstream.
- `eng/lake/RenameNamespace.cs`, `eng/lake/Lake.Parquet.targets`, `eng/lake/VerifyLakeAssembly.cs`
  and `eng/lake/PACKAGE_README.md`: the build-time rename from `Parquet` to `Lake.Parquet` and the
  package's identity.
- `.github/workflows/lake.yml`: renames a copy, builds, verifies the rename, runs upstream's test
  suite against it, packs, and publishes on manual dispatch. `lake-ci` is the required check.

## Why

The fork needs to ship improvements under its own name without renaming upstream's code, which would
make every upstream merge conflict. See [`../vision.md`](../vision.md).

## Evaluation

Built locally from upstream `master` at 658e1ca: the rename changed 162 of 164 source files and left
no `Parquet` namespace or using directive; the library and upstream's test project build; the
assembly's metadata has 15 namespaces, all under `Lake.Parquet`; and the package is `Lake.Parquet
0.1.0` with `lib/net8.0` and `lib/net10.0`, upstream's `LICENSE`, and its own README. Upstream's test
suite runs against the renamed build in CI.

## Upstream

Not for upstream: it is the fork's packaging. It goes away only if the fork does.

## Compatibility

The namespace and assembly name are `Lake.Parquet` instead of `Parquet`. No behaviour differs.
