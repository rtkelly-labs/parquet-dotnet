using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Serialization;
using Xunit;

namespace Parquet.Test.Serialisation;

/// <summary>Lists of nullable elements keep their null elements, and empty lists stay empty rather than null.</summary>
public class NullableListElementTest : TestBase {

    class Ints {
        public List<int?>? Tags { get; set; }
    }

    class Strings {
        public List<string?>? Tags { get; set; }
    }

    class Arrays {
        public int?[]? Tags { get; set; }
    }

    [Fact]
    public async Task Nullable_ints() {
        Ints[] data = [new() { Tags = [1, null, 2] }, new() { Tags = [] }, new(), new() { Tags = [null] }, new() { Tags = [3] }];
        using var ms = new MemoryStream();
        await ParquetSerializer.SerializeAsync(data, ms);

        ms.Position = 0;
        await using(ParquetReader reader = await ParquetReader.CreateAsync(ms)) {
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
            RawColumnData raw = await rg.ReadRawColumnDataBaseAsync(reader.Schema.GetDataFields()[0]);
            Assert.Equal(new[] { 3, 2, 3, 1, 0, 2, 3 }, raw.DefinitionLevels.ToArray());
        }

        ms.Position = 0;
        IList<Ints> rows = (await ParquetSerializer.DeserializeAsync<Ints>(ms)).Data;
        Assert.Equal(new int?[] { 1, null, 2 }, rows[0].Tags);
        Assert.Empty(rows[1].Tags!);
        Assert.Null(rows[2].Tags);
        Assert.Equal(new int?[] { null }, rows[3].Tags);
        Assert.Equal(new int?[] { 3 }, rows[4].Tags);
    }

    [Fact]
    public async Task Nullable_strings() {
        Strings[] data = [new() { Tags = ["a", null, "b"] }, new() { Tags = [] }, new(), new() { Tags = [null] }];
        using var ms = new MemoryStream();
        await ParquetSerializer.SerializeAsync(data, ms);
        ms.Position = 0;
        IList<Strings> rows = (await ParquetSerializer.DeserializeAsync<Strings>(ms)).Data;
        Assert.Equal(new[] { "a", null, "b" }, rows[0].Tags);
        Assert.Empty(rows[1].Tags!);
        Assert.Null(rows[2].Tags);
        Assert.Equal(new string?[] { null }, rows[3].Tags);
    }

    [Fact]
    public async Task Nullable_int_arrays() {
        Arrays[] data = [new() { Tags = [1, null, 2] }, new() { Tags = [] }, new()];
        using var ms = new MemoryStream();
        await ParquetSerializer.SerializeAsync(data, ms);
        ms.Position = 0;
        IList<Arrays> rows = (await ParquetSerializer.DeserializeAsync<Arrays>(ms)).Data;
        Assert.Equal(new int?[] { 1, null, 2 }, rows[0].Tags);
        Assert.Empty(rows[1].Tags!);
        Assert.Null(rows[2].Tags);
    }
}
