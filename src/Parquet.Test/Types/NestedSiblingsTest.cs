using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>
/// Leaves written to one row group under a shared list, map or struct must describe the same elements and nulls
/// (Lake.NET decision 11): a write that disagrees with its siblings is refused instead of making a corrupt file.
/// </summary>
public class NestedSiblingsTest : TestBase {

    [Fact]
    public async Task Pyarrows_nested_file_rewrites_leaf_by_leaf() {
        // every leaf of data/nested/nested_layout.parquet, levels and values as read, written again: siblings agree
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("nested/nested_layout.parquet"));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(reader.Schema, ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            foreach(DataField field in reader.Schema.GetDataFields()) {
                int entries = (int)rowGroup.GetMetadata(field)!.MetaData!.NumValues;
                int[] def = new int[entries], rep = new int[entries];
                Memory<int>? defs = field.MaxDefinitionLevel > 0 ? new Memory<int>(def) : (Memory<int>?)null;
                Memory<int>? reps = field.MaxRepetitionLevel > 0 ? new Memory<int>(rep) : (Memory<int>?)null;
                if(field.ClrType == typeof(int)) {
                    int[] values = new int[entries];
                    await rowGroup.ReadRawAsync<int>(field, values, defs, reps);
                    await rg.WriteAllPartsAsync<int>(field, values.AsMemory(0, Packed(field, def, entries)), defs?.Slice(0, entries), reps?.Slice(0, entries), default);
                } else if(field.ClrType == typeof(long)) {
                    long[] values = new long[entries];
                    await rowGroup.ReadRawAsync<long>(field, values, defs, reps);
                    await rg.WriteAllPartsAsync<long>(field, values.AsMemory(0, Packed(field, def, entries)), defs?.Slice(0, entries), reps?.Slice(0, entries), default);
                } else {
                    var values = new ReadOnlyMemory<char>[entries];
                    await rowGroup.ReadRawAsync<ReadOnlyMemory<char>>(field, values, defs, reps);
                    await rg.WriteAllPartsAsync<ReadOnlyMemory<char>>(field, values.AsMemory(0, Packed(field, def, entries)), defs?.Slice(0, entries), reps?.Slice(0, entries), default);
                }
            }
        }
        ms.Position = 0;
        await using ParquetReader back = await ParquetReader.CreateAsync(ms);
        Assert.Equal(7, back.Metadata!.NumRows);
    }

    static int Packed(DataField field, int[] def, int entries) {
        if(field.MaxDefinitionLevel == 0)
            return entries;
        int n = 0;
        for(int i = 0; i < entries; i++)
            if(def[i] == field.MaxDefinitionLevel)
                n++;
        return n;
    }

    [Fact]
    public async Task A_maps_keys_and_values_must_describe_the_same_pairs() {
        var key = new DataField("key", typeof(string), isNullable: false);
        var value = new DataField<int?>("value");
        var schema = new ParquetSchema(new MapField("m", key, value));
        using var ms = new MemoryStream();
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();
        // rows: {a, b}, {c}
        await rg.WriteAsync(key, new[] { "a", "b", "c" }, new[] { 0, 1, 0 });
        // values describing {x}, {y, z}: two rows, but different pairs
        ParquetException e = await Assert.ThrowsAsync<ParquetException>(() => rg.WriteAsync<int>(value, new int?[] { 1, 2, 3 }, new[] { 0, 0, 1 }));
        Assert.Contains("m/key_value", e.Message);
    }

    [Fact]
    public async Task A_structs_fields_must_agree_on_where_it_is_null() {
        var a = new DataField<int?>("a");
        var b = new DataField<int?>("b");
        var schema = new ParquetSchema(new StructField("s", a, b));
        using var ms = new MemoryStream();
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();
        // s is null in row 1 for a (definition level 0), present with a null b in row 1 for b (level 1)
        await rg.WriteAllPartsAsync<int>(a, new[] { 1, 3 }, new[] { 2, 0, 2 }, null, default);
        ParquetException e = await Assert.ThrowsAsync<ParquetException>(() => rg.WriteAllPartsAsync<int>(b, new[] { 4, 6 }, new[] { 2, 1, 2 }, null, default));
        Assert.Contains("'s' is null", e.Message);
    }

    [Fact]
    public async Task Leaves_at_different_depths_agree_on_what_they_share() {
        // a list of structs {x: int, ys: list<int>}: x and ys/element share the outer list and the struct, not the inner list
        var x = new DataField<int?>("x");
        var y = new DataField<int?>("element");
        var schema = new ParquetSchema(new ListField("l", new StructField("element", x, new ListField("ys", y))));
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            // rows: [{x 1, ys [1, 2]}, {x 2, ys []}], []
            await rg.WriteAsync<int>(x, new int?[] { 1, 2, null }, new[] { 0, 1, 0 }.AsMemory());
            await rg.WriteAsync<int>(y, new int?[] { 1, 2, null, null }, new[] { 0, 2, 1, 0 }.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.Equal(2, reader.Metadata!.NumRows);
    }
}
