using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Encryption;
using Parquet.Schema;
using Xunit;
using PS = ParquetSharp;
using PSchema = ParquetSharp.Schema;

namespace Parquet.Test.Integration;

/// <summary>
/// Files in Iceberg's layout (field IDs on every node, physical timestamps, uuid, fixed, decimal, list, map, struct)
/// exchanged with parquet-cpp (ParquetSharp) in both directions, plain and encrypted.
/// </summary>
public class ParquetCppInteropTest : IDisposable {

    private readonly string _dir = Directory.CreateTempSubdirectory("parquet-cpp-interop").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    static readonly Guid G1 = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    static readonly Guid G2 = Guid.Parse("f0000000-0000-0000-0000-000000000001");

    // three rows: values / all null (empty list and map) / negatives, null list and map, struct with a null field
    static readonly long?[] Ts = { 1_000_000L, null, -5L };
    static readonly long?[] Ns = { 1_000_000_123L, null, -7L };
    static readonly Guid?[] Uuids = { G1, null, G2 };
    static readonly byte[]?[] Fixed = { new byte[] { 1, 2, 3, 4 }, null, new byte[] { 9, 9, 9, 9 } };
    static readonly int[]?[] Tags = { new[] { 1, 2 }, Array.Empty<int>(), null };

    static T Id<T>(T f, int id) where T : Field { f.FieldId = id; return f; }

    static ParquetSchema IcebergSchema(out DataField[] leaves) {
        DataField id = Id(new DataField<long>("id"), 1);
        DataField ts = Id(new DateTimeDataField("ts", DateTimeFormat.Timestamp, isAdjustedToUTC: false, unit: DateTimeTimeUnit.Micros, isNullable: true), 2);
        DataField ns = Id(new DateTimeDataField("tsns", DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Nanos, isNullable: true), 3);
        DataField u = Id(new DataField<Guid?>("u"), 6);
        DataField f = Id(new DataField<byte[]>("f") { FixedLength = 4 }, 7);
        DataField el = Id(new DataField<int>("element"), 10);
        DataField key = Id(new DataField("key", typeof(string), isNullable: false), 12);
        DataField val = Id(new DataField<long?>("value"), 13);
        DataField a = Id(new DataField<int?>("a"), 15);
        leaves = new[] { id, ts, ns, u, f, el, key, val, a };
        return new ParquetSchema(id, ts, ns, u, f,
            Id(new ListField("tags", el), 9), Id(new MapField("m", key, val), 11), Id(new StructField("st", a), 14));
    }

    async Task<string> WriteWithParquetNetAsync(string name, ParquetOptions? options = null) {
        string path = Path.Combine(_dir, name);
        ParquetSchema schema = IcebergSchema(out DataField[] l);
        await using FileStream fs = System.IO.File.Create(path);
        await using ParquetWriter w = await ParquetWriter.CreateAsync(schema, fs, options);
        using ParquetRowGroupWriter rg = w.CreateRowGroup();
        await rg.WriteAsync<long>(l[0], new[] { 1L, 2L, 3L });
        await rg.WriteAllPartsAsync<long>(l[1], new[] { 1_000_000L, -5L }, new[] { 1, 0, 1 }, null, default);
        await rg.WriteAllPartsAsync<long>(l[2], new[] { 1_000_000_123L, -7L }, new[] { 1, 0, 1 }, null, default);
        await rg.WriteAsync<Guid>(l[3], Uuids);
        await rg.WriteAsync(l[4], Fixed);
        await rg.WriteAllPartsAsync<int>(l[5], new[] { 1, 2 }, new[] { 2, 2, 1, 0 }, new[] { 0, 1, 0, 0 }, default);
        await rg.WriteAllPartsAsync<ReadOnlyMemory<char>>(l[6], new[] { "x".AsMemory() }, new[] { 2, 1, 0 }, new[] { 0, 0, 0 }, default);
        await rg.WriteAllPartsAsync<long>(l[7], new[] { 10L }, new[] { 3, 1, 0 }, new[] { 0, 0, 0 }, default);
        await rg.WriteAllPartsAsync<int>(l[8], new[] { 5 }, new[] { 2, 0, 1 }, null, default);
        rg.CompleteValidate();
        return path;
    }

