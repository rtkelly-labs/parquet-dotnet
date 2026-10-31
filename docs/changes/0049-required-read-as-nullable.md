---
title: "A required column read as nullable has no nulls, whatever the buffer pool held"
kind: code
status: fork-only
depends_on: []
evidence: failing-test
---

# 0049 A required column read as nullable has no nulls, whatever the buffer pool held

## What and why

- **Problem:** `ParquetRowGroupReader.ReadAsync<T>(DataField, Memory<T?>)` rents a definition-level buffer from the
  shared `ArrayPool<int>` (CommunityToolkit's `MemoryOwner<int>.Allocate`, which does not clear it) and marks a value
  null where its level is below the field's maximum. A required column (maximum level 0) has no levels, so nothing is
  written to the buffer: whatever another reader of the pool left there is read, and any negative number makes a
  value null. It shows as nulls that come and go with what else ran on the thread: `DataFrameReaderTest.Read_multiple_row_groups`
  failed once in CI (`df.Rows[1][0]` null) and passed on the rerun and locally.
- **Change:** for a required column the buffer is rented cleared (`AllocationMode.Clear`), so every level is 0, the
  maximum, and no value is null. Optional columns are unchanged: their levels are always read into the buffer.

## Evaluation

- **Test:** `Reader/RequiredReadAsNullableTest` fills the shared pool with -1 and reads a required `int` column into
  `int?[]`: without the change every value is null, with it they are 1, 2, 3, 4.
- **Cost:** clearing `RowCount` ints, only for required columns read as nullable; nothing for optional columns.

## Upstream

A bug in upstream's code (the same line is in aloneguid/parquet-dotnet). Not yet proposed: a person proposes it from
`lake/change/0049-required-read-as-nullable`.

## Compatibility

None: required columns read as nullable return their values, as they were always meant to.
