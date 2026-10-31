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
    (844 checks, with enum columns refused alike: they are the serializer's, which Lake.Parquet leaves out);
  - `cases`: generated cases (every kind and shape, codec, hint, page layout, encryption): values, levels, field IDs,
    row counts and statistics read back exactly;
  - `read`/`write`: files the JIT build wrote read natively, and files the native build wrote read on the JIT;
  - `hashes`: files written natively are byte for byte the JIT build's; gzip cases are compared written uncompressed,
    since native AOT links its own zlib, whose compressed bytes differ while decoding the same (the gzip files
    themselves are covered by the cross-build reads, as are encrypted files, whose nonces are random);
  - `corpus`: upstream's test files (145, other writers' included) decoded to the same schema, values and levels as on
    the JIT: 144, the encrypted ones with the keys upstream's tests document, and one refused as expected on both
    (`special/all_nulls_no_booleans.parquet`, a FIXED_LEN_BYTE_ARRAY with no length, which parquet-cpp refuses too);
    any other failure, or that file decoding, fails the run.
- **CI:** the `aot` job builds the package as published (members stripped, renamed), builds the suite on the JIT and
  publishes it natively against that package, runs all of the above (3,000 fixed and 2,000 fresh seeds), and `lake-ci`
  requires it.

## Evaluation

Against the stripped, renamed package, on Linux x64, Windows x64 and macOS arm64 in CI: published with no warning;
`api` 844 of 844; 5,000 generated cases; 1,000 files each way between the builds; 762 files byte-identical (138 of them
gzip cases, uncompressed); 144 corpus files decoded alike and the malformed one refused alike. The suite also caught a
path the library-level analyzer had not (`SchemaEncoder.GetDecimalDataField`), fixed in `lake(0047)`, and that the
harness's own generic `Integer<T>` needed the annotation, which is what a consumer generic over `T` sees.

## Upstream

Not for upstream: it tests the fork's package.

## Compatibility

None.
