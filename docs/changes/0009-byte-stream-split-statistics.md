---
title: "`BYTE_STREAM_SPLIT` columns get no statistics"
kind: code
status: fork-only
depends_on: []
upstream_unit: "8b"
---

# 0009 `BYTE_STREAM_SPLIT` columns get no statistics

A bug fix: unit 8b of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `a28da59` (`DataColumnWriter.cs`, one line).
- **Problem:** a column written with `EncodingHint.ByteSplitStream` gets no min/max at all: that branch never computes
  statistics, so engines cannot prune on it.
- **Change:** the plain encoder (which computes them) also runs into `Stream.Null`; writes there are no-ops, so the
  added cost is the min/max pass.
- **Review:** a dedicated statistics entry point (without the encoder dispatch) would be cleaner; this is the minimal
  version.

## Evaluation

- **Without the change:** 2 of 2 in `ByteStreamSplitStatisticsTest` fail.
- **With it:** 698 passed (the whole suite, when the unit was verified on upstream `658e1ca`).
- **Performance**, write, double column, BYTE_STREAM_SPLIT (now gets bounds): upstream 12 ms, with the change 14–16 ms.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0009-byte-stream-split-statistics` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
