---
title: "The native AOT suite covers NestedLayout on pyarrow's deeply nested file"
kind: addition
status: fork-only
depends_on: [0048, 0079]
---

# 0080 The native AOT suite covers NestedLayout on pyarrow's deeply nested file

## What and why

`lake(0079)` adds `NestedLayout`. This adds it to the native AOT suite's `api` mode, on the same pyarrow file its
tests use (`data/lake_nested_layout.parquet`):

- every leaf's levels give a layout whose levels are the file's again;
- a list of lists of nullable ints assembles from offsets and packed values alone into the data pyarrow wrote.

## Evaluation

- **Native AOT, linux-x64:** the suite publishes with every trimming and AOT warning an error, and `api` passes all
  its checks, the new ones included.

## Upstream

Not applicable: the suite is the fork's.

## Compatibility

Test-only.
