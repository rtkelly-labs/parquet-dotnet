---
title: "Nulls counted vectorised; pages without nulls copied without a branch"
kind: code
status: fork-only
depends_on: []
evidence: benchmark   # a performance change: its tests guard behaviour and pass either way
---

# 0039 Nulls counted vectorised; pages without nulls copied without a branch

## What and why

- **Problem:** each page's nulls were counted by a scalar loop over its definition levels, and `ReadAsync` into
  `T?[]` rebuilt every value with a branch and a dependent index, nulls or not.
- **Change:** nulls are counted with the vectorised `Span.Count`; when a column chunk has none, `ReadAsync` copies
  the values in order.

## Evaluation

- **Profile harness:** `read-double` (1M nullable doubles, 10% null) 16.5 ms to 14.6 ms per read; with
  `lake(0038)`, `read-timestamp` 25.5 to 17.4 ms.
- **Tests:** the suite's nullable reads and the conformance harness (optional columns in every shape, with and
  without nulls).

## Upstream

Not yet proposed: a person proposes it from `lake/change/0039-nulls-counted-vectorised`.

## Compatibility

None.
