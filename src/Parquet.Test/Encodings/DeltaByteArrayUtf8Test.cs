using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using PS = ParquetSharp;
using PSchema = ParquetSharp.Schema;

namespace Parquet.Test.Encodings;

/// <summary>
/// DELTA_BYTE_ARRAY prefix lengths count bytes. Strings were rebuilt from prefixes counted in chars and suffixes
/// decoded on their own, so a value sharing a prefix that holds non-ASCII text, or one ending inside a multi-byte
/// character, came back wrong.
/// </summary>
public class DeltaByteArrayUtf8Test {

    [Fact]
    public async Task Shared_prefixes_of_non_ascii_text() {
        string[] values = { "café crème", "café noir", "日本語の本", "日本語の辞書", "😀a", "😀b", "€", "€uro", "" };
        string path = Path.GetTempFileName();
        using(var node = new PSchema.PrimitiveNode("s", PS.Repetition.Required, PS.LogicalType.String(), PS.PhysicalType.ByteArray))
        using(var root = new PSchema.GroupNode("schema", PS.Repetition.Required, new PSchema.Node[] { node }))
        using(PS.WriterProperties properties = new PS.WriterPropertiesBuilder().DisableDictionary("s").Encoding("s", PS.Encoding.DeltaByteArray).Build())
        using(var file = new PS.ParquetFileWriter(path, root, properties)) {
            using(PS.RowGroupWriter rg = file.AppendRowGroup())
            using(var writer = (PS.ColumnWriter<PS.ByteArray>)rg.NextColumn()) {
                byte[][] bytes = values.Select(Encoding.UTF8.GetBytes).ToArray();
                IntPtr block = Marshal.AllocHGlobal(Math.Max(bytes.Sum(b => b.Length), 1));
                try {
                    int offset = 0;
                    var arrays = new PS.ByteArray[bytes.Length];
                    for(int i = 0; i < bytes.Length; i++) {
                        Marshal.Copy(bytes[i], 0, block + offset, bytes[i].Length);
                        arrays[i] = new PS.ByteArray(block + offset, bytes[i].Length);
                        offset += bytes[i].Length;
                    }
                    writer.WriteBatch(arrays);
                } finally {
                    Marshal.FreeHGlobal(block);
                }
            }
            file.Close();
        }

        await using ParquetReader reader = await ParquetReader.CreateAsync(path);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var read = new ReadOnlyMemory<char>[values.Length];
        await rowGroup.ReadRawAsync<ReadOnlyMemory<char>>(reader.Schema.GetDataFields()[0], read, null, null);
        Assert.Equal(values, read.Select(r => r.ToString()));
    }
}
