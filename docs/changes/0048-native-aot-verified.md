---
title: "Native AOT: the package is analyzer-clean, and a regression suite proves it natively"
kind: addition
status: fork-only
depends_on: [0032, 0044, 0045, 0047]
---

# 0048 Native AOT: the package is analyzer-clean, and a regression suite proves it natively

## What and why

Lake.Parquet is used where a native binary is the point: trimmed and native AOT apps, and Parquet.SourceGenerator's
own AOT consumers. Analyzer-clean is a claim; this makes it a build error to break and proves it by running natively.

- **The gate:** `Lake.Parquet.targets` sets `IsAotCompatible` on the Lake build, so any trimming or AOT finding in the
  package fails it (the project treats warnings as errors).
- **The suite:** `eng/lake/Lake.Parquet.AotTests`, published with `PublishAot`, every trimming and AOT warning an error
  (`TrimmerSingleWarn` off, so a finding in any dependency shows). It shares the conformance harness's case generator
  and its library-only oracles (the harness is split into `Harness.cs` and `Harness.Cpp.cs` so the parquet-cpp half
  stays out) and runs, natively:
  - `api`: the calls generated readers and writers make: typed fields of every type, nullable reads into `T?[]`,
    strings and byte arrays (strings also as UTF-8), decimals with their scale, timestamps with their kind, dates,
    GUIDs, statistics, under every codec, every encoding hint, one page or several per chunk, and modular encryption
    (843 checks);
  - `cases`: generated cases (every kind and shape, codec, hint, page layout, encryption): values, levels, field IDs,
    row counts and statistics read back exactly;
  - `read`/`write`: files the JIT build wrote read natively, and files the native build wrote read on the JIT;
  - `hashes`: files written natively are byte for byte the JIT build's (except encrypted ones, which draw random
    nonces, and gzip, whose zlib native AOT links itself and whose output differs while decoding the same);
  - `corpus`: upstream's test files (145, other writers' included) decoded to the same schema, values and levels as on
    the JIT, the same six failing alike (five encrypted without keys, one the library cannot read).
- **CI:** the `aot` job builds the package as published (members stripped, renamed), builds the suite on the JIT and
  publishes it natively against that package, runs all of the above (3,000 fixed and 2,000 fresh seeds), and `lake-ci`
  requires it.

## Evaluation

Locally against the stripped, renamed package: published with no warning; `api` 843 of 843; 5,000 generated cases;
1,000 files each way between the builds; 624 files byte-identical; 145 corpus files alike. The suite also caught a
path the library-level analyzer had not (`SchemaEncoder.GetDecimalDataField`), fixed in `lake(0047)`, and that the
harness's own generic `Integer<T>` needed the annotation, which is what a consumer generic over `T` sees.

## Upstream

Not for upstream: it tests the fork's package.

## Compatibility

None.
