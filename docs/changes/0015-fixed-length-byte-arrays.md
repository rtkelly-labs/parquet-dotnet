---
title: "`FIXED_LEN_BYTE_ARRAY(n)` byte arrays"
kind: code
status: fork-only
depends_on: [0007, 0008]
upstream_unit: "12"
---

# 0015 `FIXED_LEN_BYTE_ARRAY(n)` byte arrays

A feature: unit 12 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `54efd9a` (`DataField.FixedLength`, `SchemaEncoder`, `EncodeFixed`, `FixedLengthByteArrayTest`).
- **Review:** API naming (`FixedLength` on `DataField`, `int?`). A value of the wrong length throws `ParquetException`.

## Evaluation

- **Without the change:** `FixedLengthByteArrayTest` does not compile.
- **With it:** 699 passed (the whole suite, when the unit was verified on upstream `658e1ca`).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0015-fixed-length-byte-arrays` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
