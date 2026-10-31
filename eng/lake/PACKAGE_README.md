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

The API is Parquet.Net's reader, writer and schema. Change `Parquet` to `Lake.Parquet` in your `using`
directives:

```csharp
using Lake.Parquet;
using Lake.Parquet.Schema;

var id = new DataField<long>("id");
await using ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(id), stream);
using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
await rowGroup.WriteAsync<long>(id, ids);
```

Lake.Parquet leaves out the reflection-based `ParquetSerializer`. To map classes to columns, use
[Parquet.SourceGenerator](https://github.com/rtkelly-labs/Parquet.SourceGenerator), which does it at
compile time; to keep using `ParquetSerializer`, stay on Parquet.Net.

The assembly is `Lake.Parquet.dll`, so it can be referenced alongside Parquet.Net.

Upstream's documentation applies apart from the namespace:
[Parquet.Net docs](https://github.com/aloneguid/parquet-dotnet/blob/master/docs/README.md).

## Links

- [Fork repository](https://github.com/rtkelly-labs/parquet-dotnet) and its [decisions and vision](https://github.com/rtkelly-labs/Lake.NET/blob/main/docs/lake-parquet/decisions.md)
- [Changes carried on top of upstream](https://github.com/rtkelly-labs/parquet-dotnet/tree/master/docs/changes)
- [Upstream: aloneguid/parquet-dotnet](https://github.com/aloneguid/parquet-dotnet)
