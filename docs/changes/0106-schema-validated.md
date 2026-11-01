---
title: "A corrupt schema in the footer throws ParquetException"
kind: code
status: fork-only
depends_on: [0100, 0105]
---

# 0106 A corrupt schema in the footer throws ParquetException

A fix for finding F4 of [0100](0100-malformed-file-tests.md): the footer's schema list was walked and turned into a
`ParquetSchema` without checking it, so a corrupt one failed with whatever the walk or the model's constructors threw.

## What and why

- **Problem:** an empty schema list (`schema[0]`) or a `num_children` that runs past the end of the list threw
  `ArgumentOutOfRangeException` from `ThriftSchemaTree`; a root with no fields (`num_children` 0 or negative) and a
  decimal whose scale exceeds its precision threw `ArgumentException` from the model's constructors (`ParquetSchema`,
  `DecimalDataField`); a physical type the format does not define (PARQUET-1481) threw `NotSupportedException`.
- **Change:**
  - `ThriftSchemaTree`: an empty list, or a parent whose `num_children` asks for more elements than the list has left,
    is a `ParquetException`.
  - `ThriftFooter.CreateModelSchema`: an `ArgumentException` from the model's own validation while the file's schema
    is built becomes a `ParquetException` ("the file's schema is invalid: …", with the original as the inner
    exception). The model's checks stay where they are and keep throwing `ArgumentException` for code that builds a
    schema itself; only the file-reading boundary translates them.
  - `SchemaEncoder.TryBuildDataField`: a physical type outside the `Type` enum is a `ParquetException` ("unknown
    physical type"), checked before the CLR type is looked up: `GetSystemType` maps a STRING, ENUM or INTEGER logical
    type, or a UTF8 or INT_* converted type, to a CLR type without looking at the physical type, so an undefined
    physical type under one of those annotations was accepted as a string or integer column. A defined type the library
    cannot map still throws `NotSupportedException` as before.
- **Review:** the try block covers only the conversion of the file's schema; the options passed in are the
  reader's own, so an `ArgumentException` there comes from the file.

## Evaluation

- **Test:** `SchemaValidationTest`: a string column's physical type set to 99 under a STRING or INTEGER(32) logical
  type, or a UTF8 converted type, fails with `ParquetException` when the schema is read (it was accepted before).

- **Malformed-file tests** (`MalformedFileTest`, net8.0 and net10.0, two runs each): known findings **124 → 9**.
  Taken off: all 115 cases of F4 (79 `ThriftSchemaTree`, 19 roots without fields, 9 unknown physical types including
  `bad_data/PARQUET-1481.parquet`, 8 decimals). Left: the 9 `meta:values=2^28` cases (`any`). No case that read
  cleanly before fails now (the corpus counts a `ParquetException` as clean, so the earlier check of an undefined type
  under an annotation moves no case).
- **The whole suite:** net10.0 and net8.0 each 2,828 passed (on `lake(0055)`), 3 skipped (upstream's), 0 failed.
- **Performance:** none: footer parsing only, a comparison per schema element.

## Upstream

Not yet proposed. Upstream accepts only hand-written issues and pull requests: a person proposes it from
`lake/change/0106-schema-validated` (`lake-stack branches`, Lake.NET docs/lake-parquet/maintaining.md). It applies to upstream as is.

## Compatibility

A file with a corrupt schema throws `ParquetException` instead of `ArgumentOutOfRangeException`, `ArgumentException`
or `NotSupportedException` (for an undefined physical type). Valid files, and files using a defined type the library
does not support, behave as before.
