using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// Min/max are written in the order the format defines for each type (parquet.thrift ColumnOrder), and the footer says
/// so with column_orders.
/// </summary>
public class StatisticsSortOrderTest {

    static async Task<FileMetaData> Write<T>(DataField field, params T[] values) where T : struct {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<T>(field, values.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        return reader.Metadata!;
    }

    static async Task<Statistics> StatisticsOf<T>(params T[] values) where T : struct =>
        (await Write(new DataField<T>("c"), values)).RowGroups[0].Columns[0].MetaData!.Statistics!;

    static async Task<Statistics> StatisticsOf(DataField field, params object?[] values) {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            if(values[0] is string)
                await rg.WriteAsync(field, values.Cast<string?>().ToArray());
            else
                await rg.WriteAsync(field, values.Cast<byte[]?>().ToArray());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        return reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
    }

    [Fact]
    public async Task Strings_compare_by_code_point() {
        // U+FF61 is below U+1F600 as a code point (and in UTF-8 bytes), above its surrogate D83D as a UTF-16 code unit
        Statistics st = await StatisticsOf(new DataField<string>("s"), "\U0001F600", "｡");
        Assert.Equal("｡", System.Text.Encoding.UTF8.GetString(st.MinValue!));
        Assert.Equal("\U0001F600", System.Text.Encoding.UTF8.GetString(st.MaxValue!));
    }

    [Fact]
    public async Task Byte_arrays_get_unsigned_bytewise_bounds() {
        Statistics st = await StatisticsOf(new DataField<byte[]>("b"), new byte[] { 2 }, new byte[] { 0xFF }, new byte[] { 1, 5 });
        Assert.Equal(new byte[] { 1, 5 }, st.MinValue);
        Assert.Equal(new byte[] { 0xFF }, st.MaxValue);
    }

    [Fact]
    public async Task Booleans_get_bounds() {
        Statistics st = await StatisticsOf(true, false, true);
        Assert.Equal(new byte[] { 0 }, st.MinValue);
        Assert.Equal(new byte[] { 1 }, st.MaxValue);
    }

    [Fact]
    public async Task NaN_is_never_a_bound() {
        Statistics st = await StatisticsOf(double.NaN, 2.0, 1.0);
        Assert.Equal(1.0, BitConverter.ToDouble(st.MinValue!));
        Assert.Equal(2.0, BitConverter.ToDouble(st.MaxValue!));

        Statistics allNaN = await StatisticsOf(float.NaN, float.NaN);
        Assert.Null(allNaN.MinValue);
        Assert.Null(allNaN.MaxValue);
    }

    [Fact]
    public async Task Zero_bounds_are_signed_zeros() {
        Statistics st = await StatisticsOf(0.0, 0.0);
        Assert.True(double.IsNegative(BitConverter.ToDouble(st.MinValue!)));
        Assert.False(double.IsNegative(BitConverter.ToDouble(st.MaxValue!)));
    }

    [Fact]
    public async Task Footer_declares_type_defined_order_for_each_column() {
        FileMetaData meta = await Write(new DataField<int>("i"), 1, 2);
        ColumnOrder order = Assert.Single(meta.ColumnOrders!);
        Assert.NotNull(order.TYPEORDER);
    }

    [Fact]
    public async Task Floating_point_columns_count_their_NaNs() {
        // required with TYPE_ORDER, even when zero
        Assert.Equal(2, (await StatisticsOf(double.NaN, 1.0, double.NaN)).NanCount);
        Assert.Equal(0, (await StatisticsOf(1f, 2f)).NanCount);
        Assert.Null((await StatisticsOf(1, 2)).NanCount);
    }

    [Fact]
    public async Task Deprecated_min_max_only_for_signed_order_columns() {
        Statistics ints = await StatisticsOf(1, 2);
        Assert.Equal(ints.MinValue, ints.Min);
        Assert.Equal(ints.MaxValue, ints.Max);

        Statistics strings = await StatisticsOf(new DataField<string>("s"), "a", "b");
        Assert.NotNull(strings.MinValue);
        Assert.Null(strings.Min);
        Assert.Null(strings.Max);
    }
}