    static void CheckWithParquetCpp(string path, PS.ReaderProperties? properties = null) {
        using var file = properties == null ? new PS.ParquetFileReader(path) : new PS.ParquetFileReader(path, properties);
        PS.SchemaDescriptor schema = file.FileMetaData.Schema;
        Assert.Equal(new[] { 1, 2, 3, 6, 7, 10, 12, 13, 15 }, Enumerable.Range(0, schema.NumColumns).Select(i => schema.Column(i).SchemaNode.FieldId));
        Assert.Equal(new[] { 1, 2, 3, 6, 7, 9, 11, 14 }, Enumerable.Range(0, schema.GroupNode.FieldCount).Select(i => schema.GroupNode.Field(i).FieldId));
        Assert.Equal(4, schema.Column(4).TypeLength);

        using PS.RowGroupReader rg = file.RowGroup(0);
        Assert.Equal(new long[] { 1, 2, 3 }, rg.Column(0).LogicalReader<long>().ReadAll(3));
        Assert.Equal(Ts, rg.Column(1).LogicalReaderOverride<long?>().ReadAll(3));
        Assert.Equal(Ns, rg.Column(2).LogicalReaderOverride<long?>().ReadAll(3));
        Assert.Equal(Uuids, rg.Column(3).LogicalReader<Guid?>().ReadAll(3));
        Assert.Equal(Tags, rg.Column(5).LogicalReader<int[]?>().ReadAll(3));
        using PS.Statistics? stats = rg.MetaData.GetColumnChunkMetaData(1).Statistics;
        Assert.Equal((-5L, 1_000_000L), (((PS.Statistics<long>)stats!).Min, ((PS.Statistics<long>)stats!).Max));
    }

    [Fact]
    public async Task Parquet_cpp_reads_iceberg_files_written_here() =>
        CheckWithParquetCpp(await WriteWithParquetNetAsync("plain.parquet"));

    [Fact]
    public async Task Parquet_cpp_reads_encrypted_iceberg_files_written_here() {
        byte[] footer = "0123456789012345"u8.ToArray(), column = "1234567890123450"u8.ToArray();
        string path = await WriteWithParquetNetAsync("encrypted.parquet", new ParquetOptions {
            Encryption = new ParquetEncryptionOptions {
                // two columns with their own key, the others in plaintext
                FooterKey = footer, ColumnKeys = new Dictionary<string, ParquetColumnKey> {
                    ["ts"] = new(column, "c"u8.ToArray()), ["tags.list.element"] = new(column, "c"u8.ToArray())
                }
            }
        });
        using var builder = new PS.FileDecryptionPropertiesBuilder();
        using PS.FileDecryptionProperties decryption = builder.FooterKey(footer).KeyRetriever(new Keys(column)).Build();
        using PS.ReaderProperties properties = PS.ReaderProperties.GetDefaultReaderProperties();
        properties.FileDecryptionProperties = decryption;
        CheckWithParquetCpp(path, properties);
    }

    sealed class Keys(byte[] column) : PS.DecryptionKeyRetriever {
        public override byte[] GetKey(string keyMetadata) => keyMetadata == "c" ? column : throw new KeyNotFoundException(keyMetadata);
    }

