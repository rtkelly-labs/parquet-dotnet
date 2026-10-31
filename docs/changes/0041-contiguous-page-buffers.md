---
title: "Fixed-width pages encoded into one contiguous buffer"
kind: code
status: fork-only
depends_on: []
evidence: benchmark   # a performance change: its tests guard behaviour and pass either way
---

# 0041 Fixed-width pages encoded into one contiguous buffer

## What and why

- **Problem:** a data page was encoded into a RecyclableMemoryStream of 128 KB blocks, and the compressor needs
  one contiguous buffer, so `GetBuffer` copied every page's blocks into a large buffer before compressing it.
- **Change:** for fixed-width values the page's size is known within a little (values at most their CLR size,
  levels well under a byte each), so the page stream is requested as one contiguous buffer of that size and the
  compressor reads it in place. Byte arrays and strings keep the block stream; a page that outgrows its estimate
  grows as before.

## Evaluation

- **Profile harness**, alternating runs of builds with and without the change, three each (1M values, Snappy):
  `write-int64` 24.1 to 22.3 ms, `write-timestamp` 27.6 to 26.3 ms; allocation per write unchanged (rented).
- **Tests:** the suite's writer tests and the conformance harness (every kind, codec and encoding hint).

## Upstream

Not yet proposed: a person proposes it from `lake/change/0041-contiguous-page-buffers`.

## Compatibility

None: the same bytes.
