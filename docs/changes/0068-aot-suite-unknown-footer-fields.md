---
title: "The native AOT suite reads a footer with unknown fields of every Thrift type"
kind: addition
status: fork-only
depends_on: [0048, 0067]
---

# 0068 The native AOT suite reads a footer with unknown fields of every Thrift type

## What and why

`lake(0067)` makes the Thrift reader skip unknown fields of every compact type. This adds a real file to the native
AOT suite's `api` mode, so the published native binary proves it through the public API.

The file, `data/lake_unknown_footer_fields.parquet`, is a pyarrow 26 file whose footer gets, before its stop, the same
unknown fields as `Thrift/ThriftUnknownFieldsTest`: a boolean, byte, i16, i32, i64, double, binary, lists, a set,
maps, a UUID, and structs and lists of structs two levels deep. pyarrow reads it. `data/make_unknown_fields.py` builds
it.

## Evaluation

- **Native AOT, linux-x64:** the file reads, values included. The same footer, built in `Thrift/ThriftUnknownFieldsTest`, fails to read without `lake(0067)`.

## Upstream

Not applicable: the suite is the fork's.

## Compatibility

Test-only.
