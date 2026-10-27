using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// Footer statistics of INT64 timestamps are in the column's own unit. They used to be written in milliseconds whatever
/// the unit, so readers that prune by statistics (DuckDB, Spark, Arrow) skipped row groups holding matching rows.
/// </summary>
public class TimestampStatisticsTest {

    [Theory]
    [InlineData(DateTimeTimeUnit.Millis, 2_000L)]
    [InlineData(DateTimeTimeUnit.Micros, 2_000_000L)]
    [InlineData(DateTimeTimeUnit.Nanos, 2_000_000_000L)]
    public async Task Statistics_are_in_the_columns_unit(DateTimeTimeUnit unit, long twoSeconds) {
        var field = new DateTimeDataField("ts", DateTimeFormat.Timestamp, unit: unit, isNullable: false);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<DateTime>(field, new[] { DateTime.UnixEpoch, DateTime.UnixEpoch.AddSeconds(2) }.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Meta.Statistics stats = reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
        Assert.Equal(0L, BitConverter.ToInt64(stats.MinValue!, 0));
        Assert.Equal(twoSeconds, BitConverter.ToInt64(stats.MaxValue!, 0));
    }
}

public class UuidStatisticsTest {

    [Fact]
    public async Task Uuid_statistics_are_in_the_values_byte_order() {
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
        Meta.Statistics stats = reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
        Assert.Equal(low.ToByteArray(bigEndian: true), stats.MinValue);
        Assert.Equal(high.ToByteArray(bigEndian: true), stats.MaxValue);
    }
}

public class WriteStatisticsOptionTest {

    [Fact]
    public async Task Statistics_can_be_left_out() {
        var field = new DataField<long>("id");
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, new ParquetOptions { WriteStatistics = false })) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<long>(field, new long[] { 1, 2, 3 }.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.Null(reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics);
        var values = new long[3];
        await reader.OpenRowGroupReader(0).ReadRawAsync<long>(reader.Schema.DataFields[0], values, null, null);
        Assert.Equal(new long[] { 1, 2, 3 }, values);
    }
}
