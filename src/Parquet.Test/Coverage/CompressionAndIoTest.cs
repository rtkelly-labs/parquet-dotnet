using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Parquet.File;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Coverage;

/// <summary>Codecs at every compression level, codecs the library refuses, and the writer's and reader's refusals.</summary>
public class CompressionAndIoTest {

    static readonly DataField<int> Id = new("id");
    static readonly int[] Values = Enumerable.Range(0, 2_000).Select(i => i % 97).ToArray();

    static async Task<byte[]> WriteAsync(ParquetOptions options) {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(Id), ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<int>(Id, Values);
        }
        return ms.ToArray();
    }

    static async Task<int[]> ReadAsync(byte[] file) {
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        int[] read = new int[Values.Length];
        await rg.ReadAsync<int>(reader.Schema.DataFields[0], read);
        return read;
    }

    [Theory]
    [InlineData(CompressionMethod.Zstd, CompressionLevel.Fastest)]
    [InlineData(CompressionMethod.Zstd, CompressionLevel.NoCompression)]
    [InlineData(CompressionMethod.Zstd, CompressionLevel.SmallestSize)]
    [InlineData(CompressionMethod.LZ4, CompressionLevel.Fastest)]
    [InlineData(CompressionMethod.LZ4, CompressionLevel.NoCompression)]
    [InlineData(CompressionMethod.LZ4, CompressionLevel.SmallestSize)]
    [InlineData(CompressionMethod.Lz4Raw, CompressionLevel.SmallestSize)]
    public async Task Every_level_round_trips(CompressionMethod method, CompressionLevel level) {
        byte[] file = await WriteAsync(new ParquetOptions { CompressionMethod = method, CompressionLevel = level });
        Assert.Equal(Values, await ReadAsync(file));
    }

    [Fact]
    public async Task Lzo_is_not_implemented_and_unknown_codecs_are_refused() {
        await Assert.ThrowsAsync<NotImplementedException>(() => WriteAsync(new ParquetOptions { CompressionMethod = CompressionMethod.Lzo }));
        await Assert.ThrowsAsync<NotImplementedException>(async () =>
            await Compressor.Instance.Decompress(CompressionMethod.Lzo, new MemoryStream(new byte[4]), 4));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Compressor.Instance.CompressAsync((CompressionMethod)99, CompressionLevel.Optimal, new MemoryStream(new byte[4])));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await Compressor.Instance.Decompress((CompressionMethod)99, new MemoryStream(new byte[4]), 4));
    }

    [Fact]
    public async Task Gzip_page_shorter_than_its_header_says_is_what_it_holds() {
        byte[] payload = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var compressed = new MemoryStream();
        using(var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(payload);
        compressed.Position = 0;
        using IMemoryOwner<byte> owner = await Compressor.Instance.Decompress(CompressionMethod.Gzip, compressed, 500);
        Assert.Equal(payload, owner.Memory.ToArray());
    }

    [Fact]
    public async Task Zstd_frame_without_a_content_size_is_sized_by_the_decoder() {
        byte[] payload = Enumerable.Range(0, 5_000).Select(i => (byte)(i % 13)).ToArray();
        using var compressor = new ZstdSharp.Compressor(3);
        compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_contentSizeFlag, 0);   // as some writers frame pages
        var compressed = new MemoryStream(compressor.Wrap(payload).ToArray());
        // the decoder's size is then an upper bound, and the page is cut to what the frame holds
        using IMemoryOwner<byte> owner = await Compressor.Instance.Decompress(CompressionMethod.Zstd, compressed, 1);
        Assert.Equal(payload, owner.Memory.ToArray());
    }

    [Fact]
    public async Task Empty_zstd_page_decodes_to_nothing() {
        // a frame of no bytes reports size 0: the decoder sizes the output itself
        using var compressor = new ZstdSharp.Compressor(3);
        var compressed = new MemoryStream(compressor.Wrap(ReadOnlySpan<byte>.Empty).ToArray());
        Assert.Equal(0UL, ZstdSharp.Decompressor.GetDecompressedSize(compressed.ToArray()));
        using IMemoryOwner<byte> owner = await Compressor.Instance.Decompress(CompressionMethod.Zstd, compressed, 0);
        Assert.Equal(0, owner.Memory.Length);
    }

    sealed class LyingLength(byte[] data) : MemoryStream(data) {
        public override long Length => base.Length + 100;
    }

    [Fact]
    public async Task A_page_shorter_than_its_stream_claims_is_an_error() {
        // LZ4 reads the rest of the page into rented memory; a short read gives that memory back and throws
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await Compressor.Instance.Decompress(CompressionMethod.LZ4, new LyingLength(new byte[8]), 8));
    }

    [Fact]
    public async Task Writer_refuses_streams_it_cannot_write() {
        await Assert.ThrowsAsync<ArgumentException>(() => ParquetWriter.CreateAsync(new ParquetSchema(Id), new MemoryStream(new byte[10], writable: false)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ParquetWriter.CreateAsync(null!, new MemoryStream()));
    }

    [Fact]
    public async Task Append_needs_an_existing_unencrypted_file() {
        await Assert.ThrowsAsync<IOException>(() => ParquetWriter.CreateAsync(new ParquetSchema(Id), new MemoryStream(), append: true));
        var footerKey = System.Text.Encoding.ASCII.GetBytes("0123456789012345");
        await Assert.ThrowsAsync<NotSupportedException>(() => ParquetWriter.CreateAsync(new ParquetSchema(Id), new MemoryStream(),
            new ParquetOptions { Encryption = new Encryption.ParquetEncryptionOptions { FooterKey = footerKey } }, append: true));

        // a file with an encrypted footer, and one with a plaintext footer but encrypted columns
        foreach(bool plaintextFooter in new[] { false, true }) {
            byte[] encrypted = await WriteAsync(new ParquetOptions {
                Encryption = new Encryption.ParquetEncryptionOptions { FooterKey = footerKey, PlaintextFooter = plaintextFooter },
            });
            var stream = new MemoryStream();
            stream.Write(encrypted);
            NotSupportedException e = await Assert.ThrowsAsync<NotSupportedException>(() => ParquetWriter.CreateAsync(new ParquetSchema(Id), stream,
                new ParquetOptions { Decryption = new Encryption.ParquetDecryptionOptions { FooterKey = footerKey } }, append: true));
            Assert.Contains("encrypted", e.Message);
        }
    }

    [Fact]
    public async Task Append_on_an_unseekable_stream_is_refused() {
        byte[] file = await WriteAsync(new ParquetOptions());
        await Assert.ThrowsAsync<IOException>(() => ParquetWriter.CreateAsync(new ParquetSchema(Id), new ForwardOnly(file), append: true));
    }

    sealed class ForwardOnly(byte[] data) : MemoryStream(data) {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task Writer_footer_metadata_is_exposed() {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(Id), ms)) {
            writer.CustomMetadata = new System.Collections.Generic.Dictionary<string, string> { ["k"] = "v" };
            Assert.Equal("v", writer.CustomMetadata["k"]);
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<int>(Id, new[] { 1 });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Assert.Equal("v", reader.CustomMetadata["k"]);
    }

    [Fact]
    public async Task Schema_alone_reads_from_a_path_and_a_stream() {
        byte[] file = await WriteAsync(new ParquetOptions());
        string path = Path.Combine(Path.GetTempPath(), $"lake-schema-{Guid.NewGuid():N}.parquet");
        await System.IO.File.WriteAllBytesAsync(path, file);
        try {
            Assert.Equal("id", (await ParquetReader.ReadSchemaAsync(path)).DataFields[0].Name);
        } finally {
            System.IO.File.Delete(path);
        }
        Assert.Equal("id", (await ParquetReader.ReadSchemaAsync(new MemoryStream(file))).DataFields[0].Name);
    }

    [Fact]
    public async Task Writer_refuses_a_null_field() {
        using var ms = new MemoryStream();
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(Id), ms);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();
        await Assert.ThrowsAsync<ArgumentNullException>(() => rg.WriteAsync<int>(null!, new[] { 1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => rg.WriteAsync<long>(Id, new[] { 1L }));      // not the field's type
        await rg.WriteAsync<int>(Id, new[] { 1 });
    }

    [Fact]
    public async Task A_repeated_column_needs_repetition_levels() {
        var schema = new ParquetSchema(new ListField("l", new DataField<int>("x")));
        using var ms = new MemoryStream();
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms);
        using ParquetRowGroupWriter rg = writer.CreateRowGroup();
        ArgumentException e = await Assert.ThrowsAsync<ArgumentException>(() => rg.WriteAsync<int>(schema.DataFields[0], new[] { 1 }));
        Assert.Equal("repetitionLevels", e.ParamName);
        await rg.WriteAsync<int>(schema.DataFields[0], new int?[] { 1 }, new[] { 0 });
    }

    [Fact]
    public async Task Raw_reads_check_their_buffers() {
        var schema = new ParquetSchema(new DataField<int?>("n"), new ListField("l", new DataField<int>("x")));
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<int>(schema.DataFields[0], new int?[] { 1, null });
            await rg.WriteAsync<int>(schema.DataFields[1], new int?[] { 1, 2, 3 }, new[] { 0, 1, 0 });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader g = reader.OpenRowGroupReader(0);
        DataField n = reader.Schema.DataFields[0], l = reader.Schema.DataFields[1];

        ArgumentException e;
        e = await Assert.ThrowsAsync<ArgumentException>(async () => await g.ReadRawAsync<long>(n, new long[2], new int[2], null));
        Assert.Contains("is not compatible", e.Message);
        e = await Assert.ThrowsAsync<ArgumentException>(async () => await g.ReadRawAsync<int>(n, new int[1], new int[2], null));
        Assert.Contains("Values buffer is too small", e.Message);
        e = await Assert.ThrowsAsync<ArgumentException>(async () => await g.ReadRawAsync<int>(n, new int[2], null, null));
        Assert.Contains("Definition levels buffer is required", e.Message);
        e = await Assert.ThrowsAsync<ArgumentException>(async () => await g.ReadRawAsync<int>(l, new int[3], new int[3], null));
        Assert.Contains("Repetition levels buffer is required", e.Message);
        e = await Assert.ThrowsAsync<ArgumentException>(async () => await g.ReadRawAsync<int>(n, new int[2], new int[1], null));
        Assert.Contains("Definition levels buffer is too small", e.Message);
        Assert.Contains("numValues 2", e.Message);
        e = await Assert.ThrowsAsync<ArgumentException>(async () => await g.ReadRawAsync<int>(l, new int[3], new int[3], new int[2]));
        Assert.Contains("Repetition levels buffer is too small", e.Message);
        Assert.Contains("numValues 3", e.Message);
    }

    [Fact]
    public async Task Bloom_filter_and_index_reads_refuse_a_corrupt_or_foreign_filter() {
        var options = new ParquetOptions();
        options.BloomFilters["id"] = 0.01;
        byte[] file = await WriteAsync(options);
        // corrupt the bloom filter header's bitset length (numBytes), so the bitset runs past the file
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader g = reader.OpenRowGroupReader(0);
        Meta.ColumnMetaData meta = g.RowGroup.Columns[0].MetaData!;
        long offset = meta.BloomFilterOffset!.Value;
        byte[] corrupt = (byte[])file.Clone();
        // the header starts with field 1, numBytes (i32, zigzag varint); make it negative
        Assert.Equal(0x15, corrupt[offset]);
        corrupt[offset + 1] = 0x01;          // zigzag 1 = -1
        await using ParquetReader bad = await ParquetReader.CreateAsync(new MemoryStream(corrupt));
        using ParquetRowGroupReader bg = bad.OpenRowGroupReader(0);
        await Assert.ThrowsAsync<ParquetException>(() => bg.ReadBloomFilterAsync(bad.Schema.DataFields[0]));
    }
}
