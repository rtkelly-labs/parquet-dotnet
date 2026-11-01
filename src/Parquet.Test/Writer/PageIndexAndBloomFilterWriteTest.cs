using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Encryption;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;
using Encoding = System.Text.Encoding;

namespace Parquet.Test.Writer;

/// <summary>Page indexes and bloom filters the writer writes (ParquetOptions.WritePageIndex, BloomFilters).</summary>
public class PageIndexAndBloomFilterWriteTest {

    const int Rows = 1_000;

    static readonly DataField<int> Id = new("id");
    static readonly DataField<long?> Big = new("big");
    static readonly DataField<double> Ratio = new("ratio");
    static readonly DataField<string> Key = new("key");
    static readonly DateTimeDataField When = new("when", DateTimeFormat.Timestamp, unit: DateTimeTimeUnit.Micros);
    static readonly DecimalDataField Amount = new("amount", 18, 2);
    static readonly DataField<bool> Flag = new("flag");

    static readonly int[] Ids = Enumerable.Range(0, Rows).ToArray();
    static readonly long?[] Bigs = Enumerable.Range(0, Rows).Select(i => (i / 100) % 3 == 0 ? null : (long?)(i * 1_000_003L)).ToArray();   // whole pages of nulls
    static readonly double[] Ratios = Enumerable.Range(0, Rows).Select(i => i == 0 ? 0.0 : Math.Sin(i) * 1e6).ToArray();
    static readonly string?[] Keys = Enumerable.Range(0, Rows).Select(i => $"key-{i % 250:D4}").ToArray();
    static readonly DateTime[] Whens = Enumerable.Range(0, Rows).Select(i => new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i * 17)).ToArray();
    static readonly decimal[] Amounts = Enumerable.Range(0, Rows).Select(i => (i - 500) * 1.25m).ToArray();
    static readonly bool[] Flags = Enumerable.Range(0, Rows).Select(i => i % 3 == 0).ToArray();

    static ParquetOptions Options(ParquetEncryptionOptions? encryption = null) {
        var options = new ParquetOptions { WritePageIndex = true, MaxDataPageValues = 100, Encryption = encryption };
        options.ColumnEncodingHints["key"] = EncodingHint.Dictionary;
        foreach(string column in new[] { "id", "big", "ratio", "key", "when", "amount" })
            options.BloomFilters[column] = 0.01;
        return options;
    }

    static async Task<byte[]> WriteAsync(ParquetOptions options, int rowGroups = 2) {
        var schema = new ParquetSchema(Id, Big, Ratio, Key, When, Amount, Flag);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms, options)) {
            for(int g = 0; g < rowGroups; g++) {
                using ParquetRowGroupWriter rg = writer.CreateRowGroup();
                await rg.WriteAsync<int>(Id, Ids);
                await rg.WriteAsync<long>(Big, Bigs);
                await rg.WriteAsync<double>(Ratio, Ratios);
                await rg.WriteAsync(Key, Keys);
                await rg.WriteAsync<DateTime>(When, Whens);
                await rg.WriteAsync<decimal>(Amount, Amounts);
                await rg.WriteAsync<bool>(Flag, Flags);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task Offset_index_locates_every_page() {
        byte[] file = await WriteAsync(Options());
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        for(int g = 0; g < reader.RowGroupCount; g++) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
            foreach(DataField field in reader.Schema.GetDataFields()) {
                OffsetIndex offsets = (await rowGroup.ReadOffsetIndexAsync(field))!;
                Assert.Equal(Rows / 100, offsets.PageLocations.Count);
                for(int p = 0; p < offsets.PageLocations.Count; p++) {
                    PageLocation page = offsets.PageLocations[p];
                    Assert.Equal(p * 100L, page.FirstRowIndex);
                    using var ms = new MemoryStream(file, (int)page.Offset, file.Length - (int)page.Offset);
                    PageHeader header = PageHeader.Read(new ThriftCompactProtocolReader(ms));
                    Assert.Equal(PageType.DATA_PAGE, header.Type);
                    Assert.Equal(100, header.DataPageHeader!.NumValues);
                    Assert.Equal(page.CompressedPageSize, (int)ms.Position + header.CompressedPageSize);
                }
            }
        }
    }

    [Fact]
    public async Task Column_index_bounds_and_counts_every_page() {
        byte[] file = await WriteAsync(Options());
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(1);
        DataField[] f = reader.Schema.GetDataFields();

        ColumnIndex ids = (await rowGroup.ReadColumnIndexAsync(f[0]))!;
        ColumnIndex bigs = (await rowGroup.ReadColumnIndexAsync(f[1]))!;
        ColumnIndex ratios = (await rowGroup.ReadColumnIndexAsync(f[2]))!;
        ColumnIndex keys = (await rowGroup.ReadColumnIndexAsync(f[3]))!;
        ColumnIndex amounts = (await rowGroup.ReadColumnIndexAsync(f[5]))!;
        for(int p = 0; p < Rows / 100; p++) {
            IEnumerable<int> rows = Enumerable.Range(p * 100, 100);
            Assert.Equal((p * 100, p * 100 + 99), (BitConverter.ToInt32(ids.MinValues[p]), BitConverter.ToInt32(ids.MaxValues[p])));

            long?[] big = rows.Select(i => Bigs[i]).ToArray();
            Assert.Equal(big.Count(b => b == null), bigs.NullCounts![p]);
            Assert.Equal(big.All(b => b == null), bigs.NullPages[p]);
            if(!bigs.NullPages[p])
                Assert.Equal((big.Min(), big.Max()), (BitConverter.ToInt64(bigs.MinValues[p]), BitConverter.ToInt64(bigs.MaxValues[p])));
            else
                Assert.Empty(bigs.MinValues[p]);

            double[] ratio = rows.Select(i => Ratios[i]).ToArray();
            Assert.Equal(ratio.Min(), BitConverter.ToDouble(ratios.MinValues[p]));
            Assert.Equal(ratio.Max(), BitConverter.ToDouble(ratios.MaxValues[p]));

            string[] key = rows.Select(i => Keys[i]!).ToArray();
            Assert.Equal(key.Min(StringComparer.Ordinal), Encoding.UTF8.GetString(keys.MinValues[p]));
            Assert.Equal(key.Max(StringComparer.Ordinal), Encoding.UTF8.GetString(keys.MaxValues[p]));

            decimal[] amount = rows.Select(i => Amounts[i]).ToArray();
            Assert.Equal((long)(amount.Min() * 100), BitConverter.ToInt64(amounts.MinValues[p]));
            Assert.Equal((long)(amount.Max() * 100), BitConverter.ToInt64(amounts.MaxValues[p]));
        }
    }

    [Fact]
    public async Task Bloom_filters_hold_every_value_and_few_others() {
        byte[] file = await WriteAsync(Options());
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        BloomFilter id = (await rowGroup.ReadBloomFilterAsync(f[0]))!, big = (await rowGroup.ReadBloomFilterAsync(f[1]))!,
            ratio = (await rowGroup.ReadBloomFilterAsync(f[2]))!, key = (await rowGroup.ReadBloomFilterAsync(f[3]))!,
            when = (await rowGroup.ReadBloomFilterAsync(f[4]))!, amount = (await rowGroup.ReadBloomFilterAsync(f[5]))!;
        Assert.Null(await rowGroup.ReadBloomFilterAsync(f[6]));   // not asked for

        Assert.All(Ids, v => Assert.True(id.MightContain(v)));
        Assert.All(Bigs.Where(v => v != null), v => Assert.True(big.MightContain(v!.Value)));
        Assert.All(Ratios, v => Assert.True(ratio.MightContain(v)));
        Assert.All(Keys, v => Assert.True(key.MightContain(v!)));
        // a TIMESTAMP(MICROS) is hashed as its physical INT64 microseconds, a decimal(18,2) as its unscaled INT64
        Assert.All(Whens, v => Assert.True(when.MightContain((v - DateTime.UnixEpoch).Ticks / 10)));
        Assert.All(Amounts, v => Assert.True(amount.MightContain((long)(v * 100))));

        int falsePositives = Enumerable.Range(0, 10_000).Count(i => id.MightContain(Rows + i))
            + Enumerable.Range(0, 10_000).Count(i => key.MightContain($"absent-{i}"));
        Assert.InRange(falsePositives, 0, 600);   // 1% asked for, of 20,000; a little slack for rounding the size
    }

    [Fact]
    public async Task Bloom_filter_on_a_boolean_column_is_refused() {
        var options = new ParquetOptions();
        options.BloomFilters["flag"] = 0.01;
        await Assert.ThrowsAsync<NotSupportedException>(() => WriteAsync(options, 1));
    }

    [Fact]
    public async Task Nothing_is_written_unless_asked() {
        byte[] file = await WriteAsync(new ParquetOptions { MaxDataPageValues = 100 });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        Assert.All(reader.Metadata!.RowGroups.SelectMany(g => g.Columns), c => {
            Assert.Null(c.ColumnIndexOffset);
            Assert.Null(c.OffsetIndexOffset);
            Assert.Null(c.MetaData!.BloomFilterOffset);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encrypted_indexes_and_filters_round_trip(bool plaintextFooter) {
        byte[] footerKey = Encoding.ASCII.GetBytes("0123456789012345");
        byte[] keyKey = Encoding.ASCII.GetBytes("1234567890123450");
        byte[] file = await WriteAsync(Options(new ParquetEncryptionOptions {
            FooterKey = footerKey, PlaintextFooter = plaintextFooter,
            ColumnKeys = new Dictionary<string, ParquetColumnKey> { ["key"] = new ParquetColumnKey(keyKey) },
        }), 1);
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), new ParquetOptions {
            Decryption = new ParquetDecryptionOptions { FooterKey = footerKey, ColumnKeys = new Dictionary<string, byte[]> { ["key"] = keyKey } },
        });
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        BloomFilter key = (await rowGroup.ReadBloomFilterAsync(f[3]))!, id = (await rowGroup.ReadBloomFilterAsync(f[0]))!;
        Assert.All(Keys, v => Assert.True(key.MightContain(v!)));
        Assert.All(Ids, v => Assert.True(id.MightContain(v)));
        ColumnIndex keys = (await rowGroup.ReadColumnIndexAsync(f[3]))!;
        Assert.Equal("key-0000", Encoding.UTF8.GetString(keys.MinValues[0]));
        Assert.Equal(Rows / 100, (await rowGroup.ReadOffsetIndexAsync(f[3]))!.PageLocations.Count);
    }
}
