using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>Statistics read back as the column's CLR type, as its values are.</summary>
public class StatisticsDecodingTest {

    static async Task<DataColumnStatistics> StatisticsOf<T>(DataField field, params T[] values) where T : struct {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<T>(field, values.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        return reader.OpenRowGroupReader(0).GetStatistics(reader.Schema.GetDataFields()[0])!;
    }

    static async Task AssertBounds<T>(T min, T max) where T : struct {
        DataColumnStatistics st = await StatisticsOf(new DataField<T>("c"), max, min);
        Assert.Equal((object)min, st.MinValue);
        Assert.Equal((object)max, st.MaxValue);
    }

    [Fact] public Task Bytes() => AssertBounds<byte>(1, 200);
    [Fact] public Task Sbytes() => AssertBounds<sbyte>(-100, 100);
    [Fact] public Task Shorts() => AssertBounds<short>(-30000, 30000);
    [Fact] public Task Ushorts() => AssertBounds<ushort>(1, 60000);
    [Fact] public Task Uints() => AssertBounds<uint>(1, uint.MaxValue);
    [Fact] public Task Ulongs() => AssertBounds<ulong>(1, ulong.MaxValue);

    [Fact]
    public async Task Decimals_stored_as_int32_and_int64() {
        DataColumnStatistics st32 = await StatisticsOf(new DecimalDataField("d", 5, 2), 123.45m, -1.5m);
        Assert.Equal((-1.5m, 123.45m), ((decimal)st32.MinValue!, (decimal)st32.MaxValue!));

        DataColumnStatistics st64 = await StatisticsOf(new DecimalDataField("d", 15, 3), 123456789.123m, 0.001m);
        Assert.Equal((0.001m, 123456789.123m), ((decimal)st64.MinValue!, (decimal)st64.MaxValue!));
    }
}
