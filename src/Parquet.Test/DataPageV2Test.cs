using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// DATA_PAGE_V2 pages written by parquet-cpp (ParquetSharp 25.0.1.1), several per column chunk, Snappy-compressed and
/// uncompressed: an optional list&lt;int?&gt; "tags" (null when i % 5 == 0, else the first i % 4 of [i, null, i + 1])
/// and an optional string "name" (null when i % 7 == 0, else "name{i % 10}"), for rows i = 0..999.
/// </summary>
public class DataPageV2Test : TestBase {

    [Theory]
    [InlineData("v2_snappy")]
    [InlineData("v2_uncompressed")]
    public async Task Reads_v2_pages_with_repetition_and_definition_levels(string file) {
        using Stream s = OpenTestFile($"v2/{file}.parquet");
        await using ParquetReader reader = await ParquetReader.CreateAsync(s);
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        DataField[] fields = reader.Schema.GetDataFields();

        // tags: rebuild the rows from values and levels
        using RawColumnData<int> tags = await rg.ReadRawColumnDataAsync<int>(fields[0]);
        var rows = new List<List<int?>?>();
        int v = 0;
        for(int i = 0; i < tags.DefinitionLevels.Length; i++) {
            int d = tags.DefinitionLevels[i];
            if(tags.RepetitionLevels[i] == 0)
                rows.Add(d == 0 ? null : new List<int?>());
            if(d >= 2)
                rows[^1]!.Add(d == 3 ? tags.Values[v++] : null);
        }
        Assert.Equal(1000, rows.Count);
        for(int i = 0; i < 1000; i++) {
            List<int?>? expected = i % 5 == 0 ? null : new int?[] { i, null, i + 1 }.Take(i % 4).ToList();
            Assert.Equal(expected, rows[i]);
        }

        using RawColumnData<System.ReadOnlyMemory<char>> names = await rg.ReadRawColumnDataAsync<System.ReadOnlyMemory<char>>(fields[1]);
        var actual = new List<string?>();
        v = 0;
        foreach(int d in names.DefinitionLevels)
            actual.Add(d == 0 ? null : names.Values[v++].ToString());
        Assert.Equal(Enumerable.Range(0, 1000).Select(i => i % 7 == 0 ? null : "name" + (i % 10)), actual);
    }
}
