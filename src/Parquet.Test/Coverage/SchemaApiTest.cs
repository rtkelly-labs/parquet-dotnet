using System;
using System.Linq;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Coverage;

/// <summary>Schema declarations: what each field type refuses, and how fields, paths and schemas compare.</summary>
public class SchemaApiTest {

    [Fact]
    public void Field_path_appends_compares_and_indexes() {
        var path = new FieldPath("a");
        path.Append("");                       // ignored
        path.Append(null!);
        path.Append("b");
        Assert.Equal(2, path.Length);
        Assert.Equal(("a", "b"), (path[0], path[1]));
        Assert.True(path.Equals(path));
        Assert.True(path.Equals((object)new FieldPath("a", "b")));
        Assert.False(path.Equals((object)"a/b"));
        Assert.False(path.Equals(new FieldPath("a", "c")));
        Assert.False(path.Equals(new FieldPath("a")));
        Assert.Equal(new FieldPath("a", "b").GetHashCode(), path.GetHashCode());
    }

    [Fact]
    public void List_of_a_clr_type_declares_its_element() {
        var list = new ListField("ids", typeof(int), elementName: "element", isNullable: true);
        Assert.True(list.IsNullable);
        Assert.Equal(("element", typeof(int)), (list.Item.Name, ((DataField)list.Item).ClrType));
        var schema = new ParquetSchema(list);
        Assert.Equal("ids/list/element", schema.DataFields[0].Path.ToString());
        Assert.Equal(new ListField("ids", typeof(int), elementName: "element", isNullable: true), list);
        Assert.NotEqual(new ListField("ids", typeof(long), elementName: "element", isNullable: true), list);
        Assert.False(list.Equals(new DataField<int>("ids")));
        Assert.Equal(list.GetHashCode(), new ListField("ids", typeof(int), elementName: "element", isNullable: true).GetHashCode());
    }

    [Fact]
    public void A_list_takes_one_item_and_a_map_two() {
        ListField list = ListField.CreateWithNoItem("l", true);
        list.Assign(new DataField<int>("x"));
        Assert.Throws<InvalidOperationException>(() => list.Assign(new DataField<int>("y")));

        var map = new MapField("m", new DataField<string>("k", false), new DataField<int>("v"));
        var empty = new MapField("m2");
        empty.Assign(new DataField<string>("k", false));
        empty.Assign(new DataField<int>("v"));
        Assert.Throws<InvalidOperationException>(() => empty.Assign(new DataField<int>("w")));
        Assert.False(map.Equals(new DataField<int>("m")));
        Assert.Equal(map.GetHashCode(), new MapField("m", new DataField<string>("k", false), new DataField<int>("v")).GetHashCode());
    }

    [Fact]
    public void A_map_key_cannot_be_nullable() {
        ArgumentException e = Assert.Throws<ArgumentException>(() => new MapField("m", new DataField<string>("k", true), new DataField<int>("v")));
        Assert.Equal("keyField", e.ParamName);
    }

    [Fact]
    public void A_schema_needs_a_field() {
        Assert.Throws<ArgumentException>(() => new ParquetSchema(Array.Empty<Field>()));
        Assert.Throws<ArgumentNullException>(() => new ParquetSchema((Field[])null!));
        Assert.Throws<ArgumentNullException>(() => new ParquetSchema((System.Collections.Generic.IEnumerable<Field>)null!));
    }

    [Fact]
    public void Schemas_compare_by_their_data_fields() {
        var a = new ParquetSchema(new DataField<int>("x"), new DataField<string>("s"));
        var same = new ParquetSchema(new DataField<int>("x"), new DataField<string>("s"));
        var other = new ParquetSchema(new DataField<int>("x"), new DataField<long>("s"));
        Assert.True(a.Equals((object)a));
        Assert.True(a.Equals((object)same));
        Assert.False(a.Equals((object?)null));
        Assert.False(a.Equals((ParquetSchema?)null));
        Assert.False(a.Equals((object)"schema"));
        Assert.False(a.Equals(other));
        Assert.False(a.Equals(new ParquetSchema(new DataField<int>("x"))));
        Assert.Equal("different number of elements (2 != 1)", a.GetNotEqualsMessage(new ParquetSchema(new DataField<int>("x")), "a", "b"));
        var twoOff = new ParquetSchema(new DataField<long>("x"), new DataField<long>("s"));
        string message = a.GetNotEqualsMessage(twoOff, "mine", "theirs");
        Assert.Equal(2, message.Split(", [").Length);       // both differences, separated
        Assert.Contains("[mine: x (System.Int32)] != [theirs: x (System.Int64)]", message);
        Assert.Equal("not sure!", a.GetNotEqualsMessage(same, "a", "b"));
        ArgumentException e = Assert.Throws<ArgumentException>(() => a.FindDataField(new FieldPath("nope")));
        Assert.Contains("nope", e.Message);
    }

