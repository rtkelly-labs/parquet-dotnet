using BenchmarkDotNet.Attributes;
using Parquet;
using Parquet.Schema;

namespace Lake.Parquet.Benchmarks;

/// <summary>
/// The paths that use hardware intrinsics: booleans written PLAIN (bit-packed with vectors), and BYTE_STREAM_SPLIT
/// floats and doubles read back. Run with <c>DOTNET_EnableAVX2=0</c> too, which leaves the 128-bit vectors native AOT's
/// default instruction set and Arm64 have.
/// </summary>
[MemoryDiagnoser]
public class VectorPathBenchmarks {

    const int Rows = 1_000_000;
    bool[] _bools = [];
    readonly DataField _boolField = new DataField<bool>("b");
    readonly DataField _floatField = new DataField<float>("f");
    readonly DataField _doubleField = new DataField<double>("d");
    byte[] _floatFile = [], _doubleFile = [];
    readonly float[] _floats = new float[Rows];
    readonly double[] _doubles = new double[Rows];
    readonly MemoryStream _sink = new(16 * 1024 * 1024);

    [GlobalSetup]
    public async Task Setup() {
        var rnd = new Random(42);
        _bools = Enumerable.Range(0, Rows).Select(_ => rnd.Next(3) == 0).ToArray();
        float[] f = Enumerable.Range(0, Rows).Select(_ => (float)rnd.NextDouble()).ToArray();
        double[] d = Enumerable.Range(0, Rows).Select(_ => rnd.NextDouble()).ToArray();
        _floatFile = await WriteSplitAsync(_floatField, rg => rg.WriteAsync<float>(_floatField, f));
        _doubleFile = await WriteSplitAsync(_doubleField, rg => rg.WriteAsync<double>(_doubleField, d));
    }

    static async Task<byte[]> WriteSplitAsync(DataField field, Func<ParquetRowGroupWriter, Task> write) {
        var options = new ParquetOptions { CompressionMethod = CompressionMethod.None };
        options.ColumnEncodingHints[field.Name] = EncodingHint.ByteSplitStream;
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await write(rg);
        }
        return ms.ToArray();
    }

    [Benchmark]
    public async Task WriteBooleans() {
        _sink.SetLength(0);
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(_boolField), _sink,
            new ParquetOptions { CompressionMethod = CompressionMethod.None });
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();
        await rg.WriteAsync<bool>(_boolField, _bools);
    }

    [Benchmark]
    public async Task ReadByteStreamSplitFloats() {
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(_floatFile));
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        await rg.ReadAsync<float>(reader.Schema.GetDataFields()[0], _floats);
    }

    [Benchmark]
    public async Task ReadByteStreamSplitDoubles() {
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(_doubleFile));
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        await rg.ReadAsync<double>(reader.Schema.GetDataFields()[0], _doubles);
    }
}
