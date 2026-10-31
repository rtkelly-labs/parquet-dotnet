using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using PS = ParquetSharp;
using PSchema = ParquetSharp.Schema;

namespace Parquet.Test.Reader;

/// <summary>Boolean data pages as parquet-cpp writes them.</summary>
public class RleBooleanPagesTest : IDisposable {

    private readonly string _dir = Directory.CreateTempSubdirectory("rle-booleans").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>
    /// parquet-cpp writes booleans in data page v2 with the RLE encoding: one bit wide and, in a data page, prefixed
    /// with its length. Reading them with the type length as the bit width (0) divided by zero.
    /// </summary>
    [Fact]
    public async Task Rle_booleans_in_data_page_v2() {
        string path = Path.Combine(_dir, "bools.parquet");
        bool[] values = { true, false, false, true, true, true, false, true, false };
        using(var node = new PSchema.PrimitiveNode("b", PS.Repetition.Required, PS.LogicalType.None(), PS.PhysicalType.Boolean))
        using(var root = new PSchema.GroupNode("schema", PS.Repetition.Required, new PSchema.Node[] { node }))
        using(PS.WriterProperties properties = new PS.WriterPropertiesBuilder().DataPageVersion(PS.ParquetDataPageVersion.V2).Build())
        using(var file = new PS.ParquetFileWriter(path, root, properties)) {
            using(PS.RowGroupWriter rg = file.AppendRowGroup())
            using(var writer = (PS.ColumnWriter<bool>)rg.NextColumn())
                writer.WriteBatch(values);
            file.Close();
        }

        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        bool[] read = new bool[values.Length];
        await rowGroup.ReadRawAsync<bool>(reader.Schema.GetDataFields()[0], read, null, null);
        Assert.Equal(values, read);
    }
}
