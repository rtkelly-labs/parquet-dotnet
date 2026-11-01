using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>A corrupt schema in the footer throws ParquetException (docs/changes/0106-schema-validated.md).</summary>
public class SchemaValidationTest {

    static async Task<byte[]> WriteAsync() {
        var s = new DataField<string>("s");
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(s), ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync(s, new[] { "a", "b" });
        }
        return ms.ToArray();
    }

    static byte[] WithFooter(byte[] file, Action<FileMetaData> edit) {
        int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 8));
        int start = file.Length - 8 - length;
        FileMetaData meta = FileMetaData.Read(new ThriftCompactProtocolReader(new MemoryStream(file, start, length)));
        edit(meta);
        var footer = new MemoryStream();
        meta.Write(new ThriftCompactProtocolWriter(footer));
        byte[] edited = [.. file.AsSpan(0, start), .. footer.ToArray(), 0, 0, 0, 0, .. "PAR1"u8];
        BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(edited.Length - 8), (int)footer.Length);
        return edited;
    }

    public static TheoryData<string> Annotations => new() { "string", "int32", "utf8" };

    /// <summary>The annotations map to a CLR type on their own; an undefined physical type under them is still corrupt.</summary>
    [Theory]
    [MemberData(nameof(Annotations))]
    public async Task Undefined_physical_type_under_an_annotation_fails(string annotation) {
        byte[] file = WithFooter(await WriteAsync(), meta => {
            SchemaElement leaf = meta.Schema[1];
            leaf.Type = (Meta.Type)99;
            leaf.LogicalType = annotation switch {
                "string" => new LogicalType { STRING = new StringType() },
                "int32" => new LogicalType { INTEGER = new IntType { BitWidth = 32, IsSigned = true } },
                _ => null
            };
            leaf.ConvertedType = annotation == "utf8" ? ConvertedType.UTF8 : null;
        });

        await Assert.ThrowsAsync<ParquetException>(async () => {
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
            _ = reader.Schema;
        });
    }
}
