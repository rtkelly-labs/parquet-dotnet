using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// What parquet.thrift requires of statistics once column_orders declares TYPE_ORDER: nan_count on floating point
/// columns, and the deprecated (signed) min/max only where the column's order is signed.
/// </summary>
public class StatisticsConformanceTest {

    static async Task<Statistics> StatisticsOf<T>(DataField field, T[] values, ParquetOptions? options = null) where T : struct {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<T>(field, values.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        return reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
    }

    [Fact]
    public async Task Floating_point_columns_count_their_NaNs() {
        Statistics st = await StatisticsOf(new DataField<double>("d"), new[] { double.NaN, 1.0, double.NaN });
        Assert.Equal(2, st.NanCount);
        Assert.Equal(1.0, BitConverter.ToDouble(st.MinValue!));

        Statistics none = await StatisticsOf(new DataField<float>("f"), new[] { 1f, 2f });
        Assert.Equal(0, none.NanCount);
    }

    [Fact]
    public async Task Other_columns_have_no_nan_count() {
        Statistics st = await StatisticsOf(new DataField<int>("i"), new[] { 1, 2 });
        Assert.Null(st.NanCount);
    }

    [Fact]
    public async Task Deprecated_min_max_only_for_signed_order_columns() {
        Statistics ints = await StatisticsOf(new DataField<int>("i"), new[] { 1, 2 });
        Assert.Equal(ints.MinValue, ints.Min);
        Assert.Equal(ints.MaxValue, ints.Max);

        var s = new DataField<string>("s");
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(s), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync(s, new[] { "a", "b" });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Statistics strings = reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
        Assert.NotNull(strings.MinValue);
        Assert.Null(strings.Min);
        Assert.Null(strings.Max);
    }

    [Fact]
    public async Task Byte_stream_split_columns_get_bounds() {
        var field = new DataField<double>("d");
        var options = new ParquetOptions { ColumnEncodingHints = { ["d"] = EncodingHint.ByteSplitStream } };
        Statistics st = await StatisticsOf(field, new[] { 3.0, -1.0, 2.0 }, options);
        Assert.Equal(-1.0, BitConverter.ToDouble(st.MinValue!));
        Assert.Equal(3.0, BitConverter.ToDouble(st.MaxValue!));
    }
}
