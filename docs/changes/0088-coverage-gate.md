---
title: "CI fails when the package's line coverage drops below 99%"
kind: addition
status: fork-only
depends_on: [0048, 0075]
---

# 0088 CI fails when the package's line coverage drops below 99%

## What and why

Lake.NET decision 6 and `testing.md` ("Coverage") set the target: every line the package ships is run by the
regression suite, or is listed with the reason it cannot be. The measured figure (99.8%) came from runs by hand. This
makes the floor a gate. A new `coverage` job in `lake.yml`, required by `lake-ci`:

- strips and renames a copy, as the `build` and `aot` jobs do;
- instruments the package (`Lake.Parquet.dll`) with `dotnet-coverage`, and collects line coverage around:
  - the native AOT suite's modes on its JIT build: `api`, `cases 1 3000`, `write`/`read 1 1000`, `hashes 1 1000`,
    `corpus`;
  - the xunit suite against the same build;
- runs both with `DOTNET_PreferredVectorBitWidth=512`, so the AVX-512 paths count where the runner has AVX-512;
- merges the two with `eng/lake/coverage.py` (a line is covered if either run covered it). It prints the figure and
  each file's unrun lines, and fails below 99%.

## Evaluation

- `eng/lake/coverage.py --min 99` on the last measurement by hand (`8b7364fa` plus `lake(0085)`, `lake(0086)`): 99.77%
  (13,693 of 13,725), passing, with the unrun lines the table in `testing.md` lists.
- The job's steps are those of that measurement.

## Upstream

Not applicable.

## Compatibility

CI-only.
