---
title: "Tests on net8.0 too"
kind: code
status: fork-only
depends_on: []
upstream_unit: "10c"
---

# 0013 Tests on net8.0 too

A test-only change: unit 10c of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** `c093dc0`.
- **Problem:** the library targets net8.0 and net10.0 but its tests ran on net10.0 only. One upstream test
  (`BigDecimalTest`) does not compile for net8.0: under C# 14, `bytes.Reverse()` binds to the in-place `Span`
  overload, which returns `void`.
- **Change:** `Enumerable.Reverse(bytes)`; the test project targets both. Both pass (772 each at the branch head).

## Evaluation

- **Without the change:** does not compile on net8.0.
- **With it:** pass on both (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0013-tests-on-net8` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
