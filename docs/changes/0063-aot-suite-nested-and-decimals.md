---
title: "The native AOT suite covers nested typed columns and exact decimals"
kind: addition
status: fork-only
depends_on: [0048, 0061, 0062]
---

# 0063 The native AOT suite covers nested typed columns and exact decimals

## What and why

`lake(0061)` and `lake(0062)` change paths the native AOT suite (`lake(0048)`) did not reach: decimals wider than
`System.Decimal`, and columns in optional structs and lists through the typed API. This adds them to the suite's
`api` mode, so the published native binary runs them on Linux and Windows in every CI run:

- **Nested typed columns:** an optional struct holding a `long` with no nulls and a required string; a list of `int?`
  with its repetition levels; and lists of strings and byte arrays with a null element and a missing list. Each is
  written and read back through `WriteAsync`/`ReadAsync`.
- **Exact decimals:** `data/lake_decimal_38_9.parquet` (pyarrow 26, decimal128(38,9)). Values that fit read exactly,
  and one beyond `decimal` is refused, naming `UseBigDecimal`. A 38-digit value the library wrote is refused as
  `decimal` and reads exactly as `BigDecimal`.

## Evaluation

- **Native AOT, linux-x64:** the suite publishes with every trimming and AOT warning an error, and `api` passes all
  its 1,095 checks, the 10 new ones included. The other modes (`cases`, JIT/native `write`/`read`, `hashes`, `corpus`)
  also pass.

## Upstream

Not applicable: the suite is the fork's.

## Compatibility

Test-only.
