---
title: "The manifest can name an else clause; five more unreachable branches and members leave the package"
kind: addition
status: fork-only
depends_on: [0084]
---

# 0086 The manifest can name an else clause; five more unreachable branches and members leave the package

## What and why

The third round of coverage tests (`lake(0085)`) left 33 lines it judged unreachable. These leave the package
(`eng/lake/strip-members.json`):

- **`SchemaEncoder.GetSystemType`'s `se.Type == null` return.** Its one caller has already returned for a null type.
- **`ColumnMetrics.GetUsedEncodings`'s `DataPageHeaderV2` branch.** The writer never writes v2 data pages.
- **`ThriftSchemaTree.ReferenceEqualityComparer<T>.Default`.** Never used.
- **The `else` of `_footer == null` in `ParquetWriter.PrepareFileAsync`.** `_footer` is null on its only non-append
  call.
- **The `else` of `se.Path != null` in `ThriftFooter.CreateModelSchema`.** The path is always set by then.

For the last two, `StripMembers.cs` learns `"branch": "else:<condition>"`: it removes only the `else` clause of the
`if` with that condition, keeping the `if`.

## Evaluation

- **The renamed build's suite:** 1,044 passed, 1 skipped, on net8.0 and net10.0, with 272 members and 9 branches
  left out.
- **The plain build** is unchanged.

## Upstream

Not applicable.

## Compatibility

None: nothing reached this code.
