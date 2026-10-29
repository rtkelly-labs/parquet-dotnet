using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>A column chunk's dictionary_page_offset and data_page_offset point at its dictionary and first data page.</summary>
public class PageOffsetTest {

    [Fact]
    public async Task Offsets_point_at_their_pages() {
        var s = new DataField<string>("s");
        var ms = new MemoryStream();
        var options = new ParquetOptions { ColumnEncodingHints = { ["s"] = EncodingHint.Dictionary } };
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(s), ms, options)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync(s, Enumerable.Range(0, 100).Select(i => "v" + (i % 3)).ToArray());
        }
        ms.Position = 0;
        ColumnMetaData meta;
        await using(ParquetReader reader = await ParquetReader.CreateAsync(ms, leaveStreamOpen: true))
            meta = reader.Metadata!.RowGroups[0].Columns[0].MetaData!;
        Assert.NotNull(meta.DictionaryPageOffset);
        Assert.True(meta.DataPageOffset > meta.DictionaryPageOffset);

        PageHeader At(long offset) {
            ms.Position = offset;
            return PageHeader.Read(new ThriftCompactProtocolReader(ms));
        }
        Assert.Equal(PageType.DICTIONARY_PAGE, At(meta.DictionaryPageOffset!.Value).Type);
        Assert.Equal(PageType.DATA_PAGE, At(meta.DataPageOffset).Type);
    }
}
