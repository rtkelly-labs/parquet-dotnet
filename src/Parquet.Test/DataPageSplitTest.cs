using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;
using PS = ParquetSharp;

namespace Parquet.Test;

/// <summary>
/// <see cref="ParquetOptions.MaxDataPageValues"/> splits column chunks into data pages at row boundaries, which this
/// library and parquet-cpp read back exactly; unset, a chunk is one data page.
/// </summary>
public class DataPageSplitTest {

    static async Task<byte[]> WriteAsync(ParquetSchema schema, ParquetOptions options, Func<ParquetRowGroupWriter, Task> write) {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await write(rg);
        }
        return ms.ToArray();
    }

    /// <summary>The data page headers of a column chunk, in order, and the levels each counts.</summary>
    static async Task<List<int>> DataPageValueCountsAsync(byte[] file, int column) {
        ColumnMetaData meta;
        using(var ms = new MemoryStream(file))
        await using(ParquetReader reader = await ParquetReader.CreateAsync(ms))
            meta = reader.Metadata!.RowGroups[0].Columns[column].MetaData!;
        long start = meta.DictionaryPageOffset ?? meta.DataPageOffset;
        long end = start + meta.TotalCompressedSize;
        var counts = new List<int>();
        using var stream = new MemoryStream(file) { Position = start };
        while(stream.Position < end) {
            PageHeader header = PageHeader.Read(new ThriftCompactProtocolReader(stream));
            if(header.Type == PageType.DATA_PAGE)
                counts.Add(header.DataPageHeader!.NumValues);
            stream.Position += header.CompressedPageSize;
        }
        return counts;
    }

    static T[] ReadWithParquetCpp<T>(byte[] file, int column, int rows) {
        string path = Path.GetTempFileName();
        System.IO.File.WriteAllBytes(path, file);
        try {
            using var reader = new PS.ParquetFileReader(path);
            using PS.RowGroupReader rg = reader.RowGroup(0);
            return rg.Column(column).LogicalReader<T>().ReadAll(rows);
        } finally {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task Unset_writes_one_page_per_chunk() {
        var id = new DataField<int>("id");
        byte[] file = await WriteAsync(new ParquetSchema(id), new ParquetOptions(), rg => rg.WriteAsync<int>(id, Enumerable.Range(0, 1000).ToArray()));
        Assert.Equal(new[] { 1000 }, await DataPageValueCountsAsync(file, 0));
    }

    [Theory]
    [InlineData(EncodingHint.Default)]
    [InlineData(EncodingHint.DeltaBinaryPacked)]
    [InlineData(EncodingHint.ByteSplitStream)]
    public async Task Splits_flat_columns_into_pages_that_read_back(EncodingHint hint) {
        var id = new DataField<int>("id");
        var score = new DataField<int?>("score");
        int[] ids = Enumerable.Range(0, 1000).ToArray();
        // whole pages of nulls: an empty delta page still needs its header, or other readers fail
        int?[] scores = ids.Select(i => i % 250 < 120 ? (int?)null : i * 3).ToArray();
        var options = new ParquetOptions { MaxDataPageValues = 100, ColumnEncodingHints = { ["id"] = hint, ["score"] = hint } };
        byte[] file = await WriteAsync(new ParquetSchema(id, score), options, async rg => {
            await rg.WriteAsync<int>(id, ids);
            await rg.WriteAsync<int>(score, scores);
        });

        Assert.Equal(Enumerable.Repeat(100, 10), await DataPageValueCountsAsync(file, 0));
        Assert.Equal(Enumerable.Repeat(100, 10), await DataPageValueCountsAsync(file, 1));

        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        int[] readIds = new int[ids.Length];
        int?[] readScores = new int?[ids.Length];
        await rowGroup.ReadAsync<int>(reader.Schema.GetDataFields()[0], readIds);
        await rowGroup.ReadAsync<int>(reader.Schema.GetDataFields()[1], new Memory<int?>(readScores));
        Assert.Equal(ids, readIds);
        Assert.Equal(scores, readScores);

        Assert.Equal(ids, ReadWithParquetCpp<int>(file, 0, ids.Length));
        Assert.Equal(scores, ReadWithParquetCpp<int?>(file, 1, ids.Length));
    }

    [Fact]
    public async Task Pages_of_a_list_column_end_at_row_boundaries() {
        var tags = new DataField<int[]>("tags");
        var lists = Enumerable.Range(0, 200).Select(i => Enumerable.Range(i, i % 7).ToArray()).ToArray();
        int[] values = lists.SelectMany(l => l).ToArray();
        int[] repetition = lists.SelectMany(l => l.Length == 0 ? new[] { 0 } : l.Select((_, j) => j == 0 ? 0 : 1)).ToArray();
        int[] definition = lists.SelectMany(l => l.Length == 0 ? new[] { 0 } : l.Select(_ => 1)).ToArray();
        var options = new ParquetOptions { MaxDataPageValues = 10 };
        byte[] file = await WriteAsync(new ParquetSchema(tags), options,
            rg => rg.WriteAllPartsAsync<int>(tags, values, (ReadOnlyMemory<int>?)definition, (ReadOnlyMemory<int>?)repetition, default));

        List<int> pages = await DataPageValueCountsAsync(file, 0);
        Assert.True(pages.Count > 1);
        Assert.Equal(repetition.Length, pages.Sum());
        int at = 0;
        foreach(int count in pages) {
            Assert.Equal(0, repetition[at]);   // every page starts a row
            at += count;
        }

        // parquet-cpp reads lists and maps split into pages in the conformance harness; here, the levels and values back
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        int[] readValues = new int[repetition.Length];
        int[] readDefinition = new int[repetition.Length];
        int[] readRepetition = new int[repetition.Length];
        await rowGroup.ReadRawAsync<int>(reader.Schema.GetDataFields()[0], readValues, readDefinition, readRepetition);
        Assert.Equal(values, readValues.Take(values.Length));
        Assert.Equal(definition, readDefinition);
        Assert.Equal(repetition, readRepetition);
    }
}
