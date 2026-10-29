using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Parquet.Serialization;
using Parquet.Serialization.Attributes;
using Xunit;

namespace Parquet.Test.Schema;

/// <summary>
/// List shapes written by older writers, which the format's backward-compatibility rules require readers to accept
/// (LogicalTypes.md, "Lists"). Each file holds the rows [1, 2], [], null, [3] in an optional LIST column "tags"
/// (bare_repeated: a repeated int32 "tags", with [] for the null row). Written by parquet-cpp (ParquetSharp).
/// </summary>
public class LegacyListTest : TestBase {

    class Ints {
        [JsonPropertyName("tags")]
        public List<int>? Tags { get; set; }
    }

    class NullableInts {
        [JsonPropertyName("tags")]
        public List<int?>? Tags { get; set; }
    }

    class Item {
        [JsonPropertyName("item")]
        public int Value { get; set; }
    }

    class Items {
        [JsonPropertyName("tags")]
        [ParquetListElementRequired]
        public List<Item>? Tags { get; set; }
    }

    async Task<RawColumnData> ReadOnlyColumn(Stream s, ParquetReader reader) {
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        return await rg.ReadRawColumnDataBaseAsync(reader.Schema.GetDataFields()[0]);
    }

    [Fact]
    public async Task Three_level_list() {
        using Stream s = OpenTestFile("legacy/three_level.parquet");
        IList<NullableInts> rows = (await ParquetSerializer.DeserializeAsync<NullableInts>(s)).Data;
        Assert.Equal(new int?[] { 1, 2 }, rows[0].Tags);
        Assert.Empty(rows[1].Tags!);
        Assert.Null(rows[2].Tags);
        Assert.Equal(new int?[] { 3 }, rows[3].Tags);
    }

    // Rule 1: the repeated field is a primitive, so it is the (required) element.
    [Fact]
    public async Task Two_level_list_of_primitives() {
        using Stream s = OpenTestFile("legacy/two_level.parquet");
        await using(ParquetReader reader = await ParquetReader.CreateAsync(s)) {
            ListField list = Assert.IsType<ListField>(Assert.Single(reader.Schema.Fields));
            DataField item = Assert.IsAssignableFrom<DataField>(list.Item);
            Assert.Equal(typeof(int), item.ClrType);
            Assert.Equal((2, 1), (item.MaxDefinitionLevel, item.MaxRepetitionLevel));
        }

        s.Position = 0;
        IList<Ints> rows = (await ParquetSerializer.DeserializeAsync<Ints>(s)).Data;
        Assert.Equal(new[] { 1, 2 }, rows[0].Tags);
        Assert.Empty(rows[1].Tags!);
        Assert.Null(rows[2].Tags);
        Assert.Equal(new[] { 3 }, rows[3].Tags);
    }

    // Rule 3: a repeated group named "array" or "<list>_tuple" with one field is itself the element, a struct.
    [Theory]
    [InlineData("array_group", "array")]
    [InlineData("tuple_group", "tags_tuple")]
    public async Task Repeated_group_named_array_or_tuple_is_a_struct_element(string file, string group) {
        using Stream s = OpenTestFile($"legacy/{file}.parquet");
        await using(ParquetReader reader = await ParquetReader.CreateAsync(s)) {
            ListField list = Assert.IsType<ListField>(Assert.Single(reader.Schema.Fields));
            StructField element = Assert.IsType<StructField>(list.Item);
            Assert.Equal(group, element.Name);
            Assert.False(element.IsNullable);
            DataField item = Assert.IsAssignableFrom<DataField>(Assert.Single(element.Fields));
            Assert.Equal("item", item.Name);
            Assert.Equal((2, 1), (item.MaxDefinitionLevel, item.MaxRepetitionLevel));
            Assert.Equal(new FieldPath("tags", group, "item"), item.Path);

            RawColumnData raw = await ReadOnlyColumn(s, reader);
            Assert.Equal(new[] { 2, 2, 1, 0, 2 }, raw.DefinitionLevels.ToArray());
            Assert.Equal(new[] { 0, 1, 0, 0, 0 }, raw.RepetitionLevels.ToArray());
        }

        s.Position = 0;
        IList<Items> rows = (await ParquetSerializer.DeserializeAsync<Items>(s)).Data;
        Assert.Equal(new[] { 1, 2 }, rows[0].Tags!.ConvertAll(i => i.Value));
        Assert.Empty(rows[1].Tags!);
        Assert.Null(rows[2].Tags);
        Assert.Equal(new[] { 3 }, rows[3].Tags!.ConvertAll(i => i.Value));
    }

    // A repeated primitive outside a LIST group is a required list of required elements.
    [Fact]
    public async Task Bare_repeated_primitive() {
        using Stream s = OpenTestFile("legacy/bare_repeated.parquet");
        await using ParquetReader reader = await ParquetReader.CreateAsync(s);
        DataField f = Assert.IsAssignableFrom<DataField>(Assert.Single(reader.Schema.Fields));
        Assert.True(f.IsArray);
        RawColumnData raw = await ReadOnlyColumn(s, reader);
        Assert.Equal(new[] { 1, 1, 0, 0, 1 }, raw.DefinitionLevels.ToArray());
        Assert.Equal(new[] { 0, 1, 0, 0, 0 }, raw.RepetitionLevels.ToArray());
    }

    // Rule 2: a repeated group with several fields is the element.
    [Fact]
    public async Task Repeated_group_with_several_fields_is_a_struct_element() {
        var schema = new List<Meta.SchemaElement> {
            new() { Name = "schema", NumChildren = 1 },
            new() { Name = "points", NumChildren = 1, RepetitionType = Meta.FieldRepetitionType.OPTIONAL,
                ConvertedType = Meta.ConvertedType.LIST },
            new() { Name = "point", NumChildren = 2, RepetitionType = Meta.FieldRepetitionType.REPEATED },
            new() { Name = "x", Type = Meta.Type.INT32, RepetitionType = Meta.FieldRepetitionType.REQUIRED },
            new() { Name = "y", Type = Meta.Type.INT32, RepetitionType = Meta.FieldRepetitionType.OPTIONAL },
        };
        ParquetSchema decoded = new File.ThriftFooter(new Meta.FileMetaData { Schema = schema, RowGroups = new() })
            .CreateModelSchema(new ParquetOptions());
        ListField list = Assert.IsType<ListField>(Assert.Single(decoded.Fields));
        StructField element = Assert.IsType<StructField>(list.Item);
        Assert.Equal(2, element.Fields.Count);
        DataField[] leaves = decoded.GetDataFields();
        Assert.Equal((2, 1), (leaves[0].MaxDefinitionLevel, leaves[0].MaxRepetitionLevel));
        Assert.Equal((3, 1), (leaves[1].MaxDefinitionLevel, leaves[1].MaxRepetitionLevel));
        Assert.Equal(new FieldPath("points", "point", "y"), leaves[1].Path);
    }
}
