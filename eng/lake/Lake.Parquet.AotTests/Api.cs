using Parquet;
using Parquet.Data;
using Parquet.Encryption;
using Parquet.Schema;

/// <summary>
/// The calls Parquet.SourceGenerator's generated readers and writers make, each round-tripped and checked: typed
/// fields of every type, nullable reads into T?[], strings and byte arrays, decimals and timestamps, statistics,
/// every encoding hint, every codec, data pages within a chunk, and modular encryption.
/// </summary>
static class Api {

    static readonly List<string> Problems = [];
    static int checks;

    static void Check(bool ok, string what) {
        checks++;
        if(!ok) {
            Problems.Add(what);
        }
    }

    public static async Task<int> RunAsync() {
        foreach(CompressionMethod codec in Enum.GetValues<CompressionMethod>()) {
            if(codec is CompressionMethod.Lzo) {
                continue;   // not supported by the library for writing
            }
            foreach(EncodingHint hint in Enum.GetValues<EncodingHint>()) {
                foreach(int? pages in new int?[] { null, 7 }) {
                    await TypedColumnsAsync(new ParquetOptions { CompressionMethod = codec, MaxDataPageValues = pages }, hint, $"{codec}/{hint}/{pages?.ToString() ?? "one page"}");
                }
            }
        }
        await EncryptedAsync();
        await StatisticsAsync();
        await EnumsRefusedAsync();
        foreach(string problem in Problems) {
            Console.WriteLine("- " + problem);
        }
        Console.WriteLine($"api: {checks - Problems.Count} of {checks} checks passed");
        return Problems.Count == 0 ? 0 : 1;
    }

