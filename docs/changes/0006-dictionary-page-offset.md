---
title: "`dictionary_page_offset` never written"
kind: code
status: fork-only
depends_on: []
upstream_unit: "6"
---

# 0006 `dictionary_page_offset` never written

A bug fix: unit 6 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `344675a` (`DataColumnWriter.cs`, the two `chunk.MetaData!.…PageOffset = _stream.Position` lines).
- **Problem:** the writer never sets `dictionary_page_offset` and points `data_page_offset` at the dictionary page.
- **Change:** each set to the position of its page.
- **Review:** no dedicated test exists for this alone: add one asserting both offsets. Readers tolerate the old
  layout, so the motivation upstream is spec conformance (and encryption, unit 20, which needs it).

## Evaluation

- **Without the change:** `PageOffsetTest` (new) fails.
- **With it:** pass (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0006-dictionary-page-offset` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
