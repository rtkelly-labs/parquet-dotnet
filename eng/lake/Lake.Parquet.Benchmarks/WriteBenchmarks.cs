using BenchmarkDotNet.Attributes;
using Parquet;
using Parquet.Schema;

namespace Lake.Parquet.Benchmarks;

/// <summary>
/// Writing a million-row column the way Parquet.SourceGenerator's generated writers do, into one reused stream, so
/// the time and allocations measured are the library's and not a growing output buffer's.
/// </summary>
[MemoryDiagnoser]
public class WriteBenchmarks {

    long[] _longs = [];
    double?[] _doubles = [];
    ReadOnlyMemory<char>?[] _strings = [];
    readonly DataField _longField = new DataField<long>("v");
    readonly DataField _doubleField = new DataField<double?>("v");
    readonly DataField _stringField = new DataField<string>("v");
    readonly MemoryStream _sink = new(64 * 1024 * 1024);

    [GlobalSetup]
    public void Setup() {
        _longs = Data.Longs();
        _doubles = Data.Doubles(0.1);
        _strings = Data.Strings(500_000).Select(s => s == null ? (ReadOnlyMemory<char>?)null : s.AsMemory()).ToArray();
    }

    [Benchmark]
    public Task RequiredInt64() => WriteAsync(_longField, rg => rg.WriteAsync<long>(_longField, _longs));

    [Benchmark]
    public Task NullableDouble_10pcNulls() => WriteAsync(_doubleField, rg => rg.WriteAsync<double>(_doubleField, _doubles));

    [Benchmark]
    public Task Strings_HighCardinality() => WriteAsync(_stringField, rg => rg.WriteAsync<ReadOnlyMemory<char>>(_stringField, _strings));

    async Task WriteAsync(DataField field, Func<ParquetRowGroupWriter, Task> write) {
        _sink.SetLength(0);
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), _sink);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();
        await write(rg);
    }
}
