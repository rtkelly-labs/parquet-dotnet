using System;
using System.Collections.Generic;
using System.IO;
using Encoding = System.Text.Encoding;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Xunit;

namespace Parquet.Test.Thrift;

/// <summary>
/// Every footer and page structure skips fields it does not know, of every Thrift compact type, as a reader must for
/// files from writers on a newer parquet-format: a struct's fields with their values, list, set and map elements
/// (booleans a byte each there), and UUIDs.
/// </summary>
public class ThriftUnknownFieldsTest {

    // ---- compact protocol, written by hand: long-form field headers (type, then the zigzag id), so ids need no deltas

    static void VarInt(List<byte> b, ulong v) {
        while(v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; }
        b.Add((byte)v);
    }

    static void ZigZag(List<byte> b, long v) => VarInt(b, (ulong)((v << 1) ^ (v >> 63)));

    static void Header(List<byte> b, short id, byte type) {
        b.Add(type);
        ZigZag(b, id);
    }

    static void Binary(List<byte> b, string s) {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        VarInt(b, (ulong)bytes.Length);
        b.AddRange(bytes);
    }

    /// <summary>One unknown field of each compact type, ids from 1000 up, a struct among them holding more of them.</summary>
    static void UnknownFields(List<byte> b, int depth = 0) {
        short id = (short)(1000 + depth * 100);
        Header(b, id++, 0x01);                                           // bool true: in the header alone
        Header(b, id++, 0x02);                                           // bool false
        Header(b, id++, 0x03); b.Add(0x7f);                              // byte
        Header(b, id++, 0x04); ZigZag(b, -300);                          // i16
        Header(b, id++, 0x05); ZigZag(b, 1 << 20);                       // i32
        Header(b, id++, 0x06); ZigZag(b, long.MinValue);                 // i64
        Header(b, id++, 0x07); b.AddRange(BitConverter.GetBytes(Math.PI)); // double
        Header(b, id++, 0x08); Binary(b, "unknown");                     // binary
        Header(b, id++, 0x09); b.Add(0x35); ZigZag(b, 1); ZigZag(b, 2); ZigZag(b, 3);   // list<i32>, 3 elements
        Header(b, id++, 0x09); b.Add(0x21); b.Add(1); b.Add(2);          // list<bool>: a byte per element
        Header(b, id++, 0x09); b.Add(0xf8); VarInt(b, 16);               // list<binary> of 16, the long size form
        for(int i = 0; i < 16; i++) Binary(b, "e" + i);
        Header(b, id++, 0x0a); b.Add(0x18); Binary(b, "s");              // set<binary>
        Header(b, id++, 0x0b); VarInt(b, 2); b.Add(0x51);                // map<i32, bool>, 2 pairs
        ZigZag(b, 7); b.Add(1); ZigZag(b, 8); b.Add(2);
        Header(b, id++, 0x0b); VarInt(b, 0);                             // an empty map: no type byte
        Header(b, id++, 0x0d); b.AddRange(new byte[16]);                 // uuid
        if(depth < 2) {
            Header(b, id++, 0x0c);                                       // struct: its fields and their values skipped
            UnknownFields(b, depth + 1);
            b.Add(0);
            Header(b, id++, 0x09); b.Add(0x2c);                          // list<struct>, 2 elements
            UnknownFields(b, depth + 1); b.Add(0);
            UnknownFields(b, depth + 1); b.Add(0);
        }
    }

