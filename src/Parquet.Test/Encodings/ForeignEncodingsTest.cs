using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;
using PS = ParquetSharp;
using PSchema = ParquetSharp.Schema;

namespace Parquet.Test.Encodings;

/// <summary>
/// Files from other writers use DELTA_BINARY_PACKED, BYTE_STREAM_SPLIT and DELTA_BYTE_ARRAY for columns whose CLR
/// type is not their physical type (parquet-mr's v2 writer uses delta encoding for every INT32 and INT64 column).
/// Their decoders only produce physical values, so reading a timestamp, date, decimal, int8 or UUID column so
/// encoded threw NotSupportedException.
/// </summary>
public class ForeignEncodingsTest : IDisposable {

    private readonly string _dir = Directory.CreateTempSubdirectory("foreign-encodings").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    string Write(PSchema.Node column, PS.Encoding encoding, Action<PS.ColumnWriter> write) {
        string path = Path.Combine(_dir, $"{column.Name}-{encoding}.parquet");
        using var root = new PSchema.GroupNode("schema", PS.Repetition.Required, new[] { column });
        using PS.WriterProperties properties = new PS.WriterPropertiesBuilder()
            .DisableDictionary(column.Name).Encoding(column.Name, encoding).Build();
        using var file = new PS.ParquetFileWriter(path, root, properties);
        using PS.RowGroupWriter rg = file.AppendRowGroup();
        using(PS.ColumnWriter writer = rg.NextColumn())
            write(writer);
        file.Close();
        return path;
    }

    static async Task<T[]> ReadAsync<T>(string path, int count) where T : struct {
        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        T[] values = new T[count];
        await rg.ReadRawAsync<T>(reader.Schema.GetDataFields()[0], values, null, null);
        return values;
    }

    [Theory]
    [InlineData(PS.Encoding.DeltaBinaryPacked)]
    [InlineData(PS.Encoding.ByteStreamSplit)]
    public async Task Timestamps(PS.Encoding encoding) {
        long[] micros = { 0, 1_000_000, -5, 1_700_000_000_000_000 };
        using var node = new PSchema.PrimitiveNode("ts", PS.Repetition.Required, PS.LogicalType.Timestamp(true, PS.TimeUnit.Micros), PS.PhysicalType.Int64);
        string path = Write(node, encoding, w => ((PS.ColumnWriter<long>)w).WriteBatch(micros));
        Assert.Equal(micros.Select(m => Epoch.AddTicks(m * 10)), await ReadAsync<DateTime>(path, micros.Length));
    }

    [Theory]
    [InlineData(PS.Encoding.DeltaBinaryPacked)]
    [InlineData(PS.Encoding.ByteStreamSplit)]
    public async Task Dates(PS.Encoding encoding) {
        int[] days = { 0, 1, -1, 19_000 };
        using var node = new PSchema.PrimitiveNode("d", PS.Repetition.Required, PS.LogicalType.Date(), PS.PhysicalType.Int32);
        string path = Write(node, encoding, w => ((PS.ColumnWriter<int>)w).WriteBatch(days));
        Assert.Equal(days.Select(d => Epoch.AddDays(d)), await ReadAsync<DateTime>(path, days.Length));
    }

    [Theory]
    [InlineData(PS.Encoding.DeltaBinaryPacked)]
    [InlineData(PS.Encoding.ByteStreamSplit)]
    public async Task Integer_backed_decimals(PS.Encoding encoding) {
        long[] unscaled = { 1999, -1, 0, 999_999_999_999 };
        using var node = new PSchema.PrimitiveNode("m", PS.Repetition.Required, PS.LogicalType.Decimal(18, 2), PS.PhysicalType.Int64);
        string path = Write(node, encoding, w => ((PS.ColumnWriter<long>)w).WriteBatch(unscaled));
        Assert.Equal(new[] { 19.99m, -0.01m, 0m, 9_999_999_999.99m }, await ReadAsync<decimal>(path, unscaled.Length));
    }

    [Fact]
    public async Task Int8_under_delta() {
        int[] values = { -128, 127, 0, -1 };
        using var node = new PSchema.PrimitiveNode("b", PS.Repetition.Required, PS.LogicalType.Int(8, true), PS.PhysicalType.Int32);
        string path = Write(node, PS.Encoding.DeltaBinaryPacked, w => ((PS.ColumnWriter<int>)w).WriteBatch(values));
        Assert.Equal(new sbyte[] { -128, 127, 0, -1 }, await ReadAsync<sbyte>(path, values.Length));
    }

    [Theory]
    [InlineData(PS.Encoding.ByteStreamSplit)]
    [InlineData(PS.Encoding.DeltaByteArray)]
    public async Task Uuids(PS.Encoding encoding) {
        Guid[] guids = { Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), Guid.Empty, Guid.Parse("00112233-4455-6677-8899-aabbccddee00") };
        using var node = new PSchema.PrimitiveNode("u", PS.Repetition.Required, PS.LogicalType.Uuid(), PS.PhysicalType.FixedLenByteArray, 16);
        string path = Write(node, encoding, w => WriteFixed((PS.ColumnWriter<PS.FixedLenByteArray>)w, guids.Select(g => g.ToByteArray(bigEndian: true)).ToArray()));
        Assert.Equal(guids, await ReadAsync<Guid>(path, guids.Length));
    }

    static void WriteFixed(PS.ColumnWriter<PS.FixedLenByteArray> writer, byte[][] values) {
        IntPtr block = Marshal.AllocHGlobal(values.Sum(v => v.Length));
        try {
            int offset = 0;
            var pointers = new PS.FixedLenByteArray[values.Length];
            for(int i = 0; i < values.Length; i++) {
                Marshal.Copy(values[i], 0, block + offset, values[i].Length);
                pointers[i] = new PS.FixedLenByteArray(block + offset);
                offset += values[i].Length;
            }
            writer.WriteBatch(pointers);
        } finally {
            Marshal.FreeHGlobal(block);
        }
    }
}