    static async Task<byte[]> WriteAsync(ParquetSchema schema, ParquetOptions options, Func<ParquetRowGroupWriter, Task> write) {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await write(rg);
        }
        return ms.ToArray();
    }

    static async Task TypedColumnsAsync(ParquetOptions options, EncodingHint hint, string where) {
        const int n = 50;
        var r = new Random(n);
        bool[] bools = Enumerable.Range(0, n).Select(i => i % 3 == 0).ToArray();
        int?[] ints = Enumerable.Range(0, n).Select(i => i % 5 == 0 ? (int?)null : r.Next()).ToArray();
        long[] longs = Enumerable.Range(0, n).Select(_ => r.NextInt64()).ToArray();
        short?[] shorts = Enumerable.Range(0, n).Select(i => i % 4 == 0 ? (short?)null : (short)r.Next(short.MinValue, short.MaxValue)).ToArray();
        uint[] uints = Enumerable.Range(0, n).Select(_ => (uint)r.NextInt64(0, uint.MaxValue)).ToArray();
        double?[] doubles = Enumerable.Range(0, n).Select(i => i % 6 == 0 ? (double?)null : r.NextDouble()).ToArray();
        float[] floats = Enumerable.Range(0, n).Select(_ => (float)r.NextDouble()).ToArray();
        decimal?[] decimals = Enumerable.Range(0, n).Select(i => i % 7 == 0 ? (decimal?)null : r.Next(-1_000_000, 1_000_000) / 100m).ToArray();
        DateTime?[] stamps = Enumerable.Range(0, n).Select(i => i % 8 == 0 ? (DateTime?)null : new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(r.NextInt64(0, 10_000_000_000_000) / 10 * 10)).ToArray();
        DateOnly[] dates = Enumerable.Range(0, n).Select(i => DateOnly.FromDayNumber(730_000 + i)).ToArray();
        Guid[] guids = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();
        string?[] strings = Enumerable.Range(0, n).Select(i => i % 9 == 0 ? null : $"value-{r.Next(10)}-é😀").ToArray();
        byte[]?[] bytes = Enumerable.Range(0, n).Select(i => i % 10 == 0 ? null : BitConverter.GetBytes(r.NextInt64())).ToArray();

        var fBool = new DataField<bool>("b");
        var fInt = new DataField<int?>("i");
        var fLong = new DataField<long>("l");
        var fShort = new DataField<short?>("s");
        var fUint = new DataField<uint>("u");
        var fDouble = new DataField<double?>("d");
        var fFloat = new DataField<float>("f");
        var fDecimal = new DecimalDataField("m", 18, 2, isNullable: true);
        var fStamp = new DateTimeDataField("t", DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Micros, isNullable: true);
        var fDate = new DataField<DateOnly>("date");
        var fGuid = new DataField<Guid>("g");
        var fString = new DataField<string>("str");
        var fBytes = new DataField<byte[]>("bin");
        var schema = new ParquetSchema(fBool, fInt, fLong, fShort, fUint, fDouble, fFloat, fDecimal, fStamp, fDate, fGuid, fString, fBytes);
        foreach(DataField field in schema.GetDataFields()) {
            options.ColumnEncodingHints[field.Path.ToString()] = hint;
        }

        byte[] file = await WriteAsync(schema, options, async rg => {
            await rg.WriteAsync<bool>(fBool, bools);
            await rg.WriteAsync<int>(fInt, ints);
            await rg.WriteAsync<long>(fLong, longs);
            await rg.WriteAsync<short>(fShort, shorts);
            await rg.WriteAsync<uint>(fUint, uints);
            await rg.WriteAsync<double>(fDouble, doubles);
            await rg.WriteAsync<float>(fFloat, floats);
            await rg.WriteAsync<decimal>(fDecimal, decimals);
            await rg.WriteAsync<DateTime>(fStamp, stamps);
            await rg.WriteAsync<DateOnly>(fDate, dates);
            await rg.WriteAsync<Guid>(fGuid, guids);
            await rg.WriteAsync(fString, strings);
            await rg.WriteAsync(fBytes, bytes);
        });

        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), new ParquetOptions { UseDateOnlyTypeForDates = true });
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        Check(rowGroup.RowCount == n, $"{where}: row count {rowGroup.RowCount}");
        Check(SequenceEqual(await ReadAsync<bool>(rowGroup, f[0], n), bools), $"{where}: bool");
        Check(SequenceEqual(await ReadNullableAsync<int>(rowGroup, f[1], n), ints), $"{where}: int?");
        Check(SequenceEqual(await ReadAsync<long>(rowGroup, f[2], n), longs), $"{where}: long");
        Check(SequenceEqual(await ReadNullableAsync<short>(rowGroup, f[3], n), shorts), $"{where}: short?");
        Check(SequenceEqual(await ReadAsync<uint>(rowGroup, f[4], n), uints), $"{where}: uint");
        Check(SequenceEqual(await ReadNullableAsync<double>(rowGroup, f[5], n), doubles), $"{where}: double?");
        Check(SequenceEqual(await ReadAsync<float>(rowGroup, f[6], n), floats), $"{where}: float");
        decimal?[] readDecimals = await ReadNullableAsync<decimal>(rowGroup, f[7], n);
        Check(SequenceEqual(readDecimals, decimals) && readDecimals.All(d => d is null || d.Value.Scale == 2), $"{where}: decimal? (values and scale)");
        DateTime?[] readStamps = await ReadNullableAsync<DateTime>(rowGroup, f[8], n);
        Check(SequenceEqual(readStamps, stamps) && readStamps.All(t => t is null || t.Value.Kind == DateTimeKind.Utc), $"{where}: timestamp (values and kind)");
        Check(SequenceEqual(await ReadAsync<DateOnly>(rowGroup, f[9], n), dates), $"{where}: DateOnly");
        Check(SequenceEqual(await ReadAsync<Guid>(rowGroup, f[10], n), guids), $"{where}: Guid");
        var readStrings = new string?[n];
        await rowGroup.ReadAsync(f[11], new Memory<string?>(readStrings));
        Check(SequenceEqual(readStrings, strings), $"{where}: string");
        // strings as their UTF-8 bytes, the way generated readers for Lake.Parquet read them
        var utf8 = new ReadOnlyMemory<byte>[n];
        var defs = new int[n];
        await rowGroup.ReadRawAsync<ReadOnlyMemory<byte>>(f[11], utf8, defs, null);
        int packed = 0;
        Check(strings.Select((s, i) => s == null ? defs[i] == 0 : defs[i] == 1 && System.Text.Encoding.UTF8.GetString(utf8[packed++].Span) == s).All(ok => ok), $"{where}: string as UTF-8");
        var readBytes = new byte[]?[n];
        await rowGroup.ReadAsync(f[12], new Memory<byte[]?>(readBytes));
        Check(readBytes.Zip(bytes).All(p => p.First == null ? p.Second == null : p.Second != null && p.First.SequenceEqual(p.Second)), $"{where}: byte[]");
    }

    static async Task EncryptedAsync() {
        byte[] footerKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var field = new DataField<string>("secret");
        string?[] values = ["a", null, "ciphertext", "é"];
        foreach(bool plaintextFooter in new[] { false, true }) {
            var options = new ParquetOptions {
                Encryption = new ParquetEncryptionOptions { FooterKey = footerKey, PlaintextFooter = plaintextFooter },
            };
            byte[] file = await WriteAsync(new ParquetSchema(field), options, rg => rg.WriteAsync(field, values));
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file),
                new ParquetOptions { Decryption = new ParquetDecryptionOptions { FooterKey = footerKey } });
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            var read = new string?[values.Length];
            await rowGroup.ReadAsync(reader.Schema.GetDataFields()[0], new Memory<string?>(read));
            Check(SequenceEqual(read, values), $"encryption (plaintext footer {plaintextFooter})");
        }
    }

    static async Task StatisticsAsync() {
        var field = new DataField<long?>("v");
        long?[] values = [5, null, -3, 42, null, 7];
        byte[] file = await WriteAsync(new ParquetSchema(field), new ParquetOptions(), rg => rg.WriteAsync<long>(field, values));
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataColumnStatistics? stats = rowGroup.GetStatistics(reader.Schema.GetDataFields()[0]);
        Check(stats is { NullCount: 2 } && Equals(stats.MinValue, -3L) && Equals(stats.MaxValue, 42L), $"statistics: {stats?.NullCount} nulls, {stats?.MinValue} .. {stats?.MaxValue}");
    }

    enum Colour { Red, Green }

    /// <summary>
    /// Enum columns are the serializer's, which Lake.Parquet leaves out: the core API refuses them, the same way here
    /// as on the JIT (and so a field of an enum type, whose nullable type native AOT does not construct, is never used).
    /// </summary>
    static async Task EnumsRefusedAsync() {
        var field = new DataField<Colour>("c");
        try {
            await WriteAsync(new ParquetSchema(field), new ParquetOptions(), rg => rg.WriteAsync<Colour>(field, new[] { Colour.Red }));
            Check(false, "enum column: written, but the core API has no encoder for enums");
        } catch(InvalidOperationException e) {
            Check(e.Message.Contains("not supported", StringComparison.Ordinal), $"enum column: refused with '{e.Message}'");
        }
    }

    static async Task<T[]> ReadAsync<T>(ParquetRowGroupReader rowGroup, DataField field, int n) where T : struct {
        var values = new T[n];
        await rowGroup.ReadAsync<T>(field, values);
        return values;
    }

    static async Task<T?[]> ReadNullableAsync<T>(ParquetRowGroupReader rowGroup, DataField field, int n) where T : struct {
        var values = new T?[n];
        await rowGroup.ReadAsync<T>(field, new Memory<T?>(values));
        return values;
    }

    static bool SequenceEqual<T>(IEnumerable<T> a, IEnumerable<T> b) => a.SequenceEqual(b);
}
