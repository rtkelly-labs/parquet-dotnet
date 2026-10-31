using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using PS = ParquetSharp;
using PSchema = ParquetSharp.Schema;

namespace Parquet.Test.Reader;

/// <summary>Row groups of no rows, as parquet-cpp writes them.</summary>
public class EmptyRowGroupTest : IDisposable {

    private readonly string _dir = Directory.CreateTempSubdirectory("empty-row-groups").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>
    /// A row group with no rows: without a dictionary, parquet-cpp writes its column chunks with no pages and no page
    /// offsets, and finding the first page took the minimum of no offsets.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_row_groups(bool dataPageV2) {
        string path = Path.Combine(_dir, "empty.parquet");
        using(var node = new PSchema.PrimitiveNode("v", PS.Repetition.Optional, PS.LogicalType.None(), PS.PhysicalType.Int64))
        using(var root = new PSchema.GroupNode("schema", PS.Repetition.Required, new PSchema.Node[] { node }))
        // without a dictionary, parquet-cpp writes no page at all for an empty chunk
        using(PS.WriterProperties properties = new PS.WriterPropertiesBuilder().DisableDictionary()
            .DataPageVersion(dataPageV2 ? PS.ParquetDataPageVersion.V2 : PS.ParquetDataPageVersion.V1).Build())
        using(var file = new PS.ParquetFileWriter(path, root, properties)) {
            using(PS.RowGroupWriter empty = file.AppendRowGroup())
            using(var writer = (PS.ColumnWriter<long>)empty.NextColumn())
                writer.WriteBatch(Array.Empty<long>());
            using(PS.RowGroupWriter rg = file.AppendRowGroup())
            using(var writer = (PS.ColumnWriter<long>)rg.NextColumn())
                writer.WriteBatch(2, new short[] { 1, 0 }, null, new long[] { 7 });
            file.Close();
        }

        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        Assert.Equal(2, reader.RowGroupCount);
        using(ParquetRowGroupReader empty = reader.OpenRowGroupReader(0)) {
            Assert.Equal(0, empty.RowCount);
            await empty.ReadRawAsync<long>(reader.Schema.GetDataFields()[0], new long[1], new int[1], null);
        }
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(1);
        long[] values = new long[2];
        int[] definitions = new int[2];
        await rowGroup.ReadRawAsync<long>(reader.Schema.GetDataFields()[0], values, definitions, null);
        Assert.Equal(7L, values[0]);
        Assert.Equal(new[] { 1, 0 }, definitions);
    }
}
