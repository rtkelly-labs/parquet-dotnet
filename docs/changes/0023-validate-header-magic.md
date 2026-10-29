---
title: "`ParquetOptions.ValidateHeaderMagic`"
kind: code
status: fork-only
depends_on: [0016]
upstream_unit: "19"
---

# 0023 `ParquetOptions.ValidateHeaderMagic`

A feature: unit 19 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** the `ParquetActor`/`ParquetOptions`/`ParquetReader` parts of `12b2856`, with
  `Header_magic_check_can_be_skipped`.
- **Review:** small and optional: it saves a request over ranged storage (parquet-cpp does not read the head).

## Evaluation

- **Without the change:** `Header_magic_check_can_be_skipped` does not compile.
- **With it:** 697 passed (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0023-validate-header-magic` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
