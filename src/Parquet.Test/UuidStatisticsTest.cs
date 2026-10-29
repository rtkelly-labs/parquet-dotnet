using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// UUID statistics are the values' RFC 4122 (big-endian) bytes, and min/max are chosen in that byte order, as the format
/// orders FIXED_LEN_BYTE_ARRAY columns.
/// </summary>
public class UuidStatisticsTest {

    static async Task<Meta.Statistics> StatisticsOf(params Guid[] values) {
        var field = new DataField<Guid>("id");
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<Guid>(field, values.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        return reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
    }

    [Fact]
    public async Task Bounds_are_in_the_values_byte_order() {
        var low = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var high = Guid.Parse("ff112233-4455-6677-8899-aabbccddeeff");
        Meta.Statistics stats = await StatisticsOf(high, low);
        Assert.Equal(low.ToByteArray(bigEndian: true), stats.MinValue);
        Assert.Equal(high.ToByteArray(bigEndian: true), stats.MaxValue);
    }

    [Fact]
    public async Task Min_and_max_are_chosen_by_those_bytes() {
        // Guid.CompareTo compares the mixed-endian fields as numbers; byte order differs from it
        var low = Guid.Parse("01000000-0000-0000-0000-0000000000ff");
        var high = Guid.Parse("ff000000-0000-0000-0000-000000000001");
        Meta.Statistics stats = await StatisticsOf(high, low);
        Assert.Equal(low.ToByteArray(bigEndian: true), stats.MinValue);
        Assert.Equal(high.ToByteArray(bigEndian: true), stats.MaxValue);
    }

    [Fact]
    public async Task Bounds_read_back_as_the_uuids() {
        var field = new DataField<Guid>("id");
        var low = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var high = Guid.Parse("ff112233-4455-6677-8899-aabbccddeeff");
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<Guid>(field, new[] { high, low }.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Data.DataColumnStatistics st = reader.OpenRowGroupReader(0).GetStatistics(reader.Schema.GetDataFields()[0])!;
        Assert.Equal((low, high), ((Guid)st.MinValue!, (Guid)st.MaxValue!));
    }
}
