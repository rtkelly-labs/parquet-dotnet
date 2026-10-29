---
title: "Byte-array and text values: one array per page"
kind: code
status: fork-only
depends_on: []
upstream_unit: "10b"
---

# 0012 Byte-array and text values: one array per page

A performance change: unit 10b of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** `003c77f`.
- **Problem:** PLAIN decoding of BYTE_ARRAY / FIXED_LEN_BYTE_ARRAY allocated a `byte[]` per value, and text a
  `char[]` per value. Reading 1M UUIDs or strings spent as much time allocating as decoding.
- **Change:** each page is copied once into one array, and every value is a slice of it.
- **Measured on its own** (same benchmark as unit 10): read 252 → 119 ms (Zstd) and 241 → 114 ms (Snappy), allocation
  208 → 170 MB.
- **Review — trade-off:** a value the caller keeps alive keeps its whole page's array alive (one `ReadOnlyMemory`
  slice pins the page). For callers that keep a few values out of large pages that is more memory than before; upstream
  may prefer it behind an option. The char buffer is sized at one char per byte, which UTF-8 never exceeds.

## Evaluation

- **Without the change:** none: perf (`PageValuesTest` guards against values aliasing reused buffers).
- **With it:** 697 passed (the whole suite, when the unit was verified on upstream `658e1ca`).
- **Performance**, read, the same file, Zstd / Snappy: upstream 252 / 241 ms, with the change 119 / 114 ms.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0012-one-array-per-page` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

A value kept alive keeps its page's array alive (see Review notes).
