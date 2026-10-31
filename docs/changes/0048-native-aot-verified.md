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
    GUIDs, statistics, under every codec, every encoding hint, one page or several per chunk, and modular encryption;
    every bit width the generated bit-packing kernels have (0 to 32 for int, 0 to 64 for long, through
    DELTA_BINARY_PACKED) and dictionary indexes of 1 to 16 bits; 16-bit and unsigned integers under delta, `Interval`
    and `BigDecimal`; columns declared by physical type (`RawDataField`); writing to a stream that cannot seek; the
    schema API and key-value metadata (979 checks; enum fields' nullable type is the same natively, and enum columns
    are refused alike: they are the serializer's, which Lake.Parquet leaves out);
  - `cases`: generated cases (every kind and shape, codec, hint, page layout, encryption): values, levels, field IDs,
    row counts and statistics read back exactly;
  - `read`/`write`: files the JIT build wrote read natively, and files the native build wrote read on the JIT; and
    parquet-cpp, an independent implementation, reads every file the native build wrote and decrypts the encrypted ones
    (the conformance project's `Files_written_by_another_build_agree_with_parquet_cpp`), which proves native encryption
    where random nonces rule out comparing bytes;
  - `hashes`: files written natively are byte for byte the JIT build's; gzip cases are compared written uncompressed,
    since native AOT links its own zlib, whose compressed bytes differ while decoding the same (the gzip files
    themselves are covered by the cross-build reads, as are encrypted files, whose nonces are random);
  - `corpus`: upstream's test files (145, other writers' included) decoded to the same schema, values and levels as on
    the JIT: 144, the encrypted ones with the keys upstream's tests document, and one refused as expected on both
    (`special/all_nulls_no_booleans.parquet`, a FIXED_LEN_BYTE_ARRAY with no length, which parquet-cpp refuses too);
    any other failure, or that file decoding, fails the run.
- **CI:** the `aot` job builds the package as published (members stripped, renamed), builds the suite on the JIT and
  publishes it natively against that package, runs all of the above (3,000 fixed and 2,000 fresh seeds), and `lake-ci`
  requires it. It runs on Linux x64 and Windows x64; the macOS arm64 leg, on runners that cost ten times as much, runs
  only on a pull request labelled `macos` or a run dispatched with `macos`. On Linux the job also adds a method nothing
  calls that needs dynamic code (`MakeGenericType` over `Type.GetType`) to a copy of the package and requires the Lake
  build to fail with `IL3050`: the analyzers stay on, over code the suite never runs.

## Evaluation

What covers all of the code, not only what the suite runs: `IsAotCompatible` turns on the trimming, AOT and
single-file analyzers over every method of the package, as errors, so a finding anywhere fails the Lake build; and
publishing the suite with `PublishAot` and `TrimmerSingleWarn=false` analyses the whole program with each warning
reported on its own. Both report none, and the canary step proves they look at uncalled code (it fails with
`IL3050` and `IL2057`). The native runs then show that the code behaves the same compiled ahead of time.

Coverage of the package by the suite's modes (`api`, `cases 1 3000`, `write`/`read 1 1000`, `hashes`, `corpus`), run on
the JIT build of the same program with `dotnet-coverage` (the assembly instrumented): 56.2% of all lines (11,450 of
20,378), and 83.3% (11,450 of 13,746) leaving out the big-endian bit-packing kernels (6,632 generated lines that
nothing in the library calls; only upstream's own encoder test does). Before the wider `api` checks it was 35.8% and
53%: the generated little-endian kernels are now reached at every width. What stays uncovered is mostly thrift
fields no file here sets (`Meta/Parquet.cs`) and plain encoders for CLR types the core API maps to their physical
type (`TimeOnly`, `TimeSpan`).

Against the stripped, renamed package, on Linux x64, Windows x64 and macOS arm64 in CI (run 41244509500, at `a6a843b0`): published with no warning;
`api` 845 of 845 (979 of 979 natively since, on Linux, with the wider checks); 5,000 generated cases; 1,000 files each way between the builds; 762 files byte-identical (138 of them
gzip cases, uncompressed); 144 corpus files decoded alike and the malformed one refused alike. The suite also caught a
path the library-level analyzer had not (`SchemaEncoder.GetDecimalDataField`), fixed in `lake(0047)`, and that the
harness's own generic `Integer<T>` needed the annotation, which is what a consumer generic over `T` sees.

## Upstream

Not for upstream: it tests the fork's package.

## Compatibility

None.
