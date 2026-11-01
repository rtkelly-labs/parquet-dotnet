---
title: "Tests: footer and page structures round-trip with every optional field set"
kind: addition
status: fork-only
depends_on: [0056]
---

# 0071 Tests: footer and page structures round-trip with every optional field set

## What and why

A coverage measurement of the regression suite found the generated Thrift code's writes of optional fields the
library never sets itself unrun. Those fields are what a footer rewritten by an append, or a structure built by a
caller, writes. This adds `Thrift/ThriftFullFieldsTest` (7). Each structure is written with every optional field set,
read back with nothing left over, and compared field by field:

- a v2 data page header with every statistics field, a CRC and an index page header;
- a sorted dictionary page header;
- a column index with repetition and definition level histograms and NaN counts;
- an offset index with unencoded byte-array sizes;
- a bounding box with Z and M;
- both encryption algorithms with every AAD field, with key metadata;
- a footer with a chunk's file path, an index page offset, a distinct count and a footer signing key.

## Evaluation

All 7 pass on net8.0 and net10.0. No defect was found: the generated code and `lake(0056)`'s double writer write each
field under its own id.

## Upstream

Not applicable.

## Compatibility

Test-only.
