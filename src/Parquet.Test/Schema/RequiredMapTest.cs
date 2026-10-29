using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Schema;

public class RequiredMapTest {

    [Fact]
    public async Task A_required_map_is_written_REQUIRED_with_levels_to_match() {
        var key = new DataField("key", typeof(string), isNullable: false);
        var value = new DataField<long?>("value");
        var map = new MapField("m", key, value, isNullable: false);
        var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(map), ms)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            // rows: {x: 1}, {}, {y: null}
            await rg.WriteAllPartsAsync<ReadOnlyMemory<char>>(key, new[] { "x".AsMemory(), "y".AsMemory() }, new[] { 1, 0, 1 }, new[] { 0, 0, 0 }, default);
            await rg.WriteAllPartsAsync<long>(value, new[] { 1L }, new[] { 2, 0, 1 }, new[] { 0, 0, 0 }, default);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.Equal(FieldRepetitionType.REQUIRED, reader.Metadata!.Schema[1].RepetitionType);
        DataField[] fields = reader.Schema.GetDataFields();
        Assert.Equal((1, 1), (fields[0].MaxDefinitionLevel, fields[0].MaxRepetitionLevel));
        Assert.Equal((2, 1), (fields[1].MaxDefinitionLevel, fields[1].MaxRepetitionLevel));
        var keys = new ReadOnlyMemory<char>[3];
        var defs = new int[3];
        await reader.OpenRowGroupReader(0).ReadRawAsync<ReadOnlyMemory<char>>(fields[0], keys, defs, new int[3]);
        Assert.Equal(new[] { 1, 0, 1 }, defs);
    }
}
