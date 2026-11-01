---
title: "A writer disposed twice writes its footer once"
kind: code
status: fork-only
depends_on: [0058]
evidence: failing-test
---

# 0077 A writer disposed twice writes its footer once

## What and why

- **Problem:** `ParquetWriter.DisposeAsync` wrote the page indexes, footer, footer length and magic every time it
  was called. A second call (an `await using` around a writer also disposed by hand, or a dispose retried) appended a
  second footer: a one-row-group file grew from 220 to 385 bytes. `IAsyncDisposable` says disposing twice must be
  harmless. Found by the coverage tests of `lake(0074)`.
- **Change:** the writer remembers that it wrote its footer, and a second dispose writes nothing.

## Evaluation

- **Tests** (`Writer/WriterDisposeTest`, 1): after a second dispose the file is the same length, and reads with its
  three rows.
- **Without the change:** the length check fails.
- **Suite:** all pass on net8.0 and net10.0.

## Upstream

Upstream has the same dispose. A person may propose it from `lake/change/0077-writer-disposed-twice`.

## Compatibility

None for a writer disposed once.
