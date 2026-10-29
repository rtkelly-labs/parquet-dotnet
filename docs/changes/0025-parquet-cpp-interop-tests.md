---
title: "Interop tests against parquet-cpp"
kind: code
status: fork-only
depends_on: [0002, 0003, 0004, 0005, 0006, 0007, 0008, 0009, 0010, 0011, 0012, 0013, 0014, 0015, 0016, 0017, 0018, 0019, 0020, 0021, 0022, 0023, 0024]
upstream_unit: "21"
---

# 0025 Interop tests against parquet-cpp

A test-only change: unit 21 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** `ed5dd6f` (`ParquetCppInteropTest`, ParquetSharp as a test-only package reference), later additions in
  `792c737`.
- **Review:** upstream may not want a native test dependency. The alternative is committed fixture files written by
  parquet-cpp (as units 5, 15 and 20 already do) plus pyarrow checks in the existing Python interop tests.

## Evaluation

- **Without the change:** tests only.
- **With it:** 792 passed, net8.0 and net10.0 (the whole suite, when the unit was verified on upstream `658e1ca` with everything else applied first).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0025-parquet-cpp-interop-tests` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
