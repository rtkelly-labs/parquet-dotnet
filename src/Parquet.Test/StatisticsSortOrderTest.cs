using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.File;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>Statistics with their sort order, raw bytes and physical values, and the rules for using min and max.</summary>
public class StatisticsSortOrderTest : TestBase {

    static readonly DataField<int> Id = new("id");
    static readonly DataField<ulong> Unsigned = new("u");
    static readonly DataField<string> Text = new("s");
    static readonly DecimalDataField Amount = new("d", 9, 2);
    static readonly DataField<Guid> Uuid = new("g");

    static async Task<MemoryStream> WriteAsync() {
        var ms = new MemoryStream();
        var schema = new ParquetSchema(Id, Unsigned, Text, Amount, Uuid);
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<int>(Id, new[] { 3, -5, 7 });
            await rg.WriteAsync<ulong>(Unsigned, new[] { 1UL, ulong.MaxValue, 2UL });
            await rg.WriteAsync(Text, new[] { "b", "a", "c" });
            await rg.WriteAsync<decimal>(Amount, new[] { 1.25m, -3.50m, 2.00m });
            await rg.WriteAsync<Guid>(Uuid, new[] {
                Guid.Parse("00000000-0000-0000-0000-000000000002"),
                Guid.Parse("ff000000-0000-0000-0000-000000000000"),
                Guid.Parse("00000000-0000-0000-0000-000000000001") });
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Min_max_with_sort_order_and_physical_values() {
        using MemoryStream ms = await WriteAsync();
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataField[] fields = reader.Schema.GetDataFields();

        DataColumnStatistics id = rg.GetStatistics(fields[0])!;
        Assert.Equal(ColumnSortOrder.Signed, id.SortOrder);
        Assert.True(id.HasMinMax);
        Assert.False(id.IsLegacyMinMax);
        Assert.Equal((-5, 7), ((int)id.PhysicalMin!, (int)id.PhysicalMax!));
        Assert.Equal(BitConverter.GetBytes(-5), id.MinBytes);

        DataColumnStatistics u = rg.GetStatistics(fields[1])!;
        Assert.Equal(ColumnSortOrder.Unsigned, u.SortOrder);
        Assert.Equal((1UL, ulong.MaxValue), ((ulong)u.MinValue!, (ulong)u.MaxValue!));
        Assert.Equal(-1L, (long)u.PhysicalMax!);    // the bits, as the physical INT64

        DataColumnStatistics s = rg.GetStatistics(fields[2])!;
        Assert.Equal(ColumnSortOrder.Unsigned, s.SortOrder);
        Assert.Equal(("a", "c"), ((string)s.MinValue!, (string)s.MaxValue!));
        Assert.Equal("a"u8.ToArray(), (byte[])s.PhysicalMin!);

        DataColumnStatistics d = rg.GetStatistics(fields[3])!;
        Assert.Equal(ColumnSortOrder.Signed, d.SortOrder);
        Assert.Equal((-3.50m, 2.00m), ((decimal)d.MinValue!, (decimal)d.MaxValue!));
        Assert.Equal(-350, d.PhysicalMin);

        DataColumnStatistics g = rg.GetStatistics(fields[4])!;
        Assert.Equal(ColumnSortOrder.Unsigned, g.SortOrder);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), g.MinValue);
        Assert.Equal(Guid.Parse("ff000000-0000-0000-0000-000000000000"), g.MaxValue);
    }

    /// <summary>The file again, with its footer changed by <paramref name="change"/>.</summary>
    static async Task<MemoryStream> RewriteFooterAsync(MemoryStream file, Action<FileMetaData> change) {
        FileMetaData meta;
        await using(ParquetReader reader = await ParquetReader.CreateAsync(file, leaveStreamOpen: true))
            meta = reader.Metadata!;
        change(meta);
        byte[] bytes = file.ToArray();
        int footerLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        var result = new MemoryStream();
        result.Write(bytes, 0, bytes.Length - 8 - footerLength);
        long written = new ThriftFooter(meta).Write(result);
        result.Write(BitConverter.GetBytes((int)written));
        result.Write("PAR1"u8);
        result.Position = 0;
        return result;
    }

    [Fact]
    public async Task Legacy_min_max_only_for_signed_numeric_columns() {
        using MemoryStream original = await WriteAsync();
        using MemoryStream ms = await RewriteFooterAsync(original, meta => {
            foreach(ColumnChunk cc in meta.RowGroups[0].Columns) {
                Statistics st = cc.MetaData!.Statistics!;
                (st.Min, st.Max, st.MinValue, st.MaxValue) = (st.MinValue, st.MaxValue, null, null);
            }
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataField[] fields = reader.Schema.GetDataFields();

        DataColumnStatistics id = rg.GetStatistics(fields[0])!;
        Assert.True(id.IsLegacyMinMax);
        Assert.Equal((-5, 7), ((int)id.MinValue!, (int)id.MaxValue!));

        // a decimal stored as INT32 compares as its unscaled int
        Assert.True(rg.GetStatistics(fields[3])!.IsLegacyMinMax);

        // unsigned and byte-array columns: legacy min/max were compared signed, so not used
        foreach(DataField f in new[] { fields[1], fields[2], fields[4] }) {
            DataColumnStatistics st = rg.GetStatistics(f)!;
            Assert.False(st.HasMinMax, f.Name);
            Assert.Null(st.MinValue);
            Assert.Equal(0, st.NullCount);
        }
    }

    [Fact]
    public async Task Undefined_column_order_makes_min_max_unusable() {
        using MemoryStream original = await WriteAsync();
        using MemoryStream ms = await RewriteFooterAsync(original, meta =>
            meta.ColumnOrders = [new ColumnOrder(), new ColumnOrder { TYPEORDER = new TypeDefinedOrder() },
                new ColumnOrder { TYPEORDER = new TypeDefinedOrder() }, new ColumnOrder { TYPEORDER = new TypeDefinedOrder() },
                new ColumnOrder { TYPEORDER = new TypeDefinedOrder() }]);
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataField[] fields = reader.Schema.GetDataFields();
        DataColumnStatistics id = rg.GetStatistics(fields[0])!;
        Assert.Equal(ColumnSortOrder.Unknown, id.SortOrder);
        Assert.False(id.HasMinMax);
        Assert.True(rg.GetStatistics(fields[1])!.HasMinMax);
    }

    [Fact]
    public async Task Statistics_written_by_parquet_cpp() {
        using Stream s = OpenTestFile("legacy/three_level.parquet");
        await using ParquetReader reader = await ParquetReader.CreateAsync(s);
        Assert.NotNull(reader.Metadata!.ColumnOrders);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataColumnStatistics st = rg.GetStatistics(reader.Schema.GetDataFields()[0])!;
        Assert.Equal(ColumnSortOrder.Signed, st.SortOrder);
        Assert.False(st.IsLegacyMinMax);
        Assert.Equal((1, 3), ((int)st.MinValue!, (int)st.MaxValue!));
        Assert.Equal(2, st.NullCount);    // the empty list and the null list
    }

    [Theory]
    [InlineData(Meta.Type.INT96, null, ColumnSortOrder.Unknown)]
    [InlineData(Meta.Type.FIXED_LEN_BYTE_ARRAY, ConvertedType.INTERVAL, ColumnSortOrder.Unknown)]
    [InlineData(Meta.Type.FIXED_LEN_BYTE_ARRAY, ConvertedType.DECIMAL, ColumnSortOrder.Signed)]
    [InlineData(Meta.Type.INT32, ConvertedType.UINT_16, ColumnSortOrder.Unsigned)]
    [InlineData(Meta.Type.INT32, ConvertedType.DATE, ColumnSortOrder.Signed)]
    [InlineData(Meta.Type.BOOLEAN, null, ColumnSortOrder.Signed)]
    [InlineData(Meta.Type.BYTE_ARRAY, ConvertedType.UTF8, ColumnSortOrder.Unsigned)]
    public void Sort_order_by_type(Meta.Type type, ConvertedType? converted, ColumnSortOrder expected) =>
        Assert.Equal(expected, ColumnStatisticsRules.SortOrder(new SchemaElement { Type = type, ConvertedType = converted }));
}
