---
title: "Physical values (`DataField.PhysicalClrType`)"
kind: code
status: fork-only
depends_on: [0002, 0008]
upstream_unit: "14"
---

# 0017 Physical values (`DataField.PhysicalClrType`)

A feature: unit 14 of the review guide that this stack replaced. The
**Commits** named below are where it was first written, on the branch `ccr-87fadcf9-bxi9wr`
(kept as the tag `lake/archive/ccr-87fadcf9`).

## What and why

- **Commits:** part of `7ecfa00` (`DataField.PhysicalClrType`, `IsCompatibleWith`, `WritingColumn.Validate`,
  `PhysicalValuesTest`).
- **Problem:** `ReadRawAsync<T>`/`WriteAsync<T>` require the logical CLR type: `TIMESTAMP(NANOS)` cannot be read as
  `long` nanoseconds (and `DateTime` loses the last two digits), dates not as `int` days, decimals not unscaled.
- **Review:** `PhysicalClrType` re-encodes the schema element when the field is not attached to a file
  (`SchemaEncoder.Encode(this, new ParquetOptions())`) on every call; cache it if upstream minds. Check that a
  physical write keeps the logical annotation and the statistics stay right (`PhysicalValuesTest`).

## Evaluation

- **Without the change:** `PhysicalValuesTest` does not compile.
- **With it:** 716 passed (the whole suite, when the unit was verified on upstream `658e1ca` with 1 + 8 applied first).

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0017-physical-values` (`lake-stack branches`, docs/maintaining.md), using the Review notes above.

## Compatibility

None: a fix or an addition; code that used upstream's API behaves as before.
