using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>Columns encoded BYTE_STREAM_SPLIT get the same min/max as plain-encoded columns.</summary>
public class ByteStreamSplitStatisticsTest {

    static async Task<Statistics> StatisticsOf<T>(params T[] values) where T : struct {
        var field = new DataField<T>("c");
        var options = new ParquetOptions { ColumnEncodingHints = { ["c"] = EncodingHint.ByteSplitStream } };
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<T>(field, values.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.Contains(Encoding.BYTE_STREAM_SPLIT, reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Encodings);
        return reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
    }

    [Fact]
    public async Task Doubles() {
        Statistics st = await StatisticsOf(3.0, -1.0, 2.0);
        Assert.Equal(-1.0, BitConverter.ToDouble(st.MinValue!));
        Assert.Equal(3.0, BitConverter.ToDouble(st.MaxValue!));
    }

    [Fact]
    public async Task Ints() {
        Statistics st = await StatisticsOf(7, -4, 0);
        Assert.Equal(-4, BitConverter.ToInt32(st.MinValue!));
        Assert.Equal(7, BitConverter.ToInt32(st.MaxValue!));
    }
}
