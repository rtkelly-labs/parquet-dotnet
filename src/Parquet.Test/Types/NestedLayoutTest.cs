using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>
/// NestedLayout (Lake.NET decision 11): a leaf's levels as Arrow's offsets and validity. The oracle is Arrow itself:
/// data/nested/nested_layout.json holds, for each leaf of data/nested/nested_layout.parquet (pyarrow 26), the offsets
/// and validity pyarrow builds when it reads the file — lists of lists, a map of lists, a list of structs holding a
/// list, nested structs, a required list.
/// </summary>
public class NestedLayoutTest : TestBase {

    sealed record Leaf(DataField Field, int[] Definition, int[] Repetition, int Entries);

    async Task<(ParquetSchema Schema, Dictionary<string, Leaf> Leaves)> ReadLevelsAsync() {
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("nested/nested_layout.parquet"));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var leaves = new Dictionary<string, Leaf>();
        foreach(DataField field in reader.Schema.GetDataFields()) {
            int entries = (int)rowGroup.GetMetadata(field)!.MetaData!.NumValues;
            int[] def = new int[entries], rep = new int[entries];
            Memory<int>? defs = field.MaxDefinitionLevel > 0 ? new Memory<int>(def) : (Memory<int>?)null;
            Memory<int>? reps = field.MaxRepetitionLevel > 0 ? new Memory<int>(rep) : (Memory<int>?)null;
            if(field.ClrType == typeof(int))
                await rowGroup.ReadRawAsync<int>(field, new int[entries], defs, reps);
            else if(field.ClrType == typeof(long))
                await rowGroup.ReadRawAsync<long>(field, new long[entries], defs, reps);
            else
                await rowGroup.ReadRawAsync<ReadOnlyMemory<char>>(field, new ReadOnlyMemory<char>[entries], defs, reps);
            leaves[field.Path.ToString()] = new Leaf(field, def, rep, entries);
        }
        return (reader.Schema, leaves);
    }

    public static TheoryData<string> LeafPaths() {
        var data = new TheoryData<string>();
        foreach(string path in new[] { "ll/list/element/list/element", "mp/key_value/key", "mp/key_value/value/list/element",
            "ls/list/element/a", "ls/list/element/b/list/element", "st/p/q", "req/list/element" })
            data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(LeafPaths))]
    public async Task Layout_is_Arrows(string path) {
        (ParquetSchema schema, Dictionary<string, Leaf> leaves) = await ReadLevelsAsync();
        Leaf leaf = leaves[path];
        using JsonDocument oracle = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine("data", "nested", "nested_layout.json")));
        JsonElement expected = oracle.RootElement.GetProperty("leaves").GetProperty(path);

        NestedLayout layout = NestedLayout.FromLevels(schema, leaf.Field, leaf.Definition, leaf.Repetition, leaf.Entries);

        Assert.Equal(oracle.RootElement.GetProperty("rows").GetInt32(), layout.SlotCounts[0]);
        int[][] offsets = expected.GetProperty("offsets").EnumerateArray().Select(o => o.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray();
        Assert.Equal(offsets.Length, layout.RepeatedCount);
        for(int i = 0; i < offsets.Length; i++)
            Assert.Equal(offsets[i], layout.Offsets[i]);
        var validity = expected.GetProperty("validity").EnumerateArray()
            .Select(v => (Layer: v.GetProperty("layer").GetInt32(), Valid: v.GetProperty("valid").EnumerateArray().Select(x => x.GetBoolean()).ToArray())).ToArray();
        Assert.Equal(validity.Length, layout.Validity.Count);
        for(int i = 0; i < validity.Length; i++) {
            Assert.Equal(validity[i].Layer, layout.Validity[i].Layer);
            Assert.Equal(validity[i].Valid, layout.Validity[i].IsValid);
        }
        Assert.Equal(expected.GetProperty("values").GetInt32(), layout.ValueCount);
    }

    [Theory]
    [MemberData(nameof(LeafPaths))]
    public async Task Levels_round_trip(string path) {
        (ParquetSchema schema, Dictionary<string, Leaf> leaves) = await ReadLevelsAsync();
        Leaf leaf = leaves[path];
        NestedLayout layout = NestedLayout.FromLevels(schema, leaf.Field, leaf.Definition, leaf.Repetition, leaf.Entries);
        (int[] def, int[] rep) = layout.ToLevels(schema, leaf.Field);
        Assert.Equal(leaf.Definition.Take(leaf.Entries), leaf.Field.MaxDefinitionLevel > 0 ? def : leaf.Definition.Take(leaf.Entries));
        if(leaf.Field.MaxRepetitionLevel > 0)
            Assert.Equal(leaf.Repetition.Take(leaf.Entries), rep);
        Assert.Equal(leaf.Entries, def.Length);
    }

    [Fact]
    public async Task A_List_of_List_assembles_from_offsets() {
        // what generated code does: walk offset ranges, no level arithmetic
        (ParquetSchema schema, Dictionary<string, Leaf> leaves) = await ReadLevelsAsync();
        Leaf leaf = leaves["ll/list/element/list/element"];
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("nested/nested_layout.parquet"));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        int[] packed = new int[leaf.Entries];
        await rowGroup.ReadRawAsync<int>(leaf.Field, packed, new int[leaf.Entries], new int[leaf.Entries]);
        NestedLayout layout = NestedLayout.FromLevels(schema, leaf.Field, leaf.Definition, leaf.Repetition, leaf.Entries);

        IReadOnlyList<bool> outerValid = layout.Validity[0].IsValid, innerValid = layout.Validity[1].IsValid, valueValid = layout.Validity[2].IsValid;
        int next = 0;
        var rows = new List<List<List<int?>?>?>();
        for(int r = 0; r < layout.SlotCounts[0]; r++) {
            if(!outerValid[r]) { rows.Add(null); continue; }
            var outer = new List<List<int?>?>();
            for(int i = layout.Offsets[0][r]; i < layout.Offsets[0][r + 1]; i++) {
                if(!innerValid[i]) { outer.Add(null); continue; }
                var inner = new List<int?>();
                for(int j = layout.Offsets[1][i]; j < layout.Offsets[1][i + 1]; j++)
                    inner.Add(valueValid[j] ? packed[next++] : null);
                outer.Add(inner);
            }
            rows.Add(outer);
        }
        Assert.Equal(layout.ValueCount, next);
        // [[1, 2], [3]], [], None, [[None], []], [[4]], [None, [5, 6, 7]], [[]]
        string Show(List<List<int?>?>? row) => row == null ? "null" : "[" + string.Join(",", row.Select(i => i == null ? "null" : "[" + string.Join(",", i.Select(v => v?.ToString() ?? "null")) + "]")) + "]";
        Assert.Equal(new[] { "[[1,2],[3]]", "[]", "null", "[[null],[]]", "[[4]]", "[null,[5,6,7]]", "[[]]" }, rows.Select(Show));
    }

    [Fact]
    public void Levels_that_are_not_the_leafs_are_refused() {
        var schema = new ParquetSchema(new ListField("l", new DataField<int?>("x")));
        DataField leaf = schema.GetDataFields()[0];
        Assert.Throws<ArgumentException>(() => NestedLayout.FromLevels(schema, leaf, [3, 3], [1, 0], 2));   // first entry continues a list
        Assert.Throws<ArgumentException>(() => NestedLayout.FromLevels(schema, leaf, [9], [0], 1));         // beyond the maximum
        Assert.Throws<ArgumentException>(() => NestedLayout.FromLevels(schema, leaf, [3, 1], [0, 1], 2));   // continues an empty list
        Assert.Throws<ArgumentException>(() => NestedLayout.FromLevels(new ParquetSchema(new DataField<int>("other")), leaf, [3], [0], 1));
    }
}
