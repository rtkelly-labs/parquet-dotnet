---
title: "Branches and members no caller reaches leave the package"
kind: addition
status: fork-only
depends_on: [0075]
---

# 0084 Branches and members no caller reaches leave the package

## What and why

Lake.NET decision 6, as extended on 1 November 2026: a line the package ships is run by the regression suite, and code
nothing can reach is left out. The second round of coverage tests (`lake(0083)`) listed the lines that remain unrun
and why. This leaves out the ones no caller can reach, with `lake(0075)`'s branch entries in
`eng/lake/strip-members.json`:

- **Bit-unpacking at width 0.** Every caller handles width 0 itself, so these never run: the `bitWidth == 0` arm of
  both `BitPackedEncoder.Unpack8ValuesLE` overloads (`int` and `long`), and the `Unpack8IntValuesLE0` and
  `Unpack8LongValuesLE0` methods they called. At width 0 those methods left the buffer as it was rather than zeroing
  it.
- **`BigDecimal.WriteBytes`'s `bytesWritten > size` check.** `TryWriteBytes` has already guaranteed it.
- **`WritingColumn.Pack`'s `_dictionary == null` check.** The cast it guards cannot fail.
- **`ParquetRowGroupWriter.WriteAsyncInternal`'s `field == null` check.** The column validation before it throws
  first.
- **`MemoryExtensions.AsReadOnlyMemory(string)` and `AsReadOnlyMemory(byte[])`.** Only the left-out serializer called
  them.

Seven other unreachable null checks the round listed stay, because the compiler's nullable analysis relies on them
and the package builds with warnings as errors. Stripping them fails the build, which is how they were found:
- `rentedBuffer == null` in both of RLE's `Encode` and `EncodeWithLength`;
- `chunk.MetaData == null` in `DataColumnWriter.WriteAsync`;
- `columnChunk.MetaData == null` in `GetColumnMetaData`;
- `_meta?.RowGroups == null` in `InitRowGroupReaders`;
- the level getters' two null checks.

## Evaluation

- **The renamed build's suite** (CI's `build` job, run locally): 1,018 passed, 1 skipped, on net8.0 and net10.0,
  with 271 members and 5 branches left out.
- **The plain build** is unchanged: the source keeps all of it.

## Upstream

Not applicable.

## Compatibility

None: nothing reached this code.
