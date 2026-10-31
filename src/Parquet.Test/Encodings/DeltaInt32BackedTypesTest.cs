using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;
using PS = ParquetSharp;

namespace Parquet.Test.Encodings;

/// <summary>
/// short, ushort and uint are stored as INT32, whose DELTA_BINARY_PACKED deltas are 32-bit two's complement. Deltas
/// computed in the values' own width (wrapping at 16 bits, or unsigned) decoded to different values in parquet-cpp:
/// [-32768, 32767, 0] read back as [-32768, -32769, -65536].
/// </summary>
public class DeltaInt32BackedTypesTest {

    static async Task<string> WriteAsync<T>(T[] values) where T : struct {
        var field = new DataField<T>("v");
        var options = new ParquetOptions();
        options.ColumnEncodingHints["v"] = EncodingHint.DeltaBinaryPacked;
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
    public async Task Short_extremes() {
        short[] values = { short.MinValue, short.MaxValue, 0, -1, short.MinValue };
        string path = await WriteAsync(values);
        Assert.Equal(values.Select(v => (int)v), ReadWithParquetCpp(path, values.Length));
        Assert.Equal(values, await ReadBackAsync<short>(path, values.Length));
    }

    [Fact]
    public async Task Ushort_extremes() {
        ushort[] values = { 0, ushort.MaxValue, 1, 40000, 0 };
        string path = await WriteAsync(values);
        Assert.Equal(values.Select(v => (int)v), ReadWithParquetCpp(path, values.Length));
        Assert.Equal(values, await ReadBackAsync<ushort>(path, values.Length));
    }

    [Fact]
    public async Task Uints_keep_their_bits() {
        // spans a delta larger than int.MaxValue, which must wrap in 32 bits
        uint[] values = Enumerable.Range(0, 300).Select(i => i % 3 == 0 ? uint.MaxValue - (uint)i : (uint)i * 7).ToArray();
        string path = await WriteAsync(values);
        Assert.Equal(values.Select(v => unchecked((int)v)), ReadWithParquetCpp(path, values.Length));
        Assert.Equal(values, await ReadBackAsync<uint>(path, values.Length));
    }
}
