using Parquet;
using Parquet.Schema;

namespace Lake.Parquet.Benchmarks;

/// <summary>Columns of a million rows, written once per run into memory, as the benchmarks' inputs.</summary>
public static class Data {

    public const int Rows = 1_000_000;

    public static long[] Longs() {
        var r = new Random(1);
        return Enumerable.Range(0, Rows).Select(_ => r.NextInt64(-1_000_000_000, 1_000_000_000)).ToArray();
    }

    public static double?[] Doubles(double nullShare) {
        var r = new Random(2);
        return Enumerable.Range(0, Rows).Select(_ => r.NextDouble() < nullShare ? (double?)null : r.NextDouble() * 1000).ToArray();
    }

    public static DateTime?[] Timestamps() {
        var r = new Random(3);
        var epoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, Rows).Select(_ => r.Next(10) == 0 ? (DateTime?)null : epoch.AddTicks(r.NextInt64(0, 50_000_000_000_000) / 10 * 10)).ToArray();
    }

    public static decimal?[] Decimals() {
        var r = new Random(4);
        return Enumerable.Range(0, Rows).Select(_ => r.Next(10) == 0 ? (decimal?)null : r.Next(-10_000_000, 10_000_000) / 100m).ToArray();
    }

    /// <summary>Strings of 8 to 40 ASCII and some non-ASCII characters; <paramref name="distinct"/> values in all.</summary>
    public static string?[] Strings(int distinct) {
        var r = new Random(5);
        string[] pool = Enumerable.Range(0, distinct).Select(i => {
            int length = r.Next(8, 41);
            var chars = new char[length];
            for(int c = 0; c < length; c++)
                chars[c] = r.Next(20) == 0 ? (char)r.Next(0xC0, 0x24F) : (char)r.Next('a', 'z' + 1);
            return new string(chars);
        }).ToArray();
        return Enumerable.Range(0, Rows).Select(_ => r.Next(10) == 0 ? null : pool[r.Next(distinct)]).ToArray();
    }

    public static async Task<byte[]> WriteAsync(DataField field, Func<ParquetRowGroupWriter, Task> write, ParquetOptions? options = null) {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await write(rg);
        }
        return ms.ToArray();
    }
}
