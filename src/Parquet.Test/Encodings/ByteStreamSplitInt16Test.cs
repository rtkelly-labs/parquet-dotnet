using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;
using PS = ParquetSharp;

namespace Parquet.Test.Encodings;

/// <summary>
/// short and ushort are stored as INT32, so BYTE_STREAM_SPLIT gives them four streams, as int. Two streams (their
/// in-memory width) made files parquet-cpp rejects ("data size not aligned with type INT32").
/// </summary>
public class ByteStreamSplitInt16Test {

    static async Task<string> WriteAsync<T>(T[] values) where T : struct {
        var field = new DataField<T>("v");
        var options = new ParquetOptions();
        options.ColumnEncodingHints["v"] = EncodingHint.ByteSplitStream;
        string path = Path.GetTempFileName();
        await using(FileStream fs = System.IO.File.Create(path))
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), fs, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<T>(field, values);
        }
        return path;
    }

    static int[] ReadWithParquetCpp(string path, int count) {
        using var file = new PS.ParquetFileReader(path);
        using PS.RowGroupReader rg = file.RowGroup(0);
        using var column = (PS.ColumnReader<int>)rg.Column(0);
        int[] values = new int[count];
        column.ReadBatch(count, values, out _);
        return values;
    }

    static async Task<T[]> ReadBackAsync<T>(string path, int count) where T : struct {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        T[] values = new T[count];
        await rg.ReadRawAsync<T>(reader.Schema.GetDataFields()[0], values, null, null);
        return values;
    }

    [Fact]
    public async Task Shorts_are_split_as_int32() {
        short[] values = { 1, -2, 300, short.MinValue, short.MaxValue };
        string path = await WriteAsync(values);
        Assert.Equal(new[] { 1, -2, 300, short.MinValue, short.MaxValue }, ReadWithParquetCpp(path, values.Length));
        Assert.Equal(values, await ReadBackAsync<short>(path, values.Length));
    }

    [Fact]
    public async Task Ushorts_are_split_as_int32() {
        ushort[] values = { 1, 2, 65535, 32768 };
        string path = await WriteAsync(values);
        Assert.Equal(new[] { 1, 2, 65535, 32768 }, ReadWithParquetCpp(path, values.Length));
        Assert.Equal(values, await ReadBackAsync<ushort>(path, values.Length));
    }
}
