using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Schema;

public class ColumnDescriptorTest {

    [Fact]
    public async Task Leaf_columns_as_stored() {
        var id = new DataField<int>("id") { FieldId = 1 };
        var uuid = new DataField<Guid>("uuid");
        var name = new DataField<string>("name");
        var tags = new ListField("tags", new DataField<long?>("element"));
        var schema = new ParquetSchema(id, uuid, name, tags);
        using var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(schema, ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync<int>(id, new[] { 1 });
            await rg.WriteAsync<Guid>(uuid, new[] { Guid.NewGuid() });
            await rg.WriteAsync(name, new[] { "a" });
            await rg.WriteAsync<long>(schema.GetDataFields()[3], new long?[] { 7 }, new[] { 0 });
        }
        byte[] file = ms.ToArray();
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        ColumnDescriptor[] columns = reader.Columns.ToArray();
        Assert.Equal(4, columns.Length);

        Assert.Equal((0, Meta.Type.INT32, 1), (columns[0].Ordinal, columns[0].PhysicalType, columns[0].FieldId));
        Assert.Null(columns[0].TypeLength);
        Assert.Equal((Meta.Type.FIXED_LEN_BYTE_ARRAY, 16), (columns[1].PhysicalType, columns[1].TypeLength));
        Assert.NotNull(columns[1].LogicalType?.UUID);
        Assert.Equal(Meta.Type.BYTE_ARRAY, columns[2].PhysicalType);
        Assert.NotNull(columns[2].LogicalType?.STRING);
        Assert.Equal(new FieldPath("tags", "list", "element"), columns[3].Path);

        // levels and paths as the model schema has them
        DataField[] model = reader.Schema.GetDataFields();
        for(int i = 0; i < columns.Length; i++) {
            Assert.Equal(model[i].Path, columns[i].Path);
            Assert.Equal((model[i].MaxDefinitionLevel, model[i].MaxRepetitionLevel), (columns[i].MaxDefinitionLevel, columns[i].MaxRepetitionLevel));
        }

        Assert.Equal(BitConverter.ToInt32(file, file.Length - 8), reader.FooterLength);
    }
}
