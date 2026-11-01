using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Encodings;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;
using Type = Parquet.Meta.Type;

namespace Parquet.Test.Coverage;

/// <summary>
/// Encoders at their edges: RLE runs four bytes wide and runs that are empty or too wide, byte stream split without
/// hardware acceleration, delta encodings asked for types they do not take, and statistics values decoded from
/// footers by every annotation.
/// </summary>
public class EncodingEdgesTest {

    [Fact]
    public void RLE_three_and_four_byte_values_decode() {
        // header: 2 repeats (2 << 1), then the value in three bytes, little-endian
        int[] back = new int[2];
        Assert.Equal(2, RleBitpackedHybridEncoder.Decode(new byte[] { 4, 0x01, 0x02, 0x03 }, 20, back, 2));
        Assert.Equal(new[] { 0x030201, 0x030201 }, back);
        // and in four (bit widths 25..32)
        Assert.Equal(2, RleBitpackedHybridEncoder.Decode(new byte[] { 4, 0x01, 0x02, 0x03, 0x04 }, 32, back, 2));
        Assert.Equal(new[] { 0x04030201, 0x04030201 }, back);
    }

    [Fact]
    public void RLE_refuses_widths_over_four_bytes() {
        Assert.Throws<IOException>(() => RleBitpackedHybridEncoder.RLEEncode(new[] { 1 }, 33, out _));
        byte[] run = [2, 1, 0, 0, 0, 0];   // one repeat of a five-byte value
        Assert.Throws<IOException>(() => RleBitpackedHybridEncoder.Decode(run, 33, new int[1], 1));
    }

    [Fact]
    public void RLE_empty_runs_end_decoding_and_no_values_write_no_runs() {
        Assert.Equal(0, RleBitpackedHybridEncoder.Decode(new byte[] { 0 }, 3, new int[4], 4));   // RLE run of 0
        Assert.Equal(0, RleBitpackedHybridEncoder.Decode(new byte[] { 1 }, 3, new int[4], 4));   // bit-packed run of 0 groups
        var ms = new MemoryStream();
        RleBitpackedHybridEncoder.Encode(ms, ReadOnlySpan<int>.Empty, 3);
        Assert.Equal(0, ms.Length);
        RleBitpackedHybridEncoder.EncodeWithLength(ms, 3, ReadOnlySpan<int>.Empty);
        Assert.Equal(new byte[4], ms.ToArray());      // only the length prefix: 0
    }

    [Fact]
    public void Byte_stream_split_decodes_without_hardware_acceleration() {
        float[] values = [1.5f, -2.25f, float.MaxValue];
        var ms = new MemoryStream();
        ByteStreamSplitEncoder.Encode<float>(values, ms);
        bool before = ParquetOptions.UseHardwareAcceleration;
        try {
            ParquetOptions.UseHardwareAcceleration = false;
            float[] back = new float[3];
            ByteStreamSplitEncoder.Decode<float>(ms.ToArray(), back);
            Assert.Equal(values, back);
        } finally {
            ParquetOptions.UseHardwareAcceleration = before;
        }
        short[] shorts = new short[2];
        Assert.Throws<ArgumentException>(() => ByteStreamSplitEncoder.DecodeOnCpu<int>(new byte[3], new int[1]));
        Assert.Throws<ArgumentException>(() => ByteStreamSplitEncoder.DecodeHwx<int>(new byte[3], new int[1]));
        Assert.Throws<NotSupportedException>(() => ByteStreamSplitEncoder.DecodeOnCpu<decimal>(new byte[16], new decimal[1]));
        ByteStreamSplitEncoder.DecodeOnCpu<short>(new byte[] { 1, 2, 3, 4 }, shorts);
        Assert.Equal(new short[] { 0x0301, 0x0402 }, shorts);
    }