    private static readonly Dictionary<string, Func<ThriftCompactProtocolReader, object>> Readers = new() {
        ["SizeStatistics"] = r => SizeStatistics.Read(r),
        ["BoundingBox"] = r => BoundingBox.Read(r),
        ["GeospatialStatistics"] = r => GeospatialStatistics.Read(r),
        ["Statistics"] = r => Statistics.Read(r),
        ["StringType"] = r => StringType.Read(r),
        ["UUIDType"] = r => UUIDType.Read(r),
        ["MapType"] = r => MapType.Read(r),
        ["ListType"] = r => ListType.Read(r),
        ["EnumType"] = r => EnumType.Read(r),
        ["DateType"] = r => DateType.Read(r),
        ["Float16Type"] = r => Float16Type.Read(r),
        ["NullType"] = r => NullType.Read(r),
        ["DecimalType"] = r => DecimalType.Read(r),
        ["MilliSeconds"] = r => MilliSeconds.Read(r),
        ["MicroSeconds"] = r => MicroSeconds.Read(r),
        ["NanoSeconds"] = r => NanoSeconds.Read(r),
        ["TimeUnit"] = r => TimeUnit.Read(r),
        ["TimestampType"] = r => TimestampType.Read(r),
        ["TimeType"] = r => TimeType.Read(r),
        ["IntType"] = r => IntType.Read(r),
        ["JsonType"] = r => JsonType.Read(r),
        ["BsonType"] = r => BsonType.Read(r),
        ["VariantType"] = r => VariantType.Read(r),
        ["GeometryType"] = r => GeometryType.Read(r),
        ["GeographyType"] = r => GeographyType.Read(r),
        ["FileType"] = r => FileType.Read(r),
        ["LogicalType"] = r => LogicalType.Read(r),
        ["SchemaElement"] = r => SchemaElement.Read(r),
        ["DataPageHeader"] = r => DataPageHeader.Read(r),
        ["IndexPageHeader"] = r => IndexPageHeader.Read(r),
        ["DictionaryPageHeader"] = r => DictionaryPageHeader.Read(r),
        ["DataPageHeaderV2"] = r => DataPageHeaderV2.Read(r),
        ["SplitBlockAlgorithm"] = r => SplitBlockAlgorithm.Read(r),
        ["BloomFilterAlgorithm"] = r => BloomFilterAlgorithm.Read(r),
        ["XxHash"] = r => XxHash.Read(r),
        ["BloomFilterHash"] = r => BloomFilterHash.Read(r),
        ["Uncompressed"] = r => Uncompressed.Read(r),
        ["BloomFilterCompression"] = r => BloomFilterCompression.Read(r),
        ["BloomFilterHeader"] = r => BloomFilterHeader.Read(r),
        ["PageHeader"] = r => PageHeader.Read(r),
        ["KeyValue"] = r => KeyValue.Read(r),
        ["SortingColumn"] = r => SortingColumn.Read(r),
        ["PageEncodingStats"] = r => PageEncodingStats.Read(r),
        ["ColumnMetaData"] = r => ColumnMetaData.Read(r),
        ["EncryptionWithFooterKey"] = r => EncryptionWithFooterKey.Read(r),
        ["EncryptionWithColumnKey"] = r => EncryptionWithColumnKey.Read(r),
        ["ColumnCryptoMetaData"] = r => ColumnCryptoMetaData.Read(r),
        ["ColumnChunk"] = r => ColumnChunk.Read(r),
        ["RowGroup"] = r => RowGroup.Read(r),
        ["TypeDefinedOrder"] = r => TypeDefinedOrder.Read(r),
        ["IEEE754TotalOrder"] = r => IEEE754TotalOrder.Read(r),
        ["Int96TimestampOrder"] = r => Int96TimestampOrder.Read(r),
        ["ColumnOrder"] = r => ColumnOrder.Read(r),
        ["PageLocation"] = r => PageLocation.Read(r),
        ["OffsetIndex"] = r => OffsetIndex.Read(r),
        ["ColumnIndex"] = r => ColumnIndex.Read(r),
        ["AesGcmV1"] = r => AesGcmV1.Read(r),
        ["AesGcmCtrV1"] = r => AesGcmCtrV1.Read(r),
        ["EncryptionAlgorithm"] = r => EncryptionAlgorithm.Read(r),
        ["FileMetaData"] = r => FileMetaData.Read(r),
        ["FileCryptoMetaData"] = r => FileCryptoMetaData.Read(r),
    };

    public static TheoryData<string> Structures() {
        var data = new TheoryData<string>();
        foreach(string name in Readers.Keys)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Structures))]
    public void Unknown_fields_of_every_type_are_skipped(string name) {
        Func<ThriftCompactProtocolReader, object> read = Readers[name];
        var bytes = new List<byte>();
        UnknownFields(bytes);
        bytes.Add(0);                                                    // stop
        bytes.Add(0xab);                                                 // what follows the struct
        using var ms = new MemoryStream(bytes.ToArray());
        Assert.NotNull(read(new ThriftCompactProtocolReader(ms)));
        Assert.True(ms.Position == bytes.Count - 1, $"{name} stopped at {ms.Position} of {bytes.Count - 1}");
    }

    [Fact]
    public void Known_fields_after_unknown_ones_read_right() {
        var bytes = new List<byte>();
        UnknownFields(bytes);
        Header(bytes, 1, 0x08); Binary(bytes, "key");
        Header(bytes, 2, 0x08); Binary(bytes, "value");
        bytes.Add(0);
        KeyValue kv = KeyValue.Read(new ThriftCompactProtocolReader(new MemoryStream(bytes.ToArray())));
        Assert.Equal(("key", "value"), (kv.Key, kv.Value));
    }

    [Fact]
    public void A_footer_with_unknown_fields_reads_the_same() {
        // a footer the library wrote, with unknown fields of every type added before its stop
        var meta = new FileMetaData {
            Version = 2, NumRows = 3, CreatedBy = "lake",
            Schema = [new SchemaElement { Name = "schema", NumChildren = 1 }, new SchemaElement { Name = "id", Type = Parquet.Meta.Type.INT32 }],
            RowGroups = [],
            KeyValueMetadata = [new KeyValue { Key = "k", Value = "v" }],
        };
        using var written = new MemoryStream();
        meta.Write(new ThriftCompactProtocolWriter(written));
        var bytes = new List<byte>(written.ToArray());
        bytes.RemoveAt(bytes.Count - 1);                                 // its stop
        UnknownFields(bytes);
        bytes.Add(0);

        FileMetaData read = FileMetaData.Read(new ThriftCompactProtocolReader(new MemoryStream(bytes.ToArray())));
        Assert.Equal((2, 3L, "lake", 2, "id", "v"), (read.Version, read.NumRows, read.CreatedBy, read.Schema.Count, read.Schema[1].Name, read.KeyValueMetadata![0].Value));
    }
}
