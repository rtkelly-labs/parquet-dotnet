---
title: "FLOAT16 columns are readable, as their two bytes"
kind: code
status: fork-only
depends_on: [0015]
evidence: failing-test
---

# 0052 FLOAT16 columns are readable, as their two bytes

## What and why

- **Problem:** opening any file with a `FLOAT16` column (parquet-cpp, so pyarrow and DuckDB, write `pa.float16()` this
  way: `FIXED_LEN_BYTE_ARRAY(2)` with the `FLOAT16` logical type) throws `NotSupportedException: type System.Half is
  not supported` while the schema is read. `SchemaEncoder` maps `FLOAT16` to `System.Half`, but no data field accepts
  `Half` (`DataField.CanCreateFrom`) and no encoder writes or reads it, so the whole file is unreadable, not only
  that column. Found by a pyarrow fixture written for the native AOT suite's coverage.
- **Change:** the `FLOAT16` branch is removed, so the column falls through to the `FIXED_LEN_BYTE_ARRAY` mapping:
  `byte[]` values of `FixedLength` 2 (`lake(0015)`), the little-endian IEEE 754 half bits (`BitConverter.ToHalf`
  turns one into a `Half`). The logical type is still read into the schema element. Writing `Half` stays
  unsupported, as before.

## Evaluation

- **Test:** `Reader/Float16Test` reads `data/float16.parquet` (written by pyarrow 26: 0, 1.5, null, -2): without the
  change it throws on open; with it the four values read back, the null included.
- **Suite:** 897 passed, 3 skipped, on net8.0 and net10.0. The fixture also joins the corpus the native AOT suite
  decodes on both builds.

## Upstream

A bug in upstream's code (the same branch is in aloneguid/parquet-dotnet). Upstream may prefer real `Half` support
(an encoder and a data field mapping); this is the smallest change that makes such files readable. Not yet proposed:
a person proposes it from `lake/change/0052-float16-columns-readable`.

## Compatibility

Files that threw on open now read; FLOAT16 values arrive as two-byte arrays.
