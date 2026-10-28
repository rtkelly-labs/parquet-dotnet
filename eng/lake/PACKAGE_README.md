# Lake.Parquet

A fork of [Parquet.Net](https://github.com/aloneguid/parquet-dotnet), the fully managed Apache Parquet
library by Ivan Gavryliuk ([@aloneguid](https://github.com/aloneguid)) and its contributors.
Lake.Parquet is upstream's code, plus a short list of improvements that were each evaluated against
upstream and recorded, in the `Lake.Parquet` namespace. The MIT licence and upstream's copyright
ship in this package unchanged.

If you do not need one of the
[recorded changes](https://github.com/rtkelly-labs/parquet-dotnet/tree/master/docs/changes), use
[Parquet.Net](https://www.nuget.org/packages/Parquet.Net), the original.

## Usage

The API is Parquet.Net's. Change `Parquet` to `Lake.Parquet` in your `using` directives:

```csharp
using Lake.Parquet;
using Lake.Parquet.Serialization;

await ParquetSerializer.SerializeAsync(rows, stream);
```

The assembly is `Lake.Parquet.dll`, so it can be referenced alongside Parquet.Net.

Upstream's documentation applies apart from the namespace:
[Parquet.Net docs](https://github.com/aloneguid/parquet-dotnet/blob/master/docs/README.md).

## Links

- [Fork repository](https://github.com/rtkelly-labs/parquet-dotnet) and its [vision](https://github.com/rtkelly-labs/parquet-dotnet/blob/master/docs/vision.md)
- [Changes carried on top of upstream](https://github.com/rtkelly-labs/parquet-dotnet/tree/master/docs/changes)
- [Upstream: aloneguid/parquet-dotnet](https://github.com/aloneguid/parquet-dotnet)
