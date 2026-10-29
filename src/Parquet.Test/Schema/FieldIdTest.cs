using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Schema;

public class FieldIdTest {

    private static async Task<(ParquetSchema Schema, Meta.FileMetaData Meta)> RoundTripAsync(ParquetSchema schema) {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms)) {
            // an empty row group is enough for the schema to be written
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        return (reader.Schema, reader.Metadata!);
    }

    [Fact]
    public async Task Field_ids_round_trip_on_data_fields() {
        var (schema, meta) = await RoundTripAsync(new ParquetSchema(
            new DataField<long>("id") { FieldId = 1 },
            new DataField<string>("name") { FieldId = 2 },
            new DataField<int>("no_id")));

        Assert.Equal(new[] { 1, 2, -1 }, schema.DataFields.Select(f => f.FieldId));
        Assert.Equal(new int?[] { 1, 2, null }, meta.Schema.Skip(1).Select(e => e.FieldId));
    }

    [Fact]
    public async Task Field_ids_round_trip_on_structs_lists_maps_and_their_children() {
        var (schema, meta) = await RoundTripAsync(new ParquetSchema(
            new StructField("point",
                new DataField<int>("x") { FieldId = 11 },
                new DataField<int>("y") { FieldId = 12 }) { FieldId = 10 },
            new ListField("tags", new DataField<string>("element") { FieldId = 21 }) { FieldId = 20 },
            new MapField("attrs",
                new DataField("key", typeof(string), isNullable: false) { FieldId = 31 },
                new DataField<long>("value") { FieldId = 32 }) { FieldId = 30 }));

        var point = (StructField)schema.Fields[0];
        Assert.Equal(10, point.FieldId);
        Assert.Equal(new[] { 11, 12 }, point.Fields.Select(f => f.FieldId));

        var tags = (ListField)schema.Fields[1];
        Assert.Equal(20, tags.FieldId);
        Assert.Equal(21, tags.Item.FieldId);

        var attrs = (MapField)schema.Fields[2];
        Assert.Equal(30, attrs.FieldId);
        Assert.Equal(31, attrs.Key.FieldId);
        Assert.Equal(32, attrs.Value.FieldId);

        // On the wire: ids sit on the outer group of a list or map, not on its repeated container.
        Assert.Equal(20, meta.Schema.Single(e => e.Name == "tags").FieldId);
        Assert.Null(meta.Schema.Single(e => e.Name == "list").FieldId);
        Assert.Equal(30, meta.Schema.Single(e => e.Name == "attrs").FieldId);
        Assert.Null(meta.Schema.Single(e => e.Name == "key_value").FieldId);
    }

    [Fact]
    public void Field_ids_default_to_none() {
        Assert.Equal(-1, new DataField<int>("a").FieldId);
        Assert.Equal(-1, new StructField("s", new DataField<int>("a")).FieldId);
    }
}
