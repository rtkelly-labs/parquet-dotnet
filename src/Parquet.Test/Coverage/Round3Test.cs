using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Encodings;
using Parquet.Extensions;
using Parquet.Extensions.Streaming;
using Parquet.File;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;
using Encoding = Parquet.Meta.Encoding;
using Type = Parquet.Meta.Type;

namespace Parquet.Test.Coverage;

/// <summary>
/// The third round of lines the suite did not run (lake(0085)): the AVX-512 boolean encoder (run with
/// DOTNET_PreferredVectorBitWidth=512 on a CPU that has it), pages other writers may produce (RLE-encoded integers,
/// delta-encoded decimals on BYTE_ARRAY), footers with schemas this library cannot build, and the remaining encoder,
/// statistics and schema paths.
/// </summary>
public class Round3Test {

    // ---- AVX-512 ----

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]     // more than one 512-bit vector of booleans
    [InlineData(200)]
    [InlineData(1031)]
    public void Booleans_encode_the_same_with_and_without_vectors(int count) {
        var random = new Random(count);
        bool[] values = Enumerable.Range(0, count).Select(_ => random.Next(3) == 0).ToArray();
        bool before = ParquetOptions.UseHardwareAcceleration;
        try {
            ParquetOptions.UseHardwareAcceleration = true;
            using var vector = new MemoryStream();
            ParquetPlainEncoder.Encode(values.AsSpan(), vector);
            ParquetOptions.UseHardwareAcceleration = false;
            using var scalar = new MemoryStream();
            ParquetPlainEncoder.Encode(values.AsSpan(), scalar);
            Assert.Equal(scalar.ToArray(), vector.ToArray());
            Assert.Equal((count + 7) / 8, vector.Length);
        } finally {
            ParquetOptions.UseHardwareAcceleration = before;
        }
    }

    [Fact]
    public async Task A_boolean_column_round_trips_with_vectors() {
        bool[] values = Enumerable.Range(0, 777).Select(i => i % 3 == 0 || i % 7 == 0).ToArray();
        var field = new DataField<bool>("b");
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<bool>(field, values);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        bool[] read = new bool[values.Length];
        await rowGroup.ReadAsync<bool>(reader.Schema.GetDataFields()[0], read);
        Assert.Equal(values, read);
    }

    // ---- hand-built files ----

    /// <summary>A file of one required column, one row group and one uncompressed v1 data page.</summary>
    static byte[] OnePageFile(SchemaElement leaf, Encoding encoding, byte[] pageData, int values) {
        leaf.RepetitionType = FieldRepetitionType.REQUIRED;
        var header = new PageHeader {
            Type = PageType.DATA_PAGE, UncompressedPageSize = pageData.Length, CompressedPageSize = pageData.Length,
            DataPageHeader = new DataPageHeader {
                NumValues = values, Encoding = encoding,
                DefinitionLevelEncoding = Encoding.RLE, RepetitionLevelEncoding = Encoding.RLE,
            },
        };
        using var page = new MemoryStream();
        header.Write(new ThriftCompactProtocolWriter(page));
        page.Write(pageData);
        long size = page.Length;
        var meta = new FileMetaData {
            Version = 1, NumRows = values,
            Schema = [new SchemaElement { Name = "schema", NumChildren = 1 }, leaf],
            RowGroups = [new RowGroup {
                NumRows = values, TotalByteSize = size,
                Columns = [new ColumnChunk {
                    FileOffset = 4,
                    MetaData = new ColumnMetaData {
                        Type = leaf.Type!.Value, Encodings = [encoding], PathInSchema = [leaf.Name], Codec = CompressionCodec.UNCOMPRESSED,
                        NumValues = values, TotalUncompressedSize = size, TotalCompressedSize = size, DataPageOffset = 4,
                    },
                }],
            }],
        };
        return Assemble(page.ToArray(), meta);
    }

    static byte[] Assemble(byte[] data, FileMetaData meta) {
        using var footer = new MemoryStream();
        meta.Write(new ThriftCompactProtocolWriter(footer));
        return [.. "PAR1"u8, .. data, .. footer.ToArray(), .. BitConverter.GetBytes((int)footer.Length), .. "PAR1"u8];
    }

    /// <summary>Opens the file and builds its schema (built when first asked for).</summary>
    static async Task<ParquetSchema> SchemaOfAsync(byte[] file, ParquetOptions? options = null) {
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), options);
        return reader.Schema;
    }

    /// <summary>A file with no row groups and this schema under its root.</summary>
    static byte[] SchemaOnlyFile(params SchemaElement[] elements) =>
        Assemble([], new FileMetaData {
            Version = 1, NumRows = 0, RowGroups = [],
            Schema = [new SchemaElement { Name = "schema", NumChildren = 1 }, .. elements],
        });

    [Fact]
    public async Task An_RLE_encoded_INT32_page_reads_with_its_type_length_as_bit_width() {
        // RLE for non-boolean values is not in the format; a writer that used it gave the bit width as type_length
        int[] values = [1, 2, 3, 3, 3, 15, 0];
        using var data = new MemoryStream();
        RleBitpackedHybridEncoder.Encode(data, values, 4);
        byte[] file = OnePageFile(new SchemaElement { Name = "x", Type = Type.INT32, TypeLength = 4 }, Encoding.RLE, data.ToArray(), values.Length);

        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        int[] read = new int[values.Length];
        await rowGroup.ReadAsync<int>(reader.Schema.GetDataFields()[0], read);
        Assert.Equal(values, read);
    }

    [Fact]
    public async Task A_delta_length_encoded_decimal_on_BYTE_ARRAY_reads_through_plain() {
        // DECIMAL(4,2) on BYTE_ARRAY: big-endian two's complement unscaled values, DELTA_LENGTH_BYTE_ARRAY encoded
        byte[][] unscaled = [[0x64], [0xFF, 0x06], [0x04, 0xD2]];   // 100, -250, 1234
        using var data = new MemoryStream();
        DeltaBinaryPackedEncoder.Encode<int>(unscaled.Select(u => u.Length).ToArray(), data);
        foreach(byte[] u in unscaled)
            data.Write(u);
        var leaf = new SchemaElement {
            Name = "d", Type = Type.BYTE_ARRAY, ConvertedType = ConvertedType.DECIMAL, Precision = 4, Scale = 2,
            LogicalType = new LogicalType { DECIMAL = new DecimalType { Precision = 4, Scale = 2 } },
        };
        byte[] file = OnePageFile(leaf, Encoding.DELTA_LENGTH_BYTE_ARRAY, data.ToArray(), unscaled.Length);

        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        decimal[] read = new decimal[3];
        await rowGroup.ReadAsync<decimal>(reader.Schema.GetDataFields()[0], read);
        Assert.Equal(new[] { 1.00m, -2.50m, 12.34m }, read);
    }

    [Fact]
    public async Task A_delta_encoded_boolean_page_is_refused() {
        byte[] file = OnePageFile(new SchemaElement { Name = "b", Type = Type.BOOLEAN }, Encoding.DELTA_BINARY_PACKED, [0, 0, 0, 0], 3);
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        ParquetException e = await Assert.ThrowsAsync<ParquetException>(async () =>
            await rowGroup.ReadAsync<bool>(reader.Schema.GetDataFields()[0], new bool[3]));
        Assert.Contains("DELTA_BINARY_PACKED is not supported for BOOLEAN", e.Message);
    }

    [Fact]
    public async Task A_repeated_group_with_no_children_is_refused() {
        // an implicit list (a repeated group outside LIST and MAP) must hold an element
        byte[] file = SchemaOnlyFile(new SchemaElement { Name = "r", RepetitionType = FieldRepetitionType.REPEATED, NumChildren = 0 });
        await Assert.ThrowsAsync<IndexOutOfRangeException>(() => SchemaOfAsync(file));
    }

    [Fact]
    public async Task A_physical_type_this_library_does_not_know_is_refused() {
        byte[] file = SchemaOnlyFile(new SchemaElement { Name = "x", Type = (Type)42, RepetitionType = FieldRepetitionType.REQUIRED });
        NotSupportedException e = await Assert.ThrowsAsync<NotSupportedException>(() => SchemaOfAsync(file));
        Assert.Contains("unsupported type", e.Message);
    }

    [Fact]
    public async Task A_TIME_without_a_unit_is_refused_when_read_as_TimeOnly() {
        byte[] file = SchemaOnlyFile(new SchemaElement {
            Name = "t", Type = Type.INT64, RepetitionType = FieldRepetitionType.REQUIRED,
            LogicalType = new LogicalType { TIME = new TimeType { IsAdjustedToUTC = true, Unit = new TimeUnit() } },
        });
        NotSupportedException e = await Assert.ThrowsAsync<NotSupportedException>(() =>
            SchemaOfAsync(file, new ParquetOptions { TimeMapping = TimeMapping.TimeOnly }));
        Assert.Contains("unsupported time unit", e.Message);
    }

    // ---- encoders ----

    [Fact]
    public void A_decimal_too_wide_for_INT32_is_refused_even_within_its_precision() {
        // a footer may declare INT32 for a precision INT32 cannot hold; the value is refused, not wrapped
        var tse = new SchemaElement { Type = Type.INT32, Precision = 18, Scale = 0 };
        Assert.Throws<ParquetException>(() => ParquetPlainEncoder.Encode(new[] { 1_000_000_000_000m }.AsSpan(), new MemoryStream(), tse));
        Assert.Throws<ParquetException>(() => ParquetPlainEncoder.TryEncode(1_000_000_000_000m, tse, out _));
    }

    [Fact]
    public async Task Byte_arrays_longer_than_the_encoders_buffer_round_trip() {
        var random = new Random(7);
        byte[][] values = [new byte[100_000], new byte[3], new byte[70_000]];
        foreach(byte[] v in values)
            random.NextBytes(v);
        var field = new DataField<byte[]>("b");
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync(field, values);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var read = new byte[]?[3];
        await rowGroup.ReadAsync(reader.Schema.GetDataFields()[0], new Memory<byte[]?>(read));
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(values[i], read[i]));
    }

    [Fact]
    public void Fixed_length_byte_arrays_decode_by_their_type_length() {
        byte[][] read = new byte[2][];
        Assert.Equal(2, ParquetPlainEncoder.Decode([1, 2, 3, 4, 5, 6], read, new SchemaElement { Type = Type.FIXED_LEN_BYTE_ARRAY, TypeLength = 3 }));
        Assert.Equal(new byte[] { 1, 2, 3 }, read[0]);
        Assert.Equal(new byte[] { 4, 5, 6 }, read[1]);
        Assert.Throws<InvalidDataException>(() => ParquetPlainEncoder.Decode([1, 2, 3], read, new SchemaElement { Type = Type.FIXED_LEN_BYTE_ARRAY }));
    }

    [Fact]
    public void Unsigned_longs_delta_encode_in_partial_groups_of_eight() {
        ulong[] values = [5, 1_000_000_000_000, 3, 4, 2];     // a miniblock of five: the last group padded with zeros
        using var ms = new MemoryStream();
        DeltaBinaryPackedEncoder.Encode<ulong>(values, ms);
        ulong[] read = new ulong[values.Length];
        Assert.Equal(values.Length, DeltaBinaryPackedEncoder.Decode<ulong>(ms.ToArray(), read, values.Length, out _));
        Assert.Equal(values, read);
    }

    [Fact]
    public void RLE_at_width_zero_writes_one_run_per_value_change() {
        using var zeros = new MemoryStream();
        RleBitpackedHybridEncoder.Encode(zeros, new[] { 0, 0, 0 }, 0);
        Assert.Equal(new byte[] { 3 << 1 }, zeros.ToArray());           // one run of three, no value bytes
        using var changes = new MemoryStream();
        RleBitpackedHybridEncoder.Encode(changes, new[] { 0, 1, 1 }, 0);
        Assert.Equal(new byte[] { 1 << 1, 2 << 1 }, changes.ToArray()); // runs of one and two
    }

    // ---- statistics ----

    [Fact]
    public void Unsigned_statistics_compare_as_unsigned_and_take_no_statistics_object() {
        StatsCompute.ComputeUnsigned(new[] { 1, 2 }.AsSpan(), null);
        StatsCompute.ComputeUnsigned(new[] { 1L, 2L }.AsSpan(), null);
        var stats = new DataColumnStatistics(null, null, null, null);
        StatsCompute.ComputeUnsigned(new[] { 5L, 3L, -1L }.AsSpan(), stats);    // -1 is the largest unsigned
        Assert.Equal((3L, -1L), ((long)stats.MinValue!, (long)stats.MaxValue!));
    }

    [Fact]
    public async Task A_chunk_of_nulls_has_no_physical_bounds() {
        var field = new DataField<int?>("x");
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<int>(field, new int?[] { null, null });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataColumnStatistics stats = rowGroup.GetStatistics(reader.Schema.GetDataFields()[0])!;
        Assert.Null(stats.PhysicalMin);
        Assert.Null(stats.PhysicalMax);
    }

    // ---- schema ----

    sealed class OtherField : Field {
        public OtherField() : base("other", SchemaType.Struct) { }
        internal override void PropagateLevels(int parentRepetitionLevel, int parentDefinitionLevel) { }
    }

    [Fact]
    public void A_field_kind_the_encoder_does_not_know_is_refused() {
        var other = new OtherField();
        var group = new StructField("s", other);       // the child's path is set through the base no-op
        other.Assign(new DataField<int>("x"));          // the base Assign: nothing to assign for this kind
        Assert.Equal("s", group.Name);
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() =>
            SchemaEncoder.Encode(other, new SchemaElement { Name = "root" }, new List<SchemaElement>(), new ParquetOptions()));
        Assert.Contains("unable to encode", e.Message);
    }

    [Fact]
    public void A_field_is_not_equal_to_something_that_is_not_a_field() {
        Assert.False(new StructField("s", new DataField<int>("a")).Equals("s"));
    }

    [Fact]
    public async Task A_maps_repeated_key_and_value_are_written_optional() {
        // repeated key/value fields would add a repetition level the map's key_value group already has
        var schema = new ParquetSchema(new MapField("m", new DataField("k", typeof(int[])), new DataField("v", typeof(int[]))));
        using var ms = new MemoryStream();
        await using(await ParquetWriter.CreateAsync(schema, ms)) { }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        List<SchemaElement> elements = reader.Metadata!.Schema;
        Assert.Equal(FieldRepetitionType.OPTIONAL, elements.Single(e => e.Name == "k").RepetitionType);
        Assert.Equal(FieldRepetitionType.OPTIONAL, elements.Single(e => e.Name == "v").RepetitionType);
    }

    [Fact]
    public void Custom_metadata_empty_is_none() {
        var footer = new ThriftFooter(new ParquetSchema(new DataField<int>("x")), 0, new ParquetOptions());
        Assert.Empty(footer.CustomMetadata);
        footer.CustomMetadata = new Dictionary<string, string>();
        Assert.Null(footer.Meta.KeyValueMetadata);
        Assert.Empty(footer.CustomMetadata);
    }

    [Fact]
    public void A_column_chunk_with_an_empty_path_has_no_schema_element() {
        var footer = new ThriftFooter(new ParquetSchema(new DataField<int>("x")), 0, new ParquetOptions());
        Assert.Null(footer.GetSchemaElement(new ColumnChunk { MetaData = new ColumnMetaData { PathInSchema = [] } }));
    }

    [Fact]
    public void Disposing_the_write_meter_disposes_its_output() {
        var inner = new MemoryStream();
        var metered = new MeteredWriteStream(inner);
        metered.Dispose();
        Assert.False(inner.CanWrite);
    }
}
