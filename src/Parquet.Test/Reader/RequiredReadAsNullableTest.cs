using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Reader;

public class RequiredReadAsNullableTest {

    [Fact]
    public async Task Required_column_read_as_nullable_has_no_nulls_whatever_the_pool_holds() {
        var id = new DataField<int>("id");
        var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(id), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<int>(id, new[] { 1, 2, 3, 4 });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rgr = reader.OpenRowGroupReader(0);

        // what another reader of the shared pool may leave behind: a required column has no definition levels to
        // overwrite it with
        int[][] rented = new int[64][];
        for(int i = 0; i < rented.Length; i++) {
            rented[i] = ArrayPool<int>.Shared.Rent(4);
            rented[i].AsSpan().Fill(-1);
        }
        foreach(int[] a in rented)
            ArrayPool<int>.Shared.Return(a);

        int?[] values = new int?[4];
        await rgr.ReadAsync(id, values.AsMemory());

        Assert.Equal(new int?[] { 1, 2, 3, 4 }, values);
    }
}
