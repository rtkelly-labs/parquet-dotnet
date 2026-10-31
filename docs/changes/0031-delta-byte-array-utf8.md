---
title: "`DELTA_BYTE_ARRAY` strings with non-ASCII prefixes"
kind: code
status: fork-only
depends_on: [0025]
---

# 0031 `DELTA_BYTE_ARRAY` strings with non-ASCII prefixes

## What and why

DELTA_BYTE_ARRAY prefix lengths count bytes. Strings were rebuilt from prefixes counted in chars and from suffixes
decoded to text on their own, so a value whose shared prefix held non-ASCII text, or ended inside a multi-byte
character, came back corrupted. Values are now rebuilt as UTF-8 bytes, then decoded whole.

## Evaluation

- **Found by** the conformance harness's reverse direction (strings with supplementary and BMP characters).
- **Test:** `DeltaByteArrayUtf8Test`: strings sharing prefixes in French, Japanese, emoji and the euro sign,
  written by parquet-cpp; they come back corrupted without the change.
- **Suite:** the whole suite passes with it (with every change of the stack, 809 passed, 3 skipped, on net8.0 and
  net10.0), and the conformance harness's 2,000 cases pass in both directions.

## Upstream

Not yet proposed. A bug in upstream's own code, reproducible on upstream `master` with files from parquet-cpp
(pyarrow and parquet-mr write the same layouts). Its test uses ParquetSharp, which `lake(0025)` adds to the
test project, so it depends on 0025; upstream may prefer committed fixture files. A person proposes it from
`lake/change/0031-delta-byte-array-utf8` (`lake-stack branches`, docs/maintaining.md).

## Compatibility

None: values that were corrupted now read correctly.
