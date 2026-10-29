using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;
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

    [Fact]
    public async Task Statistics_are_written_by_default() {
        var field = new DataField<long>("id");
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<long>(field, new long[] { 1, 2, 3 }.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.NotNull(reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics);
    }
}
