using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Parquet.Data;
using Parquet.Encodings;
using Parquet.Extensions;
using Parquet.File;
using Parquet.File.Values.Primitives;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Parquet.Serialization.Attributes;
using Xunit;
using Encoding = Parquet.Meta.Encoding;
using Type = Parquet.Meta.Type;

namespace Parquet.Test.Coverage;

/// <summary>
/// The second round of lines the suite did not run (lake(0081)): encoder and decoder refusals for types and physical
/// types that do not go together, data types' guards, and schemas read from footers other writers could produce.
/// </summary>
public class Round2EncodingTest {

    // ---- BigDecimal ----

    [Fact]
    public void BigDecimal_refuses_a_scale_beyond_its_precision() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BigDecimal(BigInteger.One, 2, 3));

    [Fact]
    public void BigDecimal_from_decimal_needs_a_precision_and_defaults_the_scale() {
        Assert.Throws<ArgumentNullException>(() => BigDecimal.FromDecimal(1m, null, 2));
        BigDecimal d = BigDecimal.FromDecimal(42m, 10, null);
        Assert.Equal((new BigInteger(42), 10, 0), (d.UnscaledValue, d.Precision, d.Scale));
    }

    [Theory]
    [InlineData(22, 10)]
    [InlineData(23, 10)]
    [InlineData(24, 11)]
    [InlineData(26, 11)]
    [InlineData(32, 14)]
    [InlineData(33, 14)]
    [InlineData(34, 15)]
    [InlineData(35, 15)]
    public void BigDecimal_buffer_holds_the_precision(int precision, int bytes) {
        Assert.Equal(bytes, BigDecimal.GetBufferSize(precision));
        // the largest value of that precision fits, signed
        BigInteger max = BigInteger.Pow(10, precision) - 1;
        Assert.True(max.GetByteCount() <= bytes);
        Assert.True((-max).GetByteCount() <= bytes);
    }

    [Fact]
    public void BigDecimal_wider_than_its_buffer_is_refused() {
        var d = new BigDecimal(BigInteger.Pow(10, 30), 5, 0);   // 13 bytes in a 3-byte buffer
        Assert.Throws<NotSupportedException>(() => d.WriteBytes(new byte[16]));
    }

    // ---- small data types ----

    [Fact]
    public void Bloom_filter_size_needs_a_probability_between_0_and_1() {
        Assert.Throws<ArgumentOutOfRangeException>(() => BloomFilter.OptimalBytes(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BloomFilter.OptimalBytes(10, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BloomFilter.OptimalBytes(10, double.NaN));
    }

    [Fact]
    public void Statistics_start_empty_and_refuse_bounds_they_cannot_encode() {
        var empty = new DataColumnStatistics();
        Assert.Equal((null, null, null, null), (empty.NullCount, empty.DistinctCount, empty.MinValue, empty.MaxValue));

        var se = new SchemaElement { Name = "x", Type = Type.INT32 };
        Assert.Throws<ArgumentException>(() => new DataColumnStatistics(0, null, new object(), 1).ToThriftStatistics(se));
        Assert.Throws<ArgumentException>(() => new DataColumnStatistics(0, null, 1, new object()).ToThriftStatistics(se));
    }

    [Fact]
    public void Interval_prints_its_parts() => Assert.Equal("1m 2d 3ms", new Interval(1, 2, 3).ToString());

    [Fact]
    public void NanoTime_needs_twelve_bytes() => Assert.Throws<ArgumentException>(() => new NanoTime(new byte[5]));

    [Fact]
    public void Attributes_hold_what_they_are_given() {
        var dec = new ParquetDecimalAttribute(10, 2);
        Assert.Equal((10, 2), (dec.Precision, dec.Scale));
        Assert.True(new ParquetRequiredAttribute().IsRequired);
        Assert.False(new ParquetRequiredAttribute(false).IsRequired);
        Assert.True(new ParquetTimeAttribute().IsAdjustedToUtc);
        var ts = new ParquetTimestampAttribute(ParquetTimestampResolution.Microseconds, true, false);
        Assert.Equal((ParquetTimestampResolution.Microseconds, true, false), (ts.Resolution, ts.UseLogicalTimestamp, ts.IsAdjustedToUTC));
    }

    // ---- NestedLayout guards ----

    [Fact]
    public void Nested_layout_refuses_level_buffers_shorter_than_the_entries() {
        var schema = new ParquetSchema(new ListField("l", new DataField<int?>("x")));
        DataField leaf = schema.GetDataFields()[0];
        Assert.Throws<ArgumentException>(() => NestedLayout.FromLevels(schema, leaf, new int[1], new int[2], 2));
        Assert.Throws<ArgumentException>(() => NestedLayout.FromLevels(schema, leaf, new int[2], new int[1], 2));
    }

    [Fact]
    public void Nested_layout_levels_are_only_for_its_own_leaf() {
        var schema = new ParquetSchema(new ListField("l", new DataField<int?>("x")), new DataField<int?>("y"));
        DataField[] f = schema.GetDataFields();
        NestedLayout layout = NestedLayout.FromLevels(schema, f[0], [3], [0], 1);
        Assert.Throws<ArgumentException>(() => layout.ToLevels(schema, f[1]));
    }

    [Fact]
    public void Nested_layout_of_a_legacy_repeated_field() {
        var schema = new ParquetSchema(new DataField<int[]>("a"));
        DataField leaf = schema.GetDataFields()[0];
        // rows: [1, 2], [], [3]
        NestedLayout layout = NestedLayout.FromLevels(schema, leaf, [1, 1, 0, 1], [0, 1, 0, 0], 4);
        Assert.Equal(new[] { 0, 2, 2, 3 }, layout.Offsets[0]);
        Assert.Equal(3, layout.ValueCount);
        (int[] def, int[] rep) = layout.ToLevels(schema, leaf);
        Assert.Equal(new[] { 1, 1, 0, 1 }, def);
        Assert.Equal(new[] { 0, 1, 0, 0 }, rep);
    }

    // ---- RawColumnData ----

    [Fact]
    public void Raw_column_data_says_what_it_does_not_have() {
        var owner = CommunityToolkit.HighPerformance.Buffers.MemoryOwner<int>.Allocate(1);
        using var raw = new RawColumnData<int>(owner, null, null);
        Assert.Throws<InvalidOperationException>(() => raw.DefinitionLevels.Length);
        Assert.Throws<InvalidOperationException>(() => raw.RepetitionLevels.Length);
        Assert.Throws<InvalidOperationException>(() => raw.ValuesAsStrings);
    }

    // ---- bit packing ----

    [Fact]
    public void Bit_packing_a_short_group_into_a_short_buffer_pads_it() {
        // three values at width 2 into one byte: padded to a group of eight, the byte that fits written
        byte[] dest = new byte[1];
        int written = BitPackedEncoder.Encode8ValuesLE(new[] { 1, 2, 3 }, dest, 2);
        Assert.Equal(1, written);
        int[] back = new int[8];
        BitPackedEncoder.Unpack8ValuesLE(new byte[] { dest[0], 0 }, back, 2);
        Assert.Equal(new[] { 1, 2, 3, 0, 0, 0, 0, 0 }, back);

    }

    [Fact]
    public void Bit_packing_into_a_short_buffer_writes_what_fits() {
        byte[] dest = new byte[1];
        int written = BitPackedEncoder.Encode8ValuesLE(new[] { 1, 1, 1, 1, 1, 1, 1, 1 }, dest, 3);
        Assert.Equal(1, written);
    }

    // ---- RLE runs of equal values at two and three bytes ----

    [Theory]
    [InlineData(9, new[] { 300, 300, 300, 301 })]
    [InlineData(17, new[] { 70000, 70000, 70000, 5 })]
    public void Rle_runs_of_equal_wide_values(int bitWidth, int[] values) {
        using var ms = new MemoryStream();
        RleBitpackedHybridEncoder.Encode(ms, values.AsSpan(), bitWidth);
        int[] back = new int[values.Length];
        RleBitpackedHybridEncoder.Decode(ms.ToArray().AsSpan(), bitWidth, (int)ms.Length, out _, back.AsSpan(), back.Length);
        Assert.Equal(values, back);
    }

    // ---- delta decoders refuse types they cannot produce ----

    [Fact]
    public void Delta_decoders_refuse_other_types() {
        Assert.Throws<NotSupportedException>(() => DeltaBinaryPackedEncoder.Decode(new byte[8], new string[1], 0, 1, out _));
        Assert.Throws<NotSupportedException>(() => DeltaByteArrayEncoder.Decode<int>(new byte[8], new int[1], 1));
    }

    // ---- the PLAIN encoder ----

    static SchemaElement Se(Type type, LogicalType? logical = null, ConvertedType? converted = null, int? length = null) =>
        new() { Name = "x", Type = type, LogicalType = logical, ConvertedType = converted, TypeLength = length };

    [Fact]
    public void Plain_encoder_refuses_types_it_has_no_encoding_for() {
        using var ms = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => ParquetPlainEncoder.Encode<DateTimeOffset>(new DateTimeOffset[1], ms, Se(Type.INT64)));
        Assert.Throws<NotSupportedException>(() => ParquetPlainEncoder.Decode<DateTimeOffset>(new DateTimeOffset[1], 1, Se(Type.INT64), new byte[8], out _));
        Assert.False(ParquetPlainEncoder.TryEncode(new object(), Se(Type.INT32), out byte[]? none));
        Assert.Null(none);
    }

    [Fact]
    public void Plain_statistics_decode_by_their_physical_and_converted_types() {
        // INT96 as a timestamp when asked, a converted-only UINT_8 as a byte
        var ts = new DateTime(2024, 2, 29, 12, 0, 0, DateTimeKind.Utc);
        byte[] i96 = new NanoTime(ts).GetBytes();
        Assert.True(ParquetPlainEncoder.TryDecode(i96, Se(Type.INT96), new ParquetOptions { TreatBigIntegersAsDates = true }, out object? date));
        Assert.Equal(ts, (DateTime)date!, TimeSpan.FromSeconds(1));
        Assert.True(ParquetPlainEncoder.TryDecode(BitConverter.GetBytes(200), Se(Type.INT32, converted: ConvertedType.UINT_8), null, out object? u8));
        Assert.Equal((byte)200, u8);
        // a UUID of the wrong length has no CLR value
        Assert.False(ParquetPlainEncoder.TryDecode(new byte[] { 1, 2, 3 }, Se(Type.FIXED_LEN_BYTE_ARRAY, new LogicalType { UUID = new UUIDType() }, length: 16), null, out object? unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public void Plain_encoder_refuses_physical_types_that_cannot_hold_the_value() {
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.TryDecode(new byte[8],
            Se(Type.DOUBLE, new LogicalType { DECIMAL = new DecimalType { Precision = 9, Scale = 2 } }), null, out _));
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.TryEncode(DateTime.UtcNow, Se(Type.DOUBLE), out _));
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.TryEncode(new TimeOnly(1, 0), Se(Type.DOUBLE), out _));
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.TryEncode(1.5m, Se(Type.DOUBLE), out _));
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.TryEncode(TimeSpan.FromHours(1), Se(Type.DOUBLE), out _));

        using var ms = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.Encode(new[] { DateTime.UtcNow }, ms, Se(Type.DOUBLE)));
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.Encode(new[] { TimeSpan.Zero }, ms, Se(Type.DOUBLE)));
        Assert.Throws<NotSupportedException>(() => ParquetPlainEncoder.Decode(new byte[8], new DateTime[1], Se(Type.DOUBLE)));
        Assert.Throws<NotSupportedException>(() => ParquetPlainEncoder.Decode(new byte[8], new TimeOnly[1], Se(Type.DOUBLE)));
        Assert.Throws<NotSupportedException>(() => ParquetPlainEncoder.Decode(new byte[8], new TimeSpan[1], Se(Type.DOUBLE)));
    }

    [Fact]
    public void A_decimal_statistic_too_wide_for_its_column_is_refused() {
        var tse = Se(Type.INT32, new LogicalType { DECIMAL = new DecimalType { Precision = 9, Scale = 2 } });
        tse.Precision = 9;
        tse.Scale = 2;
        Assert.Throws<ParquetException>(() => ParquetPlainEncoder.TryEncode(decimal.MaxValue, tse, out _));
    }

    [Fact]
    public void Timestamps_with_no_unit_or_an_INT64_with_no_annotation_are_refused() {
        var noUnit = Se(Type.INT64, new LogicalType { TIMESTAMP = new TimestampType { IsAdjustedToUTC = true, Unit = new TimeUnit() } });
        using var ms = new MemoryStream();
        Assert.Throws<ParquetException>(() => ParquetPlainEncoder.Encode(new[] { DateTime.UtcNow }, ms, noUnit));
        Assert.Throws<ParquetException>(() => ParquetPlainEncoder.Decode(new byte[8], new DateTime[1], noUnit));
        Assert.Throws<ArgumentException>(() => ParquetPlainEncoder.Encode(new[] { DateTime.UtcNow }, ms, Se(Type.INT64)));
    }

    [Fact]
    public void Fixed_length_byte_pages_need_their_length_and_whole_values() {
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.Decode(new byte[6], new ReadOnlyMemory<byte>[2], Se(Type.FIXED_LEN_BYTE_ARRAY, length: 4)));
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.Decode(new byte[6], new ReadOnlyMemory<char>[2],
            Se(Type.FIXED_LEN_BYTE_ARRAY, new LogicalType { STRING = new StringType() })));
    }

    [Fact]
    public void Booleans_encode_the_same_without_hardware_acceleration() {
        bool[] values = Enumerable.Range(0, 200).Select(i => i % 3 == 0).ToArray();
        using var fast = new MemoryStream();
        ParquetPlainEncoder.Encode(values.AsSpan(), fast);
        bool before = ParquetOptions.UseHardwareAcceleration;
        try {
            ParquetOptions.UseHardwareAcceleration = false;
            using var slow = new MemoryStream();
            ParquetPlainEncoder.Encode(values.AsSpan(), slow);
            Assert.Equal(fast.ToArray(), slow.ToArray());
        } finally {
            ParquetOptions.UseHardwareAcceleration = before;
        }
    }

    // ---- Thrift ----

    [Fact]
    public void Thrift_reader_refuses_a_field_of_no_known_type() {
        var reader = new ThriftCompactProtocolReader(new MemoryStream(new byte[] { 0x0E, 0x02, 0x00 }));
        Assert.Throws<ThriftProtocolException>(() => KeyValue.Read(reader));
    }

    // ---- schemas from footers ----

    static ParquetSchema SchemaOf(params SchemaElement[] elements) =>
        new ThriftFooter(new FileMetaData { Version = 1, Schema = elements.ToList(), RowGroups = [] }).CreateModelSchema(new ParquetOptions());

    static SchemaElement Root(int children) => new() { Name = "schema", NumChildren = children };

    [Fact]
    public void A_map_container_without_a_key_and_a_value_is_refused() {
        Assert.Throws<IndexOutOfRangeException>(() => SchemaOf(Root(1),
            new SchemaElement { Name = "m", NumChildren = 1, ConvertedType = ConvertedType.MAP, RepetitionType = FieldRepetitionType.OPTIONAL },
            new SchemaElement { Name = "key_value", NumChildren = 1, RepetitionType = FieldRepetitionType.REPEATED },
            new SchemaElement { Name = "key", Type = Type.INT32, RepetitionType = FieldRepetitionType.REQUIRED }));
    }

    [Fact]
    public void Legacy_and_converted_only_types_are_read() {
        ParquetSchema schema = SchemaOf(Root(4),
            new SchemaElement { Name = "t", Type = Type.INT32, ConvertedType = ConvertedType.TIME_MILLIS },
            new SchemaElement { Name = "d", Type = Type.FIXED_LEN_BYTE_ARRAY, TypeLength = 5, ConvertedType = ConvertedType.DECIMAL, Precision = 10, Scale = 2 },
            new SchemaElement { Name = "s", Type = Type.INT32, ConvertedType = ConvertedType.INT_8 },
            new SchemaElement { Name = "i", Type = Type.INT32, ConvertedType = ConvertedType.UINT_16 });
        DataField[] f = schema.GetDataFields();
        Assert.Equal(new[] { typeof(int), typeof(decimal), typeof(sbyte), typeof(ushort) }, f.Select(x => x.ClrType));
    }

    [Fact]
    public void A_time_with_no_unit_is_refused() {
        Assert.ThrowsAny<Exception>(() => SchemaOf(Root(1),
            new SchemaElement { Name = "t", Type = Type.INT64, LogicalType = new LogicalType { TIME = new TimeType { Unit = new TimeUnit() } } }));
    }

    [Fact]
    public void A_column_of_no_physical_type_is_refused() {
        Assert.ThrowsAny<Exception>(() => SchemaOf(Root(1), new SchemaElement { Name = "x" }));
    }

    [Fact]
    public void A_timestamp_of_an_unknown_unit_cannot_be_encoded() {
        var field = new DateTimeDataField("t", DateTimeFormat.Timestamp, unit: (DateTimeTimeUnit)99);
        Assert.Throws<ParquetException>(() => SchemaEncoder.Encode(field, new ParquetOptions()));
    }

    [Fact]
    public void A_map_of_nullable_values_writes_its_values_optional() {
        var map = new MapField("m", new DataField("key", typeof(string), isNullable: false), new DataField<int?>("value"));
        var schema = new ParquetSchema(map);
        var elements = new List<SchemaElement>();
        var root = new SchemaElement { Name = "schema" };
        SchemaEncoder.Encode(map, root, elements, new ParquetOptions());
        Assert.Equal(FieldRepetitionType.OPTIONAL, elements.Single(e => e.Name == "value").RepetitionType);
        Assert.Equal(FieldRepetitionType.REQUIRED, elements.Single(e => e.Name == "key").RepetitionType);
    }

    // ---- footer lookups ----

    [Fact]
    public void Footer_lookups_refuse_null_and_miss_unknown_paths() {
        Assert.Throws<ArgumentNullException>(() => new ThriftFooter(null!, 0, new ParquetOptions()));
        var footer = new ThriftFooter(new ParquetSchema(new DataField<int>("id")), 0, new ParquetOptions());
        Assert.Throws<ArgumentNullException>(() => footer.GetSchemaElement(null!));
        Assert.Null(footer.GetSchemaElement(new ColumnChunk { MetaData = new ColumnMetaData { PathInSchema = ["nope"] } }));
    }

    // ---- schema and field equality ----

    [Fact]
    public void Schema_and_field_equality() {
        var schema = new ParquetSchema(new DataField<int>("id"));
        Assert.True(schema.Equals(schema));
        DataField id = schema.DataFields[0];
        Assert.False(id.Equals((object)"id"));
        Assert.False(id.Equals((SchemaElement)null!));
        // TimeOnly and TimeSpan TIME fields compare by their base type
        var a = new TimeDataField("t", TimeUnitPrecision.Micros, typeof(TimeOnly));
        var b = new TimeDataField("t", TimeUnitPrecision.Micros, typeof(TimeOnly));
        Assert.Equal(a, b);
    }

    // ---- small extensions ----

    [Fact]
    public void Reading_exactly_more_bytes_than_a_stream_has_fails() {
        Assert.Throws<IOException>(() => new MemoryStream(new byte[3]).ReadBytesExactly(4));
    }

    [Fact]
    public void Stream_wrapper_disposes_what_it_wraps() {
        var inner = new MemoryStream();
        var metered = new Parquet.Extensions.Streaming.MeteredWriteStream(inner);
        metered.Dispose();
        Assert.False(inner.CanWrite);
    }
}
