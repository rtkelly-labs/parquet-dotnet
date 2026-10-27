using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

public class FixedLengthByteArrayTest {

    private static async Task<MemoryStream> WriteAsync(DataField field, byte[]?[] values) {
        var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync(field, values);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Byte_arrays_of_a_fixed_length_round_trip_as_FIXED_LEN_BYTE_ARRAY() {
        var field = new DataField<byte[]>("hash") { FixedLength = 4 };
        byte[]?[] values = { new byte[] { 1, 2, 3, 4 }, null, new byte[] { 9, 9, 9, 9 }, new byte[] { 1, 2, 3, 4 } };
        using MemoryStream file = await WriteAsync(field, values);

        await using ParquetReader reader = await ParquetReader.CreateAsync(file, leaveStreamOpen: true);
        Meta.SchemaElement se = reader.Metadata!.Schema[1];
        Assert.Equal(Meta.Type.FIXED_LEN_BYTE_ARRAY, se.Type);
        Assert.Equal(4, se.TypeLength);
        DataField read = reader.Schema.DataFields[0];
        Assert.Equal(4, read.FixedLength);

        var raw = new ReadOnlyMemory<byte>[values.Length];
        var defs = new int[values.Length];
        await reader.OpenRowGroupReader(0).ReadRawAsync<ReadOnlyMemory<byte>>(read, raw, defs, null);
        Assert.Equal(new[] { 1, 0, 1, 1 }, defs);
        Assert.Equal(values.Where(v => v != null).Select(v => v!), raw.Take(3).Select(r => r.ToArray()));
        // byte-wise bounds, as FIXED_LEN_BYTE_ARRAY sorts unsigned
        Data.DataColumnStatistics st = reader.OpenRowGroupReader(0).GetStatistics(read)!;
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, (byte[])st.MinValue!);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, (byte[])st.MaxValue!);
        // 4 values of 4 bytes (or a dictionary of them), never a 4-byte length per value; plus those bounds.
        Assert.True(file.Length < 260);
    }

    [Fact]
    public async Task A_value_of_another_length_is_refused() {
        var field = new DataField<byte[]>("hash") { FixedLength = 4 };
        await Assert.ThrowsAsync<ParquetException>(() => WriteAsync(field, new byte[]?[] { new byte[] { 1, 2, 3 } }));
    }

    [Fact]
    public async Task Variable_length_byte_arrays_are_unchanged() {
        using MemoryStream file = await WriteAsync(new DataField<byte[]>("b"), new byte[]?[] { new byte[] { 1 }, new byte[] { 1, 2, 3 } });
        await using ParquetReader reader = await ParquetReader.CreateAsync(file);
        Assert.Equal(Meta.Type.BYTE_ARRAY, reader.Metadata!.Schema[1].Type);
        Assert.Null(reader.Schema.DataFields[0].FixedLength);
    }
}
