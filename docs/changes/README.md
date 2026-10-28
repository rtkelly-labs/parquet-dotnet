# Changes on top of upstream

One file per change this fork carries on top of
[aloneguid/parquet-dotnet](https://github.com/aloneguid/parquet-dotnet). Every commit that is not an
upstream merge comes from a PR, and every PR has a record here. rig's
`repo-governance fork-status` fails when one is missing.

The point is to know at any moment exactly what Lake.Parquet adds to upstream, and to drop each
addition once upstream has it.

## Format

`NNNN-short-name.md`, numbered in order, with YAML frontmatter:

```yaml
---
title: Exact decimals for INT32/INT64-backed columns
pr: 12                     # the fork PR that introduced the change
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

`fork-status` reports `upstreamed` and `superseded` records. Retiring a change is a PR that removes
the code and deletes its record together.

## Index

| Record | Status |
|---|---|
| [0001 Lake.Parquet packaging and fork scaffolding](0001-lake-parquet-packaging.md) (#1) | fork-only |
