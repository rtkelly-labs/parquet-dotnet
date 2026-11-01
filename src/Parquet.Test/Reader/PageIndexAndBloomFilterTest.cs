using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Encoding = System.Text.Encoding;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Encryption;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Reader;

/// <summary>
/// Page indexes (column and offset indexes) and bloom filters as other implementations write them: parquet-mr,
/// parquet-rs, parquet-cpp (pyarrow), DuckDB and Polars (data/indexes, see its README). Each structure is checked
/// against the data it describes.
/// </summary>
public class PageIndexAndBloomFilterTest : TestBase {

    public static IEnumerable<object[]> Files() => new[] {
        "alltypes_tiny_pages.parquet", "data_index_bloom_encoding_stats.parquet", "data_index_bloom_encoding_with_length.parquet",
        "int32_with_null_pages.parquet", "binary_truncated_min_max.parquet", "floating_orders_nan_count.parquet",
        "duckdb_bloom.parquet", "polars_page_index.parquet", "pyarrow_index_bloom.parquet",
    }.Select(f => new object[] { f });

    // reference values: the xxhash package (Python), over bytes (i * 31 + 7) mod 256
    [Theory]
    [InlineData(0, 0xEF46DB3751D8E999UL)]
    [InlineData(1, 0xA96C7F0CE858BBB7UL)]
    [InlineData(3, 0x56E6957632A487F9UL)]
    [InlineData(4, 0xC60D15B1E3FF8F04UL)]
    [InlineData(7, 0xAFBEFC3D6C6F9A8EUL)]
    [InlineData(8, 0x3DA5C7AA269683E0UL)]
    [InlineData(15, 0xAE2A37EB9357CAA7UL)]
    [InlineData(16, 0xA19AD429B02BC413UL)]
    [InlineData(31, 0x4A74F3A1A39AD4A1UL)]
    [InlineData(32, 0x8D57D6A4671CC43DUL)]
    [InlineData(33, 0x62C9FD21ED857664UL)]
    [InlineData(63, 0x5C320A0D2707057FUL)]
    [InlineData(64, 0x7BBABBC45729D17EUL)]
    [InlineData(65, 0xF3980C34BAE65DC1UL)]
    [InlineData(100, 0xEFA0AD2D3E70C151UL)]
    public void XxHash64_matches_the_reference(int length, ulong expected) {
        byte[] data = Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7)).ToArray();
        Assert.Equal(expected, BloomFilter.Hash(data));
    }

    [Fact]
    public void Bloom_filter_from_parquet_mr_contains_what_was_inserted() {
        // bloom_filter.xxhash.bin: a thrift BloomFilterHeader then the bitset; "hello", "parquet", "bloom", "filter" inserted
        using Stream s = OpenTestFile("indexes/bloom_filter.xxhash.bin");
        BloomFilterHeader header = BloomFilterHeader.Read(new ThriftCompactProtocolReader(s));
        byte[] bitset = new byte[header.NumBytes];
        s.ReadExactly(bitset);
        var filter = new BloomFilter(bitset);

        foreach(string word in new[] { "hello", "parquet", "bloom", "filter" })
            Assert.True(filter.MightContain(word), word);
        Assert.InRange(Enumerable.Range(0, 1000).Count(i => filter.MightContain($"absent-{i}")), 0, 50);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Bloom_filters_contain_every_value_of_their_chunk(string file) {
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("indexes/" + file));
        int filters = 0;
        for(int g = 0; g < reader.RowGroupCount; g++) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
            foreach(DataField field in reader.Schema.GetDataFields()) {
                BloomFilter? filter = await rowGroup.ReadBloomFilterAsync(field);
                Assert.Equal(rowGroup.GetMetadata(field)!.MetaData!.BloomFilterOffset != null, filter != null);
                if(filter == null)
                    continue;
                filters++;
                List<object> values = (await ReadPhysicalAsync(rowGroup, field)).Values.Where(v => v != null).Cast<object>().ToList();
                Assert.NotEmpty(values);
                foreach(object v in values)
                    Assert.True(MightContain(filter, v), $"{file} {field.Path} row group {g}: {v} not in the filter");
                // values of the same type that are not in the chunk: false positives only, at a low rate
                int probes = 0, positives = 0;
                foreach(object absent in Absent(values)) {
                    probes++;
                    positives += MightContain(filter, absent) ? 1 : 0;
                }
                Assert.True(positives <= probes / 10, $"{file} {field.Path}: {positives} of {probes} absent values reported present");
            }
        }
        Assert.Equal(file.Contains("bloom") ? true : filters > 0, filters > 0);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Page_indexes_describe_their_pages(string file) {
        byte[] bytes;
        using(Stream s = OpenTestFile("indexes/" + file)) {
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(bytes));
        int indexed = 0;
        for(int g = 0; g < reader.RowGroupCount; g++) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
            foreach(DataField field in reader.Schema.GetDataFields()) {
                ColumnChunk chunk = rowGroup.GetMetadata(field)!;
                OffsetIndex? offsets = await rowGroup.ReadOffsetIndexAsync(field);
                ColumnIndex? index = await rowGroup.ReadColumnIndexAsync(field);
                Assert.Equal(chunk.OffsetIndexOffset != null, offsets != null);
                Assert.Equal(chunk.ColumnIndexOffset != null, index != null);
                if(offsets == null)
                    continue;
                indexed++;
                CheckOffsets(bytes, offsets, rowGroup.RowCount, $"{file} {field.Path} row group {g}");
                if(index != null && field.MaxRepetitionLevel == 0)
                    await CheckColumnIndexAsync(rowGroup, field, offsets, index, $"{file} {field.Path} row group {g}", IsFloat16(reader, field));
            }
        }
        Assert.Equal(file.StartsWith("duckdb", StringComparison.Ordinal) ? 0 : indexed, indexed);
        if(!file.StartsWith("duckdb", StringComparison.Ordinal))
            Assert.True(indexed > 0, $"{file}: no page index read");
    }

    [Fact]
    public async Task Encrypted_bloom_filters_and_page_indexes_are_decrypted() {
        // parquet-mr: encrypted footer and columns, bloom filters on double_field and float_field
        var options = new ParquetOptions {
            Decryption = new ParquetDecryptionOptions {
                FooterKey = Encoding.ASCII.GetBytes("0123456789012345"),
                ColumnKeys = new Dictionary<string, byte[]> {
                    ["double_field"] = Encoding.ASCII.GetBytes("1234567890123450"),
                    ["float_field"] = Encoding.ASCII.GetBytes("1234567890123451"),
                },
            },
        };
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("indexes/encrypt_columns_and_footer_bloom_filter.parquet.encrypted"), options);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        foreach(string name in new[] { "double_field", "float_field" }) {
            DataField field = reader.Schema.GetDataFields().Single(f => f.Name == name);
            BloomFilter filter = (await rowGroup.ReadBloomFilterAsync(field))!;
            List<object> values = (await ReadPhysicalAsync(rowGroup, field)).Values.Where(v => v != null).Cast<object>().ToList();
            Assert.NotEmpty(values);
            Assert.All(values, v => Assert.True(MightContain(filter, v), $"{name}: {v}"));
            OffsetIndex offsets = (await rowGroup.ReadOffsetIndexAsync(field))!;
            ColumnIndex index = (await rowGroup.ReadColumnIndexAsync(field))!;
            Assert.Equal(offsets.PageLocations.Count, index.NullPages.Count);
            await CheckColumnIndexAsync(rowGroup, field, offsets, index, name, false);
        }
    }

    static void CheckOffsets(byte[] file, OffsetIndex offsets, long rows, string what) {
        Assert.NotEmpty(offsets.PageLocations);
        Assert.Equal(0, offsets.PageLocations[0].FirstRowIndex);
        for(int p = 0; p < offsets.PageLocations.Count; p++) {
            PageLocation page = offsets.PageLocations[p];
            if(p > 0)
                Assert.True(page.FirstRowIndex > offsets.PageLocations[p - 1].FirstRowIndex, $"{what}: page {p} starts at row {page.FirstRowIndex}");
            Assert.InRange(page.FirstRowIndex, 0, rows - 1);
            // a data page header starts at each offset, and the page (header and data) is the size the index says
            Assert.InRange(page.Offset, 4, file.Length - 8);
            using var ms = new MemoryStream(file, (int)page.Offset, file.Length - (int)page.Offset);
            PageHeader header = PageHeader.Read(new ThriftCompactProtocolReader(ms));
            Assert.True(header.Type is PageType.DATA_PAGE or PageType.DATA_PAGE_V2, $"{what}: page {p} is {header.Type}");
            Assert.Equal(page.CompressedPageSize, (int)ms.Position + header.CompressedPageSize);
        }
    }

    /// <summary>Each page's null count, null-page flag and bounds, checked against the values in its rows.</summary>
    static bool IsFloat16(ParquetReader reader, DataField field) =>
        reader.Metadata!.Schema.Any(se => se.Name == field.Name && se.LogicalType?.FLOAT16 != null);

    static async Task CheckColumnIndexAsync(ParquetRowGroupReader rowGroup, DataField field, OffsetIndex offsets, ColumnIndex index, string what, bool float16) {
        int pages = offsets.PageLocations.Count;
        Assert.Equal(pages, index.NullPages.Count);
        Assert.Equal(pages, index.MinValues.Count);
        Assert.Equal(pages, index.MaxValues.Count);
        (object?[] values, SchemaElement se) = await ReadPhysicalAsync(rowGroup, field);
        if(float16)   // FLOAT16 is ordered as the number, not its bytes
            values = values.Select(v => v is byte[] b ? (object)(float)BitConverter.ToHalf(b) : null).ToArray();
        for(int p = 0; p < pages; p++) {
            int from = (int)offsets.PageLocations[p].FirstRowIndex;
            int to = p + 1 < pages ? (int)offsets.PageLocations[p + 1].FirstRowIndex : values.Length;
            object?[] page = values[from..to];
            int nulls = page.Count(v => v == null);
            if(index.NullCounts != null)
                Assert.True(index.NullCounts[p] == nulls, $"{what}: page {p} has {nulls} nulls, the index says {index.NullCounts[p]}");
            Assert.True(index.NullPages[p] == (nulls == page.Length), $"{what}: page {p} null page {index.NullPages[p]}, {nulls} of {page.Length} null");
            if(index.NullPages[p])
                continue;
            object?[] present = page.Where(v => v != null && !(v is double d && double.IsNaN(d)) && !(v is float f && float.IsNaN(f))).ToArray();
            if(present.Length == 0)
                continue;   // NaN only: bounds are writer-defined
            object min = present.Min(Comparer.Instance)!, max = present.Max(Comparer.Instance)!;
            object indexMin = float16 ? (float)BitConverter.ToHalf(index.MinValues[p]) : Decode(index.MinValues[p], min);
            object indexMax = float16 ? (float)BitConverter.ToHalf(index.MaxValues[p]) : Decode(index.MaxValues[p], max);
            // the format promises bounds: a writer may truncate them (binary) or widen them (Polars writes the chunk's),
            // never narrow them; a zero minimum is written as -0.0 and a zero maximum as +0.0
            Assert.True(Comparer.Numeric(indexMin, min) <= 0, $"{what}: page {p} min {Show(indexMin)} above the data's {Show(min)}");
            Assert.True(Comparer.Numeric(indexMax, max) >= 0, $"{what}: page {p} max {Show(indexMax)} below the data's {Show(max)}");
        }
        _ = se;
    }

    /// <summary>A column chunk's values in its physical type, one per row (null for a null), for flat columns.</summary>
    static async Task<(object?[] Values, SchemaElement Se)> ReadPhysicalAsync(ParquetRowGroupReader rowGroup, DataField field) {
        int n = checked((int)rowGroup.GetMetadata(field)!.MetaData!.NumValues);
        int[]? defs = field.MaxDefinitionLevel > 0 ? new int[n] : null;
        object?[] Expand<T>(T[] packed, Func<T, object> box) {
            var result = new object?[n];
            int vi = 0;
            for(int i = 0; i < n; i++)
                result[i] = defs == null || defs[i] == field.MaxDefinitionLevel ? box(packed[vi++]) : null;
            return result;
        }
        async Task<object?[]> ReadAs<T>(Func<T, object> box) where T : struct {
            var values = new T[n];
            await rowGroup.ReadRawAsync<T>(field, values, defs is null ? null : (Memory<int>?)defs, null);
            return Expand(values, box);
        }
        object?[] read = field.PhysicalClrType switch {
            System.Type t when t == typeof(int) => await ReadAs<int>(v => v),
            System.Type t when t == typeof(long) => await ReadAs<long>(v => v),
            System.Type t when t == typeof(float) => await ReadAs<float>(v => v),
            System.Type t when t == typeof(double) => await ReadAs<double>(v => v),
            System.Type t when t == typeof(bool) => await ReadAs<bool>(v => v),
            System.Type t when t == typeof(ReadOnlyMemory<byte>) => await ReadAs<ReadOnlyMemory<byte>>(v => v.ToArray()),
            _ => await ReadAs<DateTime>(v => v),   // INT96
        };
        return (read, new SchemaElement());
    }

    static bool MightContain(BloomFilter filter, object value) => value switch {
        int i => filter.MightContain(i),
        long l => filter.MightContain(l),
        float f => filter.MightContain(f),
        double d => filter.MightContain(d),
        byte[] b => filter.MightContain(b),
        _ => throw new NotSupportedException(value.GetType().Name),
    };

    /// <summary>Values of the same type that the chunk does not hold.</summary>
    static IEnumerable<object> Absent(List<object> present) {
        var set = new HashSet<object>(present.Select(v => v is byte[] b ? (object)Convert.ToBase64String(b) : v));
        for(int i = 0; i < 2000; i++) {
            object candidate = present[0] switch {
                int => (object)(int.MinValue + 7919 * i),
                long => long.MinValue + 104729L * i,
                float => -1e30f - i,
                double => -1e300 - i * 1e290,
                _ => Encoding.UTF8.GetBytes($"\u0001absent-{i}"),
            };
            if(!set.Contains(candidate is byte[] b ? Convert.ToBase64String(b) : candidate))
                yield return candidate;
        }
    }

    static object Decode(byte[] plain, object like) => like switch {
        int => BitConverter.ToInt32(plain),
        long => BitConverter.ToInt64(plain),
        float => BitConverter.ToSingle(plain),
        double => BitConverter.ToDouble(plain),
        bool => plain[0] != 0,
        DateTime => like,   // INT96: not compared
        _ => plain,
    };

    static string Show(object v) => v is byte[] b ? Convert.ToHexString(b) : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!;

    /// <summary>The physical types' orders: numbers numerically, -0.0 below +0.0, byte arrays unsigned and lexicographic.</summary>
    sealed class Comparer : IComparer<object?> {
        public static readonly Comparer Instance = new();
        /// <summary>As Compare, but -0.0 equals +0.0, as bounds compare.</summary>
        public static int Numeric(object x, object y) => (x, y) switch {
            (double a, double b) => a.CompareTo(b) == 0 || a == b ? 0 : a.CompareTo(b),
            (float a, float b) => a.CompareTo(b) == 0 || a == b ? 0 : a.CompareTo(b),
            _ => Instance.Compare(x, y),
        };
        public int Compare(object? x, object? y) => (x, y) switch {
            (byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b),
            (double a, double b) => a == b ? (BitConverter.DoubleToInt64Bits(b) < 0).CompareTo(BitConverter.DoubleToInt64Bits(a) < 0) : a.CompareTo(b),
            (float a, float b) => a == b ? (BitConverter.SingleToInt32Bits(b) < 0).CompareTo(BitConverter.SingleToInt32Bits(a) < 0) : a.CompareTo(b),
            (DateTime, DateTime) => 0,
            (IComparable a, _) => a.CompareTo(y),
            _ => 0,
        };
    }
}
