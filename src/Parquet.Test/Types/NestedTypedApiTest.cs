using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>
/// Columns inside optional structs and lists, written and read through the typed API (WriteAsync, ReadAsync): a
/// repeated column has one level per value slot, more than it has rows, and a value is null wherever its definition
/// level is below the column's maximum.
/// </summary>
public class NestedTypedApiTest : TestBase {

    static async Task<MemoryStream> WriteAsync(ParquetSchema schema, Func<ParquetRowGroupWriter, DataField[], Task> write) {
        var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await write(rg, schema.GetDataFields());
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Values_without_nulls_under_an_optional_struct() {
        var schema = new ParquetSchema(new StructField("st", new DataField<long>("a"), new DataField("s", typeof(string), isNullable: false)));
        using MemoryStream file = await WriteAsync(schema, async (rg, f) => {
            await rg.WriteAsync<long>(f[0], new long[] { 7, 8, 9 });     // threw "Definition levels are not ready yet"
            await rg.WriteAsync(f[1], new[] { "x", "y", "z" });
        });

        await using ParquetReader reader = await ParquetReader.CreateAsync(file);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        var a = new long?[3];
        var s = new string?[3];
        await rg.ReadAsync<long>(f[0], a);
        await rg.ReadAsync(f[1], new Memory<string?>(s));            // a required string under an optional struct
        Assert.Equal(new long?[] { 7, 8, 9 }, a);
        Assert.Equal(new[] { "x", "y", "z" }, s);
    }

    [Fact]
    public async Task List_of_nullable_ints_round_trips_with_its_levels() {
        // rows: [1, null, 3], [], [4]
        var element = new DataField<int?>("x");
        var schema = new ParquetSchema(new ListField("l", element));
        int?[] values = [1, null, 3, null, 4];
        int[] repetition = [0, 1, 1, 0, 0];
        using MemoryStream file = await WriteAsync(schema, (rg, f) => rg.WriteAsync<int>(f[0], values, repetition));

        await using ParquetReader reader = await ParquetReader.CreateAsync(file);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        Assert.Equal(3, rg.RowCount);
        var read = new int?[5];
        int[] readRepetition = new int[5];
        await rg.ReadAsync<int>(reader.Schema.GetDataFields()[0], read, readRepetition);   // threw: the buffer was sized by rows
        Assert.Equal(values, read);
        Assert.Equal(repetition, readRepetition);
    }

    [Fact]
    public async Task Strings_and_bytes_in_a_list_are_null_below_the_maximum_level() {
        // rows: ["a", null], null list, ["b"]; a null element and a missing list are both null values, at levels 2 and 0
        var str = new DataField<string>("s");
        var bin = new DataField<byte[]>("b");
        var schema = new ParquetSchema(new ListField("ls", str), new ListField("lb", bin));
        int[] repetition = [0, 1, 0, 0];
        using MemoryStream file = await WriteAsync(schema, async (rg, f) => {
            await rg.WriteAsync(f[0], new[] { "a", null, null, "b" }, repetition);
            await rg.WriteAsync(f[1], new[] { new byte[] { 1 }, null, null, new byte[] { 2, 3 } }, repetition);
        });

        await using ParquetReader reader = await ParquetReader.CreateAsync(file);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        Assert.Equal(3, f[0].MaxDefinitionLevel);
        var s = new string?[4];
        var b = new byte[]?[4];
        int[] sr = new int[4], br = new int[4];
        await rg.ReadAsync(f[0], new Memory<string?>(s), sr);
        await rg.ReadAsync(f[1], new Memory<byte[]?>(b), br);
        Assert.Equal(new[] { "a", null, null, "b" }, s);
        Assert.Equal(new[] { "01", null, null, "0203" }, b.Select(x => x == null ? null : Convert.ToHexString(x)));
        Assert.Equal(repetition, sr);
        Assert.Equal(repetition, br);
    }

    [Fact]
    public async Task Pyarrow_nested_columns_read_through_the_typed_API() {
        // list_empty_alt.parquet: a list of strings, rows ["1", "2", "3"], [], ["1", "2", "3"], []
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("list_empty_alt.parquet"));
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataField list = reader.Schema.GetDataFields()[1];
        var values = new string?[8];
        int[] repetition = new int[8];
        await rg.ReadAsync(list, new Memory<string?>(values), repetition);
        Assert.Equal(new[] { "1", "2", "3", null, "1", "2", "3", null }, values);
        Assert.Equal(new[] { 0, 1, 1, 0, 0, 1, 1, 0 }, repetition);
    }
}