    [Fact]
    public void Data_fields_compare_and_clone() {
        var f = new DataField<int>("x");
        Assert.False(f.Equals("x"));
        var clone = (DataField)f.Clone();
        Assert.Equal(f, clone);
        Assert.NotSame(f, clone);
        // DateOnly and DateTime, TimeOnly and TimeSpan columns compare as the same base type
        Assert.Equal<DataField>(new DataField<DateOnly>("d"), new DataField<DateTime>("d"));
        Assert.Throws<NotSupportedException>(() => new DataField("o", typeof(DateTimeOffset)));
        Assert.Contains("DateTimeOffset support was dropped", Assert.Throws<NotSupportedException>(() => new DataField<DateTimeOffset>("o")).Message);
    }

    [Fact]
    public void A_field_outside_a_schema_cannot_be_written() {
        var schema = new ParquetSchema(new DataField<int>("x"));
        var loose = new DataField<int>("x");
        ArgumentException e = Assert.Throws<ArgumentException>(() => loose.EnsureAttachedToSchema("field"));
        Assert.Contains("not attached to any schema", e.Message);
        Assert.Equal("field", e.ParamName);
        schema.DataFields[0].EnsureAttachedToSchema("field");   // attached: no throw
    }

    [Fact]
    public void A_field_matches_only_its_own_schema_element() {
        var f = new DataField<int>("x");
        Assert.True(f.Equals(new SchemaElement { Name = "x" }));
        Assert.False(f.Equals(new SchemaElement { Name = "y" }));
    }

    [Theory]
    [InlineData(Meta.Type.BOOLEAN, typeof(bool))]
    [InlineData(Meta.Type.INT32, typeof(int))]
    [InlineData(Meta.Type.INT64, typeof(long))]
    [InlineData(Meta.Type.FLOAT, typeof(float))]
    [InlineData(Meta.Type.DOUBLE, typeof(double))]
    [InlineData(Meta.Type.BYTE_ARRAY, typeof(ReadOnlyMemory<byte>))]
    public void Raw_fields_take_their_physical_types_clr_type(Meta.Type physical, System.Type clr) {
        Assert.Equal(clr, new RawDataField("r", physical, true).ClrType);
    }

    [Fact]
    public void Raw_fields_refuse_what_cannot_be_declared() {
        Assert.Throws<NotSupportedException>(() => new RawDataField("r", Meta.Type.INT96, true));
        Assert.Throws<ArgumentException>(() => new RawDataField("r", Meta.Type.FIXED_LEN_BYTE_ARRAY, true));
        Assert.Throws<ArgumentException>(() => new RawDataField("r", Meta.Type.FIXED_LEN_BYTE_ARRAY, true, typeLength: 0));
        Assert.Equal(4, new RawDataField("r", Meta.Type.FIXED_LEN_BYTE_ARRAY, true, typeLength: 4).FixedLength);
    }

    [Fact]
    public void Time_field_refuses_an_unknown_unit() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeDataField("t", (TimeUnitPrecision)99));
    }

    [Fact]
    public async System.Threading.Tasks.Task Column_descriptors_describe_each_leaf() {
        var schema = new ParquetSchema(new DataField<int>("id"), new ListField("tags", new DataField<string>("element")));
        using var ms = new System.IO.MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<int>(schema.DataFields[0], new[] { 1 });
            await rg.WriteAsync(schema.DataFields[1], new[] { "a" }, new[] { 0 });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        ColumnDescriptor tags = reader.Columns.Single(c => c.Path.ToString() == "tags/list/element");
        Assert.Equal(ConvertedType.UTF8, tags.ConvertedType);
        Assert.Equal("tags/list/element (BYTE_ARRAY, DL 3, RL 1)", tags.ToString());
    }
}
