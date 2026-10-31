---
title: "Conformance harness: chunks split into data pages"
kind: addition
status: fork-only
depends_on: [0032, 0043]
---

# 0044 Conformance harness: chunks split into data pages

## What and why

The harness's writer options now include `MaxDataPageValues` (`lake(0043)`): half the cases split their chunks into
pages of 1 to 60 values, so every kind and shape, under every encoding hint, codec and encryption setting, is
checked with multi-page chunks against parquet-cpp as well as by round trip. The choice is drawn from its own
random stream, so every existing seed keeps the schema, options and values it had.

## Evaluation

Its first run found that DELTA_BINARY_PACKED pages of no values (a page of only nulls) had no header, which
parquet-cpp rejects; fixed in `lake(0043)`. With that, 2,000 seeds and 10,000 fresh seeds pass in each direction on
net8.0 and net10.0.

## Upstream

Not for upstream: it lives in the fork's `eng/lake/`.

## Compatibility

None: tests only.
