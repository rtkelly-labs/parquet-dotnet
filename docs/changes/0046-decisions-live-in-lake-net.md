---
title: "The fork's decisions and process documents move to Lake.NET"
kind: addition
status: fork-only
depends_on: [0045]
---

# 0046 The fork's decisions and process documents move to Lake.NET

## What and why

The fork's vision, rules, operations guide, test guide and upstreaming notes (`docs/vision.md`,
`docs/changes/README.md`, `docs/maintaining.md`, `docs/testing.md`, `docs/changes/upstreaming.md`) move to Lake.NET,
under [`docs/lake-parquet/`](https://github.com/rtkelly-labs/Lake.NET/blob/main/docs/lake-parquet/README.md), beside a new [decisions](https://github.com/rtkelly-labs/Lake.NET/blob/main/docs/lake-parquet/decisions.md) page. Lake.NET is the project
the fork exists for, so its decisions are made there, in Lake.NET pull requests, and the fork's code follows them.

What stays here: the code and tooling, `README.md` (attribution, licence, how the package is built), `AGENTS.md`, and
one record per change in `docs/changes/`, written in the change's own pull request. `AGENTS.md` now sends every
major decision to Lake.NET and sets out how a change is written so it can be rewritten deterministically at each
rebaseline (smallest edit, a record complete enough to re-make it from, mechanical edits by tools from data,
templates and generated files together, conflicts re-made and re-verified rather than merged).

References in the README, the package README, the stack tool, the workflow and the commitlint config point to Lake.NET.

## Evaluation

`lake-stack check` and the stack tool's tests pass; no code changes.

## Upstream

Not for upstream.

## Compatibility

None.
