---
title: "String statistics compared vectorised"
kind: code
status: fork-only
depends_on: [0008]
evidence: benchmark   # a performance change: its tests guard behaviour and pass either way
---

# 0040 String statistics compared vectorised

## What and why

- **Problem:** string min/max (`lake(0008)`, code point order) compared strings a char at a time; in a profile of
  writing 1M strings it was 16% of the time.
- **Change:** strings that differ in their first char (most) compare at once; others find their first difference
  with the vectorised `CommonPrefixLength`. The order is unchanged.

## Evaluation

- **Min/max of 900,000 strings** (best of 6): random strings 87 to 66 ms; URLs with a 36-char common prefix 57 to
  26 ms.
- **Tests:** the suite's statistics tests (code point order, surrogates, BMP private use) and the conformance
  harness's statistics oracle.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0040-string-statistics-vectorised` (with `lake(0008)`).

## Compatibility

None.
