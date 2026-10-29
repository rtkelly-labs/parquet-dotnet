using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// Byte-array and text values are slices of one array per page: they must stay intact after later pages are read
/// (the page buffers are pooled and reused).
/// </summary>
public class PageValuesTest {

    [Fact]
    public async Task Values_survive_reading_later_pages() {
        var s = new DataField("s", typeof(string), isNullable: false);
        var b = new DataField("b", typeof(byte[]), isNullable: false);
        string[][] strings = Enumerable.Range(0, 3)
            .Select(g => Enumerable.Range(0, 1000).Select(i => i % 7 == 0 ? "" : $"g{g} é\U0001F600 {i}").ToArray())
            .ToArray();
        byte[][][] bytes = Enumerable.Range(0, 3)
            .Select(g => Enumerable.Range(0, 1000).Select(i => Enumerable.Repeat((byte)(g * 31 + i), i % 5).ToArray()).ToArray())
            .ToArray();

        using var ms = new MemoryStream();
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(s, b), ms)) {
            for(int g = 0; g < 3; g++) {
                using ParquetRowGroupWriter rg = w.CreateRowGroup();
                await rg.WriteAsync(s, strings[g]);
                await rg.WriteAsync(b, bytes[g]);
            }
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        // the raw values are the slices themselves, kept until every row group is read
        var chars = new ReadOnlyMemory<char>[3][];
        var raw = new ReadOnlyMemory<byte>[3][];
        DataField[] fields = reader.Schema.GetDataFields();
        for(int g = 0; g < 3; g++) {
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(g);
            chars[g] = new ReadOnlyMemory<char>[1000];
            raw[g] = new ReadOnlyMemory<byte>[1000];
            await rg.ReadRawAsync<ReadOnlyMemory<char>>(fields[0], chars[g], null, null);
            await rg.ReadRawAsync<ReadOnlyMemory<byte>>(fields[1], raw[g], null, null);
        }
        for(int g = 0; g < 3; g++) {
            Assert.Equal(strings[g], chars[g].Select(m => new string(m.Span)));
            Assert.Equal(bytes[g], raw[g].Select(m => m.ToArray()));
        }
    }
}
