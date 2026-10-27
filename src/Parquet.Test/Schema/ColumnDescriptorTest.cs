using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Schema;

public class ColumnDescriptorTest : TestBase {

    [Fact]
    public async Task Leaf_columns_as_stored() {
        // list<int?> tags and string name, written by parquet-cpp
        using Stream s = OpenTestFile("v2/v2_snappy.parquet");
        await using ParquetReader reader = await ParquetReader.CreateAsync(s);
        ColumnDescriptor[] columns = reader.Columns.ToArray();
        Assert.Equal(2, columns.Length);

        Assert.Equal(new FieldPath("tags", "list", "element"), columns[0].Path);
        Assert.Equal((0, Meta.Type.INT32, 3, 1), (columns[0].Ordinal, columns[0].PhysicalType, columns[0].MaxDefinitionLevel, columns[0].MaxRepetitionLevel));
        Assert.Null(columns[0].TypeLength);
        Assert.Equal(ColumnSortOrder.Signed, columns[0].SortOrder);

        Assert.Equal(new FieldPath("name"), columns[1].Path);
        Assert.Equal((Meta.Type.BYTE_ARRAY, 1, 0), (columns[1].PhysicalType, columns[1].MaxDefinitionLevel, columns[1].MaxRepetitionLevel));
        Assert.NotNull(columns[1].LogicalType?.STRING);
        Assert.Equal(ColumnSortOrder.Unsigned, columns[1].SortOrder);

        Assert.True(reader.FooterLength > 0);
        Assert.Equal(reader.FooterLength, System.BitConverter.ToInt32(System.IO.File.ReadAllBytes("./data/v2/v2_snappy.parquet")[^8..^4]));
    }

    [Fact]
    public async Task Fixed_length_field_ids_and_levels_match_the_model_schema() {
        var fixedField = new DataField<byte[]>("hash") { FixedLength = 4, FieldId = 3 };
        var schema = new ParquetSchema(new DataField<int>("id") { FieldId = 1 }, fixedField,
            new ListField("tags", new DataField<long?>("element")) { FieldId = 5 });
        using var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(schema, ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync<int>((DataField)schema[0], new[] { 1 });
            await rg.WriteAsync(fixedField, new[] { new byte[] { 1, 2, 3, 4 } });
            await rg.WriteAsync<long>(schema.GetDataFields()[2], new long?[] { 7 }, new[] { 0 });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        ColumnDescriptor[] columns = reader.Columns.ToArray();
        Assert.Equal(1, columns[0].FieldId);
        Assert.Equal((Meta.Type.FIXED_LEN_BYTE_ARRAY, 4, 3), (columns[1].PhysicalType, columns[1].TypeLength, columns[1].FieldId));
        DataField[] model = reader.Schema.GetDataFields();
        for(int i = 0; i < columns.Length; i++) {
            Assert.Equal(model[i].Path, columns[i].Path);
            Assert.Equal((model[i].MaxDefinitionLevel, model[i].MaxRepetitionLevel), (columns[i].MaxDefinitionLevel, columns[i].MaxRepetitionLevel));
        }
    }
}
