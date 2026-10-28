# Changes on top of upstream

One file per change this fork carries on top of
[aloneguid/parquet-dotnet](https://github.com/aloneguid/parquet-dotnet). Every commit that is not an
upstream merge belongs to a record here, through its PR or its change branch. rig's
`repo-governance fork-status` fails when one is missing.

## Two kinds of change

- **Additions** (packaging, CI, docs, the fork's own tests) only add files. They are squash-merged into
  `master`, one PR each, and the record names the PR.
- **Code changes** edit upstream's files. Each lives on its own branch, `lake/change/NNNN-short-name`,
  cut from upstream (`git switch -c lake/change/0002-exact-decimals upstream/master`), merged into
  `master` with a merge commit, and **kept**. The record names the branch. The branch's diff against
  upstream is exactly the change, which is also what an upstream PR would contain; a conflict with a
  new upstream release is resolved on that branch alone; and when a sync leaves the branch with no
  difference from upstream, upstream has the change and it can be retired.

The point is to know at any moment exactly what Lake.Parquet adds to upstream, and to drop each
addition once upstream has it.

## Format

`NNNN-short-name.md`, numbered in order (a code change's branch uses the same number and name), with
YAML frontmatter:

```yaml
---
title: Exact decimals for INT32/INT64-backed columns
branch: lake/change/0002-exact-decimals   # a code change: its branch
# pr: 12                   # an addition: its squash-merged PR (a record names one or the other)
status: fork-only          # fork-only | proposed-upstream | upstreamed | superseded
upstream: https://github.com/aloneguid/parquet-dotnet/issues/...   # optional; any issue or PR, ours or not
---
```

Then, in the body:

- **What**: the change, in a sentence or two.
- **Why**: the problem, with the evidence (a failing test, a benchmark, a reproduction).
- **Evaluation**: how it was measured against upstream, and the numbers.
- **Upstream**: what upstream would need to make the change unnecessary, and its status there.
- **Compatibility**: any behaviour that differs from upstream, or "none".

## Status

| Status | Meaning |
|---|---|
| `fork-only` | Carried here and nowhere else. |
| `proposed-upstream` | Raised upstream, by us or anyone else. Link it. |
| `upstreamed` | Upstream has it, or an equivalent. **Cleanup candidate.** |
| `superseded` | Upstream solved the same problem differently. **Cleanup candidate.** |

`fork-status` reports `upstreamed` and `superseded` records, and change branches that no longer differ
from upstream. Retiring a change is a PR that removes the code and deletes its record together; for a
code change, the branch is deleted once `master` has the upstream release that made it unnecessary.

## Index

| Record | Status |
|---|---|
| [0001 Lake.Parquet packaging and fork scaffolding](0001-lake-parquet-packaging.md) (#1) | fork-only |
