---
title: "The Thrift reader skips unknown struct, set, map, boolean-element and UUID fields"
kind: code
status: fork-only
depends_on: []
evidence: failing-test
---

# 0067 The Thrift reader skips unknown struct, set, map, boolean-element and UUID fields

## What and why

A reader must skip footer and page-header fields it does not know: every parquet-format release adds some, and a
file from a newer writer carries them. `SizeStatistics`, `GeospatialStatistics` and the VARIANT and GEOMETRY logical
types were all added as structs.

- **Problem:** `ThriftCompactProtocolReader.SkipField` mis-skipped four kinds of field:
  - **Structs:** it read each inner field's header but not its value, so the next header was read from the middle of
    a value. The reader lost its place, threw, or read garbage into the known fields after it.
  - **Booleans in lists:** it skipped them as zero bytes. Unlike a boolean field, a boolean list element takes a byte.
  - **Sets and maps:** it threw "file is most likely corrupt".
  - **UUIDs:** it threw the same.

  None of these are in today's footers. The first unknown struct field from a newer writer would have broken every
  reader of this library. Found by a measurement of what the regression suite covers: each structure's unknown-field
  path was never run.
- **Change:**
  - A struct's fields are skipped with their values.
  - Lists and sets skip their elements, maps their key-value pairs (an empty map has no type byte), each boolean
    element as one byte.
  - A UUID skips its 16 bytes.

## Evaluation

- **Tests** (`Thrift/ThriftUnknownFieldsTest`, 63):
  - each of the 61 footer, page-header, index and encryption structures reads a run of unknown fields of every
    compact type, and stops exactly at its end. The fields are a boolean (true and false), byte, i16, i32, i64,
    double, binary, list (of i32, of booleans, of 16 binaries), set, map (with pairs, and empty), UUID, and structs
    and lists of structs holding the same, two levels deep;
  - known fields after unknown ones read right;
  - a library-written footer with unknown fields appended reads the same.
- **Without the change:** all 63 fail.
- **Suite:** 1,058 passed, 3 skipped, on net8.0 and net10.0.

## Upstream

Upstream has the same `SkipField`. A person may propose it from `lake/change/0067-thrift-skips-unknown-fields`.

## Compatibility

Files that were read are read the same. Files with such unknown fields, which threw or misread, now read.
