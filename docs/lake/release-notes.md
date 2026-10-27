# 0.1.0

First release of Lake.Parquet, based on Parquet.Net 6.1.1-pre.2
([658e1ca](https://github.com/aloneguid/parquet-dotnet/commit/658e1cafebdd1d80d6833c40b0f387d0769898c8)).

Everything in [README.md](README.md) under "What it adds to Parquet.Net". Each change, with its tests and measured
effect, is described in [../upstream-review.md](../upstream-review.md).

Differences a Parquet.Net user notices:

- Package `Lake.Parquet`, assembly `Lake.Parquet.dll`; namespace `Parquet` as before.
- Files say `created_by: Lake.Parquet version …`.
- Statistics of `INT32`/`INT64` decimals come back as `decimal` and unsigned integers as their unsigned type
  (Parquet.Net returned the stored integers).
- Old-style list groups (`array`, `<name>_tuple`, several fields) decode as the format's backward-compatibility
  rules say: as lists of structs.
