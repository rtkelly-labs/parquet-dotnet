using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Encryption;
using Parquet.File.Values.Primitives;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;
using Encoding = Parquet.Meta.Encoding;

namespace Parquet.Test.Coverage;

/// <summary>
/// The second round of lines the suite did not run (lake(0081)), through whole files: encryption failures, page index
/// and bloom filter edges, corrupt pages and footers, paged statistics, compression levels.
/// </summary>
public class Round2FilesTest {

    static readonly byte[] FooterKey = System.Text.Encoding.ASCII.GetBytes("0123456789012345");
    static readonly byte[] ColumnKey = System.Text.Encoding.ASCII.GetBytes("1234567890123450");

    static async Task<byte[]> WriteAsync(ParquetSchema schema, ParquetOptions options, Func<ParquetRowGroupWriter, DataField[], Task> write) {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await write(rg, schema.GetDataFields());
        }
        return ms.ToArray();
    }

    static Task<byte[]> IdsAsync(ParquetOptions options, int n = 10) {
        var id = new DataField<int>("id");
        return WriteAsync(new ParquetSchema(id), options, (rg, f) => rg.WriteAsync<int>(f[0], Enumerable.Range(0, n).ToArray()));
    }

    static async Task<int[]> ReadIdsAsync(byte[] file, ParquetOptions? options = null) {
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), options);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var values = new int[rowGroup.RowCount];
        await rowGroup.ReadAsync<int>(reader.Schema.GetDataFields()[0], values);
        return values;
    }

    // ---- encryption ----

    [Fact]
    public async Task A_key_of_the_wrong_length_is_refused() {
        await Assert.ThrowsAsync<ArgumentException>(() => IdsAsync(new ParquetOptions { Encryption = new ParquetEncryptionOptions { FooterKey = new byte[5] } }));
    }

    [Fact]
    public async Task An_aad_prefix_not_stored_must_be_given_and_one_stored_must_match() {
        byte[] prefix = [1, 2, 3];
        byte[] notStored = await IdsAsync(new ParquetOptions { Encryption = new ParquetEncryptionOptions { FooterKey = FooterKey, AadPrefix = prefix, StoreAadPrefix = false } });
        ParquetException missing = await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(notStored,
            new ParquetOptions { Decryption = new ParquetDecryptionOptions { FooterKey = FooterKey } }));
        Assert.Contains("AadPrefix", missing.Message);
        Assert.Equal(Enumerable.Range(0, 10), await ReadIdsAsync(notStored,
            new ParquetOptions { Decryption = new ParquetDecryptionOptions { FooterKey = FooterKey, AadPrefix = prefix } }));

        byte[] stored = await IdsAsync(new ParquetOptions { Encryption = new ParquetEncryptionOptions { FooterKey = FooterKey, AadPrefix = prefix } });
        await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(stored,
            new ParquetOptions { Decryption = new ParquetDecryptionOptions { FooterKey = FooterKey, AadPrefix = [9] } }));
    }

    [Fact]
    public async Task An_encrypted_footer_needs_its_key_and_the_right_one() {
        byte[] file = await IdsAsync(new ParquetOptions { Encryption = new ParquetEncryptionOptions { FooterKey = FooterKey } });
        ParquetException noKey = await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(file));
        Assert.Contains("footer is encrypted", noKey.Message);
        await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(file,
            new ParquetOptions { Decryption = new ParquetDecryptionOptions { FooterKey = ColumnKey } }));
    }

    [Fact]
    public async Task A_plaintext_footer_signature_is_checked() {
        byte[] file = await IdsAsync(new ParquetOptions { Encryption = new ParquetEncryptionOptions { FooterKey = FooterKey, PlaintextFooter = true } });
        ParquetException wrong = await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(file,
            new ParquetOptions { Decryption = new ParquetDecryptionOptions { FooterKey = ColumnKey } }));
        Assert.Contains("signature", wrong.Message);

        // the same footer with its nonce and tag cut off
        int footerLength = BitConverter.ToInt32(file, file.Length - 8);
        int cut = footerLength - 28;
        byte[] unsigned = [.. file.AsSpan(0, file.Length - 8 - 28), .. BitConverter.GetBytes(cut), .. "PAR1"u8];
        ParquetException none = await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(unsigned,
            new ParquetOptions { Decryption = new ParquetDecryptionOptions { FooterKey = FooterKey } }));
        Assert.Contains("no signature", none.Message);
    }

    [Fact]
    public async Task A_column_key_comes_from_the_key_retriever_and_a_column_without_one_is_hidden() {
        var id = new DataField<int?>("id");
        var secret = new DataField<int?>("secret");
        byte[] file = await WriteAsync(new ParquetSchema(id, secret), new ParquetOptions {
            Encryption = new ParquetEncryptionOptions {
                FooterKey = FooterKey,
                ColumnKeys = new Dictionary<string, ParquetColumnKey> { ["secret"] = new ParquetColumnKey(ColumnKey, [7]) },
            },
        }, async (rg, f) => {
            await rg.WriteAsync<int>(f[0], new int?[] { 1, 2 });
            await rg.WriteAsync<int>(f[1], new int?[] { 3, null });
        });

        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), new ParquetOptions {
            Decryption = new ParquetDecryptionOptions { FooterKey = FooterKey, KeyRetriever = metadata => metadata is [7] ? ColumnKey : null },
        })) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            var values = new int?[2];
            await rowGroup.ReadAsync<int>(reader.Schema.GetDataFields()[1], values);
            Assert.Equal(new int?[] { 3, null }, values);
        }

        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), new ParquetOptions {
            Decryption = new ParquetDecryptionOptions { FooterKey = FooterKey },
        })) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            var ids = new int?[2];
            await rowGroup.ReadAsync<int>(reader.Schema.GetDataFields()[0], ids);
            Assert.Equal(new int?[] { 1, 2 }, ids);
            ParquetException hidden = await Assert.ThrowsAsync<ParquetException>(async () =>
                await rowGroup.ReadAsync<int>(reader.Schema.GetDataFields()[1], new int?[2]));
            Assert.Contains("encrypted", hidden.Message);
        }
    }

    [Fact]
    public async Task An_encrypted_module_with_a_bad_length_is_refused() {
        var options = new ParquetOptions { Encryption = new ParquetEncryptionOptions { FooterKey = FooterKey } };
        options.BloomFilters["id"] = 0.01;
        byte[] file = await IdsAsync(options);
        var read = new ParquetOptions { Decryption = new ParquetDecryptionOptions { FooterKey = FooterKey } };
        long offset;
        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), read))
            offset = reader.Metadata!.RowGroups[0].Columns[0].MetaData!.BloomFilterOffset!.Value;
        BitConverter.GetBytes(int.MaxValue - 1).CopyTo(file, (int)offset);
        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), read)) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            ParquetException e = await Assert.ThrowsAsync<ParquetException>(() => rowGroup.ReadBloomFilterAsync(reader.Schema.GetDataFields()[0]));
            Assert.Contains("length", e.Message);
        }
    }

    [Fact]
    public void Aes_module_guards() {
        Assert.Throws<NotSupportedException>(() => AesModules.Aad([1], ModuleType.DataPage, 40_000));
        Assert.Throws<ParquetException>(() => AesModules.CheckedLength(new byte[2], 12));
        Assert.Throws<ParquetException>(() => AesModules.CheckedLength(BitConverter.GetBytes(1000), 12));
    }

    [Fact]
    public void An_unknown_encryption_algorithm_is_refused() {
        using var ms = new MemoryStream();
        new FileCryptoMetaData { EncryptionAlgorithm = new EncryptionAlgorithm() }.Write(new ThriftCompactProtocolWriter(ms));
        Assert.Throws<NotSupportedException>(() => FileDecryptor.ReadEncryptedFooter(ms.ToArray(), new ParquetDecryptionOptions { FooterKey = FooterKey }));
    }

    // ---- page index and bloom filter edges ----

    [Fact]
    public async Task Columns_without_an_order_or_with_bounds_missing_get_no_column_index() {
        var interval = new DataField<Interval>("i");
        var nan = new DataField<double>("d");
        var options = new ParquetOptions { WritePageIndex = true, MaxDataPageValues = 2 };
        byte[] file = await WriteAsync(new ParquetSchema(interval, nan), options, async (rg, f) => {
            await rg.WriteAsync<Interval>(f[0], new[] { new Interval(1, 2, 3), new Interval(4, 5, 6), new Interval(0, 0, 1), new Interval(0, 0, 2) });
            await rg.WriteAsync<double>(f[1], new[] { double.NaN, double.NaN, 1.0, 2.0 });
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        foreach(DataField f in reader.Schema.GetDataFields()) {
            Assert.Null(await rowGroup.ReadColumnIndexAsync(f));                     // no order; a page of NaNs only
            Assert.Equal(2, (await rowGroup.ReadOffsetIndexAsync(f))!.PageLocations.Count);
        }
    }

    [Fact]
    public async Task An_INT96_timestamp_bloom_filter_holds_its_twelve_byte_values() {
        var when = new DataField<DateTime>("when");
        var options = new ParquetOptions();
        options.BloomFilters["when"] = 0.01;
        DateTime[] values = [new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 6, 30, 12, 0, 0, DateTimeKind.Utc)];
        byte[] file = await WriteAsync(new ParquetSchema(when), options, (rg, f) => rg.WriteAsync<DateTime>(f[0], values));
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        BloomFilter bloom = (await rowGroup.ReadBloomFilterAsync(reader.Schema.GetDataFields()[0]))!;
        Assert.All(values, v => Assert.True(bloom.MightContain(new NanoTime(v).GetBytes())));
    }

    /// <summary>The file with <paramref name="insert"/> placed before its footer, and the footer changed.</summary>
    static async Task<byte[]> RewriteFooterAsync(byte[] file, byte[] insert, Action<FileMetaData, long> change) {
        FileMetaData meta;
        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file)))
            meta = reader.Metadata!;
        int footerLength = BitConverter.ToInt32(file, file.Length - 8);
        int dataEnd = file.Length - 8 - footerLength;
        change(meta, dataEnd);
        using var footer = new MemoryStream();
        meta.Write(new ThriftCompactProtocolWriter(footer));
        return [.. file.AsSpan(0, dataEnd), .. insert, .. footer.ToArray(), .. BitConverter.GetBytes((int)footer.Length), .. "PAR1"u8];
    }

    static byte[] BloomBytes(BloomFilterHeader header, int bitsetLength) {
        using var ms = new MemoryStream();
        header.Write(new ThriftCompactProtocolWriter(ms));
        ms.Write(new byte[bitsetLength]);
        return ms.ToArray();
    }

    static BloomFilterHeader Header(int numBytes, bool block = true) => new() {
        NumBytes = numBytes,
        Algorithm = new BloomFilterAlgorithm { BLOCK = block ? new SplitBlockAlgorithm() : null },
        Hash = new BloomFilterHash { XXHASH = new XxHash() },
        Compression = new BloomFilterCompression { UNCOMPRESSED = new Uncompressed() },
    };

    [Theory]
    [InlineData(false, 32)]   // not a split block filter
    [InlineData(true, 33)]    // not a whole number of blocks
    public async Task Bloom_filters_other_than_split_block_or_of_odd_sizes_are_refused(bool block, int numBytes) {
        byte[] bloom = BloomBytes(Header(numBytes, block), numBytes);
        byte[] file = await RewriteFooterAsync(await IdsAsync(new ParquetOptions()), bloom, (meta, at) => {
            meta.RowGroups[0].Columns[0].MetaData!.BloomFilterOffset = at;
            meta.RowGroups[0].Columns[0].MetaData!.BloomFilterLength = bloom.Length;
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        if(block)
            await Assert.ThrowsAsync<ParquetException>(() => rowGroup.ReadBloomFilterAsync(reader.Schema.GetDataFields()[0]));
        else
            await Assert.ThrowsAsync<NotSupportedException>(() => rowGroup.ReadBloomFilterAsync(reader.Schema.GetDataFields()[0]));
    }

    [Fact]
    public async Task A_page_index_outside_the_file_is_refused() {
        byte[] file = await RewriteFooterAsync(await IdsAsync(new ParquetOptions()), [], (meta, _) => {
            meta.RowGroups[0].Columns[0].ColumnIndexOffset = 1_000_000;
            meta.RowGroups[0].Columns[0].ColumnIndexLength = 10;
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        ParquetException e = await Assert.ThrowsAsync<ParquetException>(() => rowGroup.ReadColumnIndexAsync(reader.Schema.GetDataFields()[0]));
        Assert.Contains("outside the file", e.Message);
    }

    // ---- corrupt pages ----

    /// <summary>The file with its first page header (at the data page offset, or the dictionary's) changed.</summary>
    static async Task<byte[]> RewritePageAsync(byte[] file, bool dictionary, Action<PageHeader> change) {
        long offset;
        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file))) {
            ColumnMetaData cmd = reader.Metadata!.RowGroups[0].Columns[0].MetaData!;
            offset = dictionary ? cmd.DataPageOffset : cmd.DictionaryPageOffset ?? cmd.DataPageOffset;
            if(!dictionary && cmd.DictionaryPageOffset is long dict && dict < cmd.DataPageOffset)
                offset = dict;
            if(dictionary)
                offset = cmd.DataPageOffset;
        }
        using var ms = new MemoryStream(file, (int)offset, file.Length - (int)offset);
        PageHeader ph = PageHeader.Read(new ThriftCompactProtocolReader(ms));
        int headerLength = (int)ms.Position;
        change(ph);
        using var header = new MemoryStream();
        ph.Write(new ThriftCompactProtocolWriter(header));
        return [.. file.AsSpan(0, (int)offset), .. header.ToArray(), .. file.AsSpan((int)offset + headerLength)];
    }

    static Task<byte[]> PlainIdsAsync() => IdsAsync(new ParquetOptions { CompressionMethod = CompressionMethod.None });

    [Fact]
    public async Task A_page_of_an_unknown_type_is_refused() {
        byte[] file = await RewritePageAsync(await PlainIdsAsync(), false, ph => ph.Type = PageType.INDEX_PAGE);
        await Assert.ThrowsAsync<NotSupportedException>(() => ReadIdsAsync(file));
    }

    [Fact]
    public async Task A_data_page_without_its_header_is_refused() {
        byte[] v1 = await RewritePageAsync(await PlainIdsAsync(), false, ph => ph.DataPageHeader = null);
        await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(v1));
        byte[] v2 = await RewritePageAsync(await PlainIdsAsync(), false, ph => { ph.Type = PageType.DATA_PAGE_V2; ph.DataPageHeader = null; });
        await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(v2));
    }

    [Fact]
    public async Task A_page_in_an_unknown_encoding_is_refused() {
        byte[] file = await RewritePageAsync(await PlainIdsAsync(), false, ph => ph.DataPageHeader!.Encoding = (Encoding)99);
        await Assert.ThrowsAsync<ParquetException>(() => ReadIdsAsync(file));
    }

    [Fact]
    public async Task A_second_dictionary_page_is_refused() {
        var key = new DataField<string>("key");
        var options = new ParquetOptions { CompressionMethod = CompressionMethod.None };
        options.ColumnEncodingHints["key"] = EncodingHint.Dictionary;
        byte[] file = await WriteAsync(new ParquetSchema(key), options, (rg, f) => rg.WriteAsync(f[0], new[] { "a", "b", "a", "b" }));
        // the data page after the dictionary page claims to be a dictionary too
        byte[] twice = await RewritePageAsync(file, true, ph => {
            ph.Type = PageType.DICTIONARY_PAGE;
            ph.DictionaryPageHeader = new DictionaryPageHeader { NumValues = 2, Encoding = Encoding.PLAIN };
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(twice));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rowGroup.ReadAsync(reader.Schema.GetDataFields()[0], new Memory<string?>(new string?[4])));
    }

    // ---- statistics of paged chunks, and of long values ----

    [Fact]
    public async Task Paged_chunks_of_unsigned_binary_decimal_and_interval_columns_round_trip() {
        var u32 = new DataField<uint>("u32");
        var u64 = new DataField<ulong>("u64");
        var bin = new DataField<byte[]>("bin");
        var big = new BigDecimalDataField("big", 38, 2);
        var interval = new DataField<Interval>("i");
        uint[] u32s = [5, 3, uint.MaxValue, 1];
        ulong[] u64s = [5, 3, ulong.MaxValue, 1];
        byte[][] bins = [[3], [1], [2], [0]];
        BigDecimal[] bigs = [new(100, 38, 2), new(-5, 38, 2), new(7, 38, 2), new(0, 38, 2)];
        Interval[] intervals = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(2, 2, 2)];
        byte[] file = await WriteAsync(new ParquetSchema(u32, u64, bin, big, interval), new ParquetOptions { MaxDataPageValues = 2 }, async (rg, f) => {
            await rg.WriteAsync<uint>(f[0], u32s);
            await rg.WriteAsync<ulong>(f[1], u64s);
            await rg.WriteAsync(f[2], bins);
            await rg.WriteAsync<BigDecimal>(f[3], bigs);
            await rg.WriteAsync<Interval>(f[4], intervals);
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), new ParquetOptions { UseBigDecimal = true });
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] r = reader.Schema.GetDataFields();
        var a = new uint[4]; await rowGroup.ReadAsync<uint>(r[0], a); Assert.Equal(u32s, a);
        var b = new ulong[4]; await rowGroup.ReadAsync<ulong>(r[1], b); Assert.Equal(u64s, b);
        var c = new byte[]?[4]; await rowGroup.ReadAsync(r[2], new Memory<byte[]?>(c)); Assert.Equal(bins, c);
        var d = new BigDecimal[4]; await rowGroup.ReadAsync<BigDecimal>(r[3], d); Assert.Equal(bigs.Select(x => x.UnscaledValue), d.Select(x => x.UnscaledValue));
        var e = new Interval[4]; await rowGroup.ReadAsync<Interval>(r[4], e); Assert.Equal(intervals, e);
    }

    [Fact]
    public async Task Unsigned_64_bit_statistics_compare_unsigned() {
        var u64 = new DataField<ulong>("u64");
        byte[] file = await WriteAsync(new ParquetSchema(u64), new ParquetOptions(), (rg, f) => rg.WriteAsync<ulong>(f[0], new ulong[] { 9, 3, ulong.MaxValue, 5 }));
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataColumnStatistics stats = rowGroup.GetStatistics(reader.Schema.GetDataFields()[0])!;
        Assert.Equal((3UL, ulong.MaxValue), ((ulong)stats.MinValue!, (ulong)stats.MaxValue!));
    }

    [Fact]
    public async Task Values_longer_than_the_bound_limit_get_no_bounds_and_long_strings_round_trip() {
        var bin = new DataField<byte[]>("bin");
        var text = new DataField<string>("text");
        byte[] longBytes = new byte[5000];
        longBytes[0] = 9;
        string longText = new string('x', 300_000);
        byte[] file = await WriteAsync(new ParquetSchema(bin, text), new ParquetOptions(), async (rg, f) => {
            await rg.WriteAsync(f[0], new[] { longBytes, new byte[] { 1 } });
            await rg.WriteAsync(f[1], new[] { longText, "short" });
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] r = reader.Schema.GetDataFields();
        DataColumnStatistics stats = rowGroup.GetStatistics(r[0])!;
        Assert.Equal((null, null), (stats.MinValue, stats.MaxValue));
        var s = new string?[2];
        await rowGroup.ReadAsync(r[1], new Memory<string?>(s));
        Assert.Equal(new[] { longText, "short" }, s);
    }

    [Fact]
    public async Task Unsigned_64_bit_delta_encoding_round_trips() {
        var u64 = new DataField<ulong>("u64");
        var options = new ParquetOptions();
        options.ColumnEncodingHints["u64"] = EncodingHint.DeltaBinaryPacked;
        ulong[] values = Enumerable.Range(0, 300).Select(i => i % 7 == 0 ? ulong.MaxValue - (ulong)i : (ulong)i * 3).ToArray();
        byte[] file = await WriteAsync(new ParquetSchema(u64), options, (rg, f) => rg.WriteAsync<ulong>(f[0], values));
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var back = new ulong[values.Length];
        await rowGroup.ReadAsync<ulong>(reader.Schema.GetDataFields()[0], back);
        Assert.Equal(values, back);
    }

    // ---- compression levels outside the enum ----

    [Theory]
    [InlineData(CompressionMethod.Zstd)]
    [InlineData(CompressionMethod.LZ4)]
    public async Task An_unknown_compression_level_uses_the_codecs_default(CompressionMethod codec) {
        byte[] file = await IdsAsync(new ParquetOptions { CompressionMethod = codec, CompressionLevel = (CompressionLevel)42 }, 1000);
        Assert.Equal(Enumerable.Range(0, 1000), await ReadIdsAsync(file));
    }
}
