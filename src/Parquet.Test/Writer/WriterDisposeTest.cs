using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Writer;

/// <summary>Disposing a writer twice writes its footer once.</summary>
public class WriterDisposeTest {

    [Fact]
    public async Task A_second_dispose_writes_nothing() {
        var field = new DataField<int>("id");
        using var ms = new MemoryStream();
        ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms);
        using(ParquetRowGroupWriter rg = writer.CreateRowGroup())
            await rg.WriteAsync<int>(field, new[] { 1, 2, 3 });
        await writer.DisposeAsync();
        long once = ms.Length;
        await writer.DisposeAsync();
        Assert.Equal(once, ms.Length);

        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.Equal(3, reader.Metadata!.NumRows);
    }
}
