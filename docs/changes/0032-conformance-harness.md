---
title: "Conformance harness: generated cases against parquet-cpp, in both directions"
kind: addition
status: fork-only
depends_on: [0026, 0027, 0028, 0029, 0030, 0031]
---

# 0032 Conformance harness: generated cases against parquet-cpp, in both directions

## What and why

`eng/lake/Lake.Parquet.Conformance`: seeded random cases (schemas of flat, list, struct and map columns over
every primitive kind the library writes; row groups; compression, statistics, encoding hints, field IDs and
modular encryption), each checked against independent oracles:

- **forward** (the library writes): an exact round trip of values, levels, field IDs and row counts; parquet-cpp
  (ParquetSharp) reading the same physical values, levels and field IDs, decrypting with the same keys; and
  statistics exactly as the values give them under the column's sort order, agreed by both readers;
- **reverse** (parquet-cpp writes, with its own encodings, data page versions, page sizes, checksums, page
  indexes and encryption): the library reads back exactly the values, levels, field IDs and statistics.

Hand-written tests cover what their author thought of. The harness covers combinations: every kind in every
shape, under every writer option, in both directions, and grows with the number of seeds. With
`docs/testing.md`, the guide to every test layer of the fork, a link to it from `README.md`, and a
`conformance` job in `lake.yml` (200 seeds per direction on every push and pull request, a deep run on fresh
seeds on demand) that `lake-ci` requires.

## Evaluation

Its first runs found six bugs in upstream's code, each now a change with a focused test: `lake(0026)` to
`lake(0031)` (int16/uint16 byte stream split; delta encoding of INT32-backed types; logical types under delta
and byte stream split; RLE booleans; empty column chunks; delta strings with non-ASCII prefixes). It also found
that parquet-cpp 25 writes AES_GCM_CTR_V1 files with a plaintext footer that nobody can read, parquet-cpp
included; the reverse direction leaves that combination out, with a comment.

With those changes, 2,000 seeds pass in each direction on net8.0 and net10.0 (8,000 cases), in about 35 seconds
per framework.

## Upstream

Not for upstream as it stands: it lives in the fork's `eng/lake/`. Its findings go upstream as the changes
above, each with its own test.

## Compatibility

None: tests only.