    [Fact]
    public void Delta_encodings_refuse_types_they_do_not_take() {
        Assert.Throws<NotSupportedException>(() => DeltaBinaryPackedEncoder.Encode<double>(new[] { 1.0 }, new MemoryStream()));
        Assert.Throws<NotSupportedException>(() => DeltaBinaryPackedEncoder.Decode<double>(new byte[8], new double[1], 1, out _));
        Assert.Throws<NotSupportedException>(() => DeltaBinaryPackedEncoder.Decode(new byte[8], new double[1], 0, 1, out _));
        Assert.Throws<NotSupportedException>(() => DeltaByteArrayEncoder.Decode<int>(new byte[8], new int[1], 1));
        Assert.Throws<NotSupportedException>(() => DeltaLengthByteArrayEncoder.Decode<int>(new byte[8], new int[1], 1));
        // nothing to decode: nothing consumed
        Assert.Equal(0, DeltaBinaryPackedEncoder.Decode<int>(Span<byte>.Empty, new int[1], 0, out int consumed));
        Assert.Equal(0, consumed);
        Assert.Equal(0, DeltaBinaryPackedEncoder.Decode(Span<byte>.Empty, new int[1], 0, 0, out consumed));
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void Statistics_values_decode_by_annotation() {
        Assert.True(ParquetPlainEncoder.TryEncode(null, new SchemaElement { Type = Type.INT32 }, out byte[]? none));
        Assert.Null(none);
        Assert.True(ParquetPlainEncoder.TryDecode(null, new SchemaElement { Type = Type.INT32 }, null, out object? nothing));
        Assert.Null(nothing);

        // a converted type alone gives the CLR type
        var int8 = new SchemaElement { Type = Type.INT32, ConvertedType = ConvertedType.INT_8 };
        Assert.True(ParquetPlainEncoder.TryDecode(BitConverter.GetBytes(-5), int8, null, out object? s8));
        Assert.Equal((sbyte)-5, s8);

        // INT96 as a BigInteger, and an INT96 that is not twelve bytes
        var int96 = new SchemaElement { Type = Type.INT96 };
        byte[] twelve = new byte[12];
        twelve[0] = 42;
        Assert.True(ParquetPlainEncoder.TryDecode(twelve, int96, new ParquetOptions { TreatBigIntegersAsDates = false }, out object? big));
        Assert.Equal(new BigInteger(42), big);
        Assert.True(ParquetPlainEncoder.TryDecode(new byte[5], int96, new ParquetOptions(), out object? wrong));
        Assert.Null(wrong);
    }

    [Fact]
    public void Plain_encoder_refuses_what_a_column_cannot_hold() {
        // a BigInteger beyond 96 bits
        ParquetException e = Assert.Throws<ParquetException>(() =>
            ParquetPlainEncoder.Encode(new[] { BigInteger.One << 100 }, new MemoryStream(), new SchemaElement { Type = Type.INT96 }));
        Assert.Contains("does not fit in INT96", e.Message);
        // a FIXED_LEN_BYTE_ARRAY decimal of no width decodes nothing; a GUID column of the wrong width is refused
        Assert.Equal(0, ParquetPlainEncoder.Decode(new byte[16], new decimal[1], new SchemaElement { Type = Type.FIXED_LEN_BYTE_ARRAY, TypeLength = 0, Precision = 9, Scale = 2 }));
        Assert.Equal(0, ParquetPlainEncoder.Decode(new byte[16], new BigDecimal[1], new SchemaElement { Type = Type.FIXED_LEN_BYTE_ARRAY, TypeLength = 0, Precision = 9, Scale = 2 }));
        Assert.Throws<InvalidOperationException>(() => ParquetPlainEncoder.Decode(new byte[16], new Guid[1], new SchemaElement { Type = Type.FIXED_LEN_BYTE_ARRAY, TypeLength = 8 }));
        // a decimal on a physical type that cannot hold one
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.Decode(new byte[8], new decimal[1], new SchemaElement { Type = Type.DOUBLE }));
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.Decode(new byte[8], new BigDecimal[1], new SchemaElement { Type = Type.DOUBLE }));
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.Encode(new[] { 1m }, new MemoryStream(), new SchemaElement { Type = Type.DOUBLE }));
    }

    [Fact]
    public async Task Fixed_length_strings_read_as_strings() {
        // a STRING annotation on FIXED_LEN_BYTE_ARRAY, as some writers produce for CHAR(n)
        var field = new RawDataField("code", Type.FIXED_LEN_BYTE_ARRAY, false, new LogicalType { STRING = new StringType() }, typeLength: 3);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync(field, new byte[]?[] { "abc"u8.ToArray(), "xyz"u8.ToArray() });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader g = reader.OpenRowGroupReader(0);
        var read = new string?[2];
        await g.ReadAsync(reader.Schema.DataFields[0], new Memory<string?>(read));
        Assert.Equal(new[] { "abc", "xyz" }, read);
    }

    [Fact]
    public async Task Half_columns_take_byte_stream_split() {
        var field = new DataField<Half>("h");
        var options = new ParquetOptions();
        options.ColumnEncodingHints["h"] = EncodingHint.ByteSplitStream;
        Half[] values = [(Half)1.5, (Half)(-2), Half.MaxValue];
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<Half>(field, values);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader g = reader.OpenRowGroupReader(0);
        var read = new Half[3];
        await g.ReadAsync<Half>(reader.Schema.DataFields[0], read);
        Assert.Equal(values, read);
    }

    [Fact]
    public async Task Unsigned_raw_columns_keep_unsigned_statistics() {
        // INT32 with UINT_32: the stored ints compare unsigned, and a chunk of nulls has no bounds
        var field = new RawDataField("u", Type.INT32, true, convertedType: ConvertedType.UINT_32);
        var wide = new RawDataField("w", Type.INT64, true, convertedType: ConvertedType.UINT_64);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field, wide), ms)) {
            using(ParquetRowGroupWriter rg = writer.CreateRowGroup()) {
                await rg.WriteAsync<int>(field, new int?[] { 1, -1, null });       // -1 is uint.MaxValue
                await rg.WriteAsync<long>(wide, new long?[] { 1, -1, null });
            }
            using(ParquetRowGroupWriter rg = writer.CreateRowGroup()) {
                await rg.WriteAsync<int>(field, new int?[] { null });
                await rg.WriteAsync<long>(wide, new long?[] { null });
            }
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using(ParquetRowGroupReader g = reader.OpenRowGroupReader(0)) {
            DataColumnStatistics s = g.GetStatistics(reader.Schema.DataFields[0])!;
            Assert.Equal((1u, uint.MaxValue), ((uint)s.MinValue!, (uint)s.MaxValue!));
            DataColumnStatistics w = g.GetStatistics(reader.Schema.DataFields[1])!;
            Assert.Equal((1ul, ulong.MaxValue), ((ulong)w.MinValue!, (ulong)w.MaxValue!));
        }
        using(ParquetRowGroupReader g = reader.OpenRowGroupReader(1)) {
            Assert.All(reader.Schema.DataFields, f => {
                DataColumnStatistics s = g.GetStatistics(f)!;
                Assert.Equal((1L, null, null), (s.NullCount, s.MinValue, s.MaxValue));
            });
        }
    }
}
