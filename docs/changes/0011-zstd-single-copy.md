---
title: "Zstd decompression without double copies"
kind: code
status: fork-only
depends_on: []
upstream_unit: "10"
---

# 0011 Zstd decompression without double copies

A performance change: unit 10 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** `cbcc43d` **and** the `Compressor.cs` part of `12b2856`, squashed: `cbcc43d` alone has a race (the
  per-thread decompressor was taken before an `await`, so concurrent reads could share one; it showed up as "Data
  corruption detected" in parallel ranged reads). **Never propose `cbcc43d` without the fix.**
- **Problem:** decompression copied the compressed page into a new array, let ZstdSharp allocate the output, then
  copied that again.
- **Measured on its own** (patch on upstream, 1M rows of string + binary + int64 in 4 row groups, best of 7):
  allocation per Zstd read 208 → 137 MB; time unchanged (252 → 250 ms). The read time gain seen in the fork comes
  from unit 10b. Snappy files are unaffected.
- **Review:** `ConcurrentZstdTest` is a smoke test only, it did not reproduce the race on the unfixed code. Judge
  whether a `[ThreadStatic]` cache is acceptable upstream at all (simplest alternative: a new `Decompressor` per page,
  keeping the single-copy part). The frame-header size fallback covers frames without a content size.

## Evaluation

- **Without the change:** none: perf (`ConcurrentZstdTest` guards the thread-static cache; 5 runs of 5).
- **With it:** 697 passed (the whole suite, when the unit was verified on upstream `658e1ca`).
- **Performance**, read, string + binary + int64, Zstd (4 row groups): upstream 252 ms, 208 MB allocated, with the change 250 ms, 137 MB.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0011-zstd-single-copy` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
