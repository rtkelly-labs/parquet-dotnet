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
