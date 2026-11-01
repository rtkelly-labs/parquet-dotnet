---
title: "A writer without an output stream is refused with ArgumentNullException"
kind: code
status: fork-only
depends_on: [0077]
evidence: failing-test
---

# 0078 A writer without an output stream is refused with ArgumentNullException

## What and why

- **Problem:** `ParquetWriter.CreateAsync(schema, null)` threw `NullReferenceException`. The constructor's base call
  reads `output.CanSeek` before the body's null check runs, so the documented `ArgumentNullException` was never
  thrown, and its check was dead code. Found by the coverage tests of `lake(0074)`.
- **Change:** the base call checks for null itself (`output?.CanSeek`, then `output ?? throw new
  ArgumentNullException(nameof(output))`), and the unreachable check in the body goes.

## Evaluation

- **Tests** (`Writer/WriterArgumentsTest`, 1): a null output throws `ArgumentNullException` with `ParamName`
  `output`.
- **Without the change:** it throws `NullReferenceException`.
- **Suite:** all pass on net8.0 and net10.0.

## Upstream

Upstream has the same constructor. A person may propose it from `lake/change/0078-writer-null-output`.

## Compatibility

The exception a null output throws changes from `NullReferenceException` to `ArgumentNullException`, as documented.
