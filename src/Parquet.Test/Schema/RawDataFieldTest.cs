using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Schema;

/// <summary>Columns declared exactly as stored, written and read as physical values, with statistics in each column's sort order.</summary>
public class RawDataFieldTest {

    static async Task<(ParquetReader reader, MemoryStream ms)> RoundTrip<T>(RawDataField field, T[] values) where T : struct {
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync<T>(field, values);
        }
        ms.Position = 0;
        return (await ParquetReader.CreateAsync(ms), ms);
    }

    [Fact]
    public async Task Writes_exactly_the_declared_schema_element() {
        var field = new RawDataField("ts", Meta.Type.INT64, isNullable: false,
            new LogicalType { TIMESTAMP = new TimestampType { IsAdjustedToUTC = false, Unit = new TimeUnit { NANOS = new NanoSeconds() } } }) { FieldId = 7 };
        (ParquetReader reader, MemoryStream ms) = await RoundTrip(field, new[] { 5L, -3L, 1_000_000_000_123L });
        await using(reader) {
            SchemaElement se = reader.Metadata!.Schema[1];
            Assert.Equal((Meta.Type.INT64, FieldRepetitionType.REQUIRED, 7), (se.Type, se.RepetitionType, se.FieldId));
            Assert.NotNull(se.LogicalType!.TIMESTAMP!.Unit.NANOS);
            Assert.Null(se.ConvertedType);
            DataField df = reader.Schema.GetDataFields()[0];
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
            long[] back = new long[3];
            await rg.ReadRawAsync<long>(df, back, null, null);
            Assert.Equal(new[] { 5L, -3L, 1_000_000_000_123L }, back);
            DataColumnStatistics st = rg.GetStatistics(df)!;
            Assert.Equal((-3L, 1_000_000_000_123L), ((long)st.PhysicalMin!, (long)st.PhysicalMax!));
        }
    }

    [Fact]
    public async Task Unsigned_columns_get_unsigned_bounds() {
        var u32 = new RawDataField("u", Meta.Type.INT32, false, new LogicalType { INTEGER = new IntType { BitWidth = 32, IsSigned = false } });
        (ParquetReader reader, _) = await RoundTrip(u32, new[] { 1, unchecked((int)4_000_000_000u), 7 });
        await using(reader) {
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
            DataColumnStatistics st = rg.GetStatistics(reader.Schema.GetDataFields()[0])!;
            Assert.Equal(ColumnSortOrder.Unsigned, st.SortOrder);
            Assert.Equal((1u, 4_000_000_000u), ((uint)st.MinValue!, (uint)st.MaxValue!));
        }
        var u64 = new RawDataField("u", Meta.Type.INT64, false, new LogicalType { INTEGER = new IntType { BitWidth = 64, IsSigned = false } });
        (reader, _) = await RoundTrip(u64, new[] { 3L, -1L, 9L });
        await using(reader) {
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
            DataColumnStatistics st = rg.GetStatistics(reader.Schema.GetDataFields()[0])!;
            Assert.Equal((3UL, ulong.MaxValue), ((ulong)st.MinValue!, (ulong)st.MaxValue!));
        }
    }

    [Fact]
    public async Task Byte_arrays_get_unsigned_bytewise_bounds() {
        var s = new RawDataField("s", Meta.Type.BYTE_ARRAY, false, new LogicalType { STRING = new StringType() }, ConvertedType.UTF8);
        byte[][] values = new[] { "b", "é", "a", "\U0001F600" }.Select(System.Text.Encoding.UTF8.GetBytes).ToArray();
        (ParquetReader reader, _) = await RoundTrip(s, values.Select(v => (ReadOnlyMemory<byte>)v).ToArray());
        await using(reader) {
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
            DataColumnStatistics st = rg.GetStatistics(reader.Schema.GetDataFields()[0])!;
            Assert.Equal(("a", "\U0001F600"), ((string)st.MinValue!, (string)st.MaxValue!));
        }
        var fixedDecimal = new RawDataField("d", Meta.Type.FIXED_LEN_BYTE_ARRAY, false,
            new LogicalType { DECIMAL = new DecimalType { Precision = 20, Scale = 2 } }, typeLength: 9);
        (reader, _) = await RoundTrip(fixedDecimal, new ReadOnlyMemory<byte>[] { new byte[9], Enumerable.Repeat((byte)0xFF, 9).ToArray() });
        await using(reader) {
            Assert.Equal((20, 2, 9), (reader.Metadata!.Schema[1].Precision, reader.Metadata.Schema[1].Scale, reader.Metadata.Schema[1].TypeLength));
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
            // signed values compared as bytes would be wrong: no bounds
            Assert.False(rg.GetStatistics(reader.Schema.GetDataFields()[0])!.HasMinMax);
        }
    }

    [Fact]
    public async Task Strings_compare_by_code_point_and_uuids_by_their_bytes() {
        var s = new DataField<string>("s");
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(s), ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            // U+FFFD sorts below U+1F600 by code point (and in UTF-8), above it in UTF-16 code units
            await rg.WriteAsync(s, new[] { "\U0001F600", "�" });
        }
        ms.Position = 0;
        await using(ParquetReader r = await ParquetReader.CreateAsync(ms)) {
            DataColumnStatistics st = r.OpenRowGroupReader(0).GetStatistics(r.Schema.GetDataFields()[0])!;
            Assert.Equal(("�", "\U0001F600"), ((string)st.MinValue!, (string)st.MaxValue!));
        }

        var g = new DataField<Guid>("g");
        Guid low = Guid.Parse("01000000-0000-0000-0000-0000000000ff"), high = Guid.Parse("ff000000-0000-0000-0000-000000000001");
        ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(g), ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync<Guid>(g, new[] { high, low });
        }
        ms.Position = 0;
        await using(ParquetReader r = await ParquetReader.CreateAsync(ms)) {
            DataColumnStatistics st = r.OpenRowGroupReader(0).GetStatistics(r.Schema.GetDataFields()[0])!;
            Assert.Equal((low, high), ((Guid)st.MinValue!, (Guid)st.MaxValue!));
        }
    }

    [Fact]
    public async Task Float_bounds_skip_nan_and_sign_zeros() {
        var d = new DataField<double>("d");
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(d), ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync<double>(d, new[] { double.NaN, 0.0, -0.0, 0.0 });
        }
        ms.Position = 0;
        await using ParquetReader r = await ParquetReader.CreateAsync(ms);
        DataColumnStatistics st = r.OpenRowGroupReader(0).GetStatistics(r.Schema.GetDataFields()[0])!;
        Assert.True(double.IsNegative((double)st.MinValue!) && (double)st.MinValue! == 0);
        Assert.True(!double.IsNegative((double)st.MaxValue!) && (double)st.MaxValue! == 0);
    }

    [Fact]
    public async Task Text_columns_take_strings_and_maps_keep_their_repetition() {
        var name = new RawDataField("name", Meta.Type.BYTE_ARRAY, true, new LogicalType { STRING = new StringType() }, ConvertedType.UTF8);
        var key = new RawDataField("key", Meta.Type.BYTE_ARRAY, false, new LogicalType { STRING = new StringType() }, ConvertedType.UTF8);
        var value = new RawDataField("value", Meta.Type.INT64, true);
        var map = new MapField("m", key, value, isNullable: false);
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(name, map), ms,
            new ParquetOptions { ColumnEncodingHints = { ["name"] = EncodingHint.Dictionary } })) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync(name, new[] { "a", null, "a" });
            // rows: {x: 1}, {}, {y: null}
            await rg.WriteAllPartsAsync<ReadOnlyMemory<char>>(key, new[] { "x".AsMemory(), "y".AsMemory() }, new[] { 1, 0, 1 }, new[] { 0, 0, 0 }, default);
            await rg.WriteAllPartsAsync<long>(value, new[] { 1L }, new[] { 2, 0, 1 }, new[] { 0, 0, 0 }, default);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.Equal(FieldRepetitionType.REQUIRED, reader.Metadata!.Schema[2].RepetitionType);
        var encodings = reader.Metadata.RowGroups[0].Columns[0].MetaData!.Encodings;
        Assert.True(encodings.Contains(Meta.Encoding.PLAIN_DICTIONARY) || encodings.Contains(Meta.Encoding.RLE_DICTIONARY));   // strings dictionary-encoded
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.Equal((1, 1), (fields[1].MaxDefinitionLevel, fields[1].MaxRepetitionLevel));
        Assert.Equal((2, 1), (fields[2].MaxDefinitionLevel, fields[2].MaxRepetitionLevel));
        using ParquetRowGroupReader rg2 = reader.OpenRowGroupReader(0);
        var keys = new ReadOnlyMemory<char>[3];
        var defs = new int[3];
        var reps = new int[3];
        await rg2.ReadRawAsync<ReadOnlyMemory<char>>(fields[1], keys, defs, reps);
        Assert.Equal(new[] { 1, 0, 1 }, defs);
        Assert.Equal(new[] { "x", "y" }, keys.Take(2).Select(k => k.ToString()));
    }

    [Fact]
    public async Task Byte_array_columns_are_not_repeated() {
        var b = new RawDataField("b", Meta.Type.BYTE_ARRAY, true);
        var f = new RawDataField("f", Meta.Type.FIXED_LEN_BYTE_ARRAY, false, typeLength: 2);
        Assert.False(b.IsArray);
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(f, b), ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAllPartsAsync<ReadOnlyMemory<byte>>(f, new ReadOnlyMemory<byte>[] { new byte[] { 1, 2 }, new byte[] { 3, 4 } }, null, null, default);
            await rg.WriteAllPartsAsync<ReadOnlyMemory<byte>>(b, new ReadOnlyMemory<byte>[] { new byte[] { 5 } }, new[] { 0, 1 }, null, default);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.Equal(2, reader.RowGroups[0].RowCount);
        Assert.Equal(FieldRepetitionType.OPTIONAL, reader.Metadata!.Schema[2].RepetitionType);
    }

    [Fact]
    public async Task Boolean_columns_get_bounds() {
        var b = new DataField<bool?>("b");
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(b), ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync<bool>(b, new bool?[] { true, null, false });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        DataColumnStatistics st = reader.OpenRowGroupReader(0).GetStatistics(reader.Schema.GetDataFields()[0])!;
        Assert.Equal((false, true, 1L), ((bool)st.MinValue!, (bool)st.MaxValue!, st.NullCount!.Value));
    }

    [Fact]
    public async Task Text_written_as_chars_gets_bounds() {
        var s = new RawDataField("s", Meta.Type.BYTE_ARRAY, true, new LogicalType { STRING = new StringType() }, ConvertedType.UTF8);
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(s), ms,
            new ParquetOptions { ColumnEncodingHints = { ["s"] = EncodingHint.Dictionary } })) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAllPartsAsync<ReadOnlyMemory<char>>(s, new[] { "s0".AsMemory(), "\U0001F600 emoji".AsMemory() }, new[] { 1, 0, 1 }, null, default);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        DataColumnStatistics st = reader.OpenRowGroupReader(0).GetStatistics(reader.Schema.GetDataFields()[0])!;
        Assert.Equal(("s0", "\U0001F600 emoji", 1L), ((string)st.MinValue!, (string)st.MaxValue!, st.NullCount!.Value));
    }
}