    string WriteWithParquetCpp() {
        string path = Path.Combine(_dir, "cpp.parquet");
        using var id = new PSchema.PrimitiveNode("id", PS.Repetition.Required, PS.LogicalType.None(), PS.PhysicalType.Int64, fieldId: 1);
        using var ts = new PSchema.PrimitiveNode("ts", PS.Repetition.Optional, PS.LogicalType.Timestamp(false, PS.TimeUnit.Micros), PS.PhysicalType.Int64, fieldId: 2);
        using var ns = new PSchema.PrimitiveNode("tsns", PS.Repetition.Optional, PS.LogicalType.Timestamp(true, PS.TimeUnit.Nanos), PS.PhysicalType.Int64, fieldId: 3);
        using var u = new PSchema.PrimitiveNode("u", PS.Repetition.Optional, PS.LogicalType.Uuid(), PS.PhysicalType.FixedLenByteArray, 16, 6);
        // a fixed[n] column is left out: ParquetSharp's logical writers do not take un-annotated FIXED_LEN_BYTE_ARRAY
        using var el = new PSchema.PrimitiveNode("element", PS.Repetition.Required, PS.LogicalType.None(), PS.PhysicalType.Int32, fieldId: 10);
        using var lst = new PSchema.GroupNode("list", PS.Repetition.Repeated, new PSchema.Node[] { el });
        using var tags = new PSchema.GroupNode("tags", PS.Repetition.Optional, new PSchema.Node[] { lst }, PS.LogicalType.List(), 9);
        using var root = new PSchema.GroupNode("table", PS.Repetition.Required, new PSchema.Node[] { id, ts, ns, u, tags });
        using PS.WriterProperties props = new PS.WriterPropertiesBuilder().Compression(PS.Compression.Zstd).Build();
        using var file = new PS.ParquetFileWriter(path, root, props);
        using PS.RowGroupWriter rg = file.AppendRowGroup();
        using(var w = rg.NextColumn().LogicalWriter<long>()) w.WriteBatch(new long[] { 1, 2, 3 });
        using(var w = rg.NextColumn().LogicalWriterOverride<long?>()) w.WriteBatch(Ts);
        using(var w = rg.NextColumn().LogicalWriterOverride<long?>()) w.WriteBatch(Ns);
        using(var w = rg.NextColumn().LogicalWriter<Guid?>()) w.WriteBatch(Uuids);
        using(var w = rg.NextColumn().LogicalWriter<int[]?>()) w.WriteBatch(Tags);
        file.Close();
        return path;
    }

    [Fact]
    public async Task Reads_iceberg_files_written_by_parquet_cpp() {
        await using ParquetReader reader = await ParquetReader.CreateAsync(WriteWithParquetCpp());
        Assert.Equal(new[] { 1, 2, 3, 6, 10 }, reader.Columns.Select(c => c.FieldId ?? -1));
        Assert.Equal(new[] { 1, 2, 3, 6, 9 }, reader.Schema.Fields.Select(f => f.FieldId));
        Assert.Equal(16, reader.Columns[3].TypeLength);
        DataField[] fields = reader.Schema.GetDataFields();
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);

        async Task<long?[]> Longs(DataField df) {
            using RawColumnData<long> raw = await rg.ReadRawColumnDataAsync<long>(df);
            return raw.NullableValues.ToArray();
        }
        Assert.Equal(Ts, await Longs(fields[1]));
        Assert.Equal(Ns, await Longs(fields[2]));

        using(RawColumnData<Guid> raw = await rg.ReadRawColumnDataAsync<Guid>(fields[3]))
            Assert.Equal(Uuids, raw.NullableValues.ToArray());
        using(RawColumnData<int> raw = await rg.ReadRawColumnDataAsync<int>(fields[4])) {
            Assert.Equal(new[] { 1, 2 }, raw.Values[..2].ToArray());   // values of defined elements first
            Assert.Equal(new[] { 2, 2, 1, 0 }, raw.DefinitionLevels.ToArray());
            Assert.Equal(new[] { 0, 1, 0, 0 }, raw.RepetitionLevels.ToArray());
        }

        DataColumnStatistics ts = rg.GetStatistics(fields[1])!;
        Assert.Equal((-5L, 1_000_000L), ((long)ts.PhysicalMin!, (long)ts.PhysicalMax!));
        DataColumnStatistics u = rg.GetStatistics(fields[3])!;
        Assert.Equal((G1, G2), ((Guid)u.MinValue!, (Guid)u.MaxValue!));
    }
}
