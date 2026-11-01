using Parquet;
using Parquet.Data;
using Parquet.Encryption;
using Parquet.File.Values.Primitives;
using Parquet.Schema;
using PMeta = Parquet.Meta;

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
        await BitWidthsAsync();
        await DictionaryIndexWidthsAsync();
        await RawColumnsAsync();
        await UnseekableOutputAsync();
        await MoreTypesAsync();
        await SchemaAndMetadataAsync();
        await UnsignedStatisticsAsync();
        await DecimalOutOfRangeAsync();
        await Int96AsBigIntegerAsync();
        await TypedStringsAndBytesAsync();
        await LogicalTypesAsync();
        await OtherWritersMetadataAsync();
        await OtherWritersEncodingsAsync();
        await DateTimeFormatsAsync();
        await LargeBooleanColumnAsync();
        await SingleValueDictionaryAsync();
        await AppendWithAnotherSchemaAsync();
        await GeometryStatisticsAsync();
        await PageIndexAndBloomFiltersAsync();
        await PageIndexAndBloomFiltersWrittenAsync();
        await FooterStructuresFromOtherWritersAsync();
        await NestedTypedColumnsAsync();
        await DecimalExactOrRefusedAsync();
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
        // the field itself works, its nullable type included (constructed natively too)
        var nullable = new DataField<Colour>("n", nullable: true);
        Check(nullable.ClrNullableIfHasNullsType == typeof(Colour?), $"enum field: nullable type {nullable.ClrNullableIfHasNullsType}");
        var field = new DataField<Colour>("c");
        try {
            await WriteAsync(new ParquetSchema(field), new ParquetOptions(), rg => rg.WriteAsync<Colour>(field, new[] { Colour.Red }));
            Check(false, "enum column: written, but the core API has no encoder for enums");
        } catch(InvalidOperationException e) {
            Check(e.Message.Contains("not supported", StringComparison.Ordinal), $"enum column: refused with '{e.Message}'");
        }
    }

    /// <summary>
    /// Every bit width the delta encoder packs and unpacks (0 to 32 for int, 0 to 64 for long): each column alternates
    /// around a value so that its deltas span exactly that many bits. The bit-packing kernels are generated per width, so
    /// a width no column reaches is code the native build never ran.
    /// </summary>
    static async Task BitWidthsAsync() {
        for(int w = 0; w <= 64; w++) {
            long span = w == 0 ? 0 : w == 64 ? long.MaxValue : (1L << (w - 1)) - 1;
            long[] longs = Enumerable.Range(0, 300).Select(i => w switch {
                0 => 7L * i,                           // constant deltas: width 0
                1 => i / 2,                            // deltas 0 and 1
                _ => i % 2 == 0 ? 0L : span            // deltas +span and -span
            }).ToArray();
            await RoundTripAsync(new DataField<long>("l"), longs, $"delta long, width {w}");
            if(w <= 32) {
                int[] ints = longs.Select(v => (int)Math.Min(v, int.MaxValue)).ToArray();   // width 32: deltas of int.MaxValue
                await RoundTripAsync(new DataField<int>("i"), ints, $"delta int, width {w}");
            }
        }

        static async Task RoundTripAsync<T>(DataField<T> field, T[] values, string what) where T : struct {
            var options = new ParquetOptions();
            options.ColumnEncodingHints[field.Path.ToString()] = EncodingHint.DeltaBinaryPacked;
            byte[] file = await WriteAsync(new ParquetSchema(field), options, rg => rg.WriteAsync<T>(field, values));
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            Check(SequenceEqual(await ReadAsync<T>(rowGroup, reader.Schema.GetDataFields()[0], values.Length), values), what);
        }
    }

    /// <summary>
    /// Dictionary indexes of every width from 1 to 16 bits (RLE/bit-packed hybrid): string columns (the writer
    /// dictionary-encodes strings) of 2^w distinct values, each twice.
    /// </summary>
    static async Task DictionaryIndexWidthsAsync() {
        for(int w = 1; w <= 16; w++) {
            int distinct = 1 << w;
            string?[] values = Enumerable.Range(0, distinct * 2).Select(i => "v" + (i * 7919 % distinct)).ToArray();
            var field = new DataField<string>("d");
            var options = new ParquetOptions();
            options.ColumnEncodingHints[field.Path.ToString()] = EncodingHint.Dictionary;
            byte[] file = await WriteAsync(new ParquetSchema(field), options, rg => rg.WriteAsync(field, values));
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            bool dictionary = rowGroup.RowGroup.Columns[0].MetaData!.DictionaryPageOffset != null;
            var read = new string?[values.Length];
            await rowGroup.ReadAsync(reader.Schema.GetDataFields()[0], new Memory<string?>(read));
            Check(dictionary && SequenceEqual(read, values),
                $"dictionary of {distinct} values (index width {w}): {(dictionary ? "dictionary-encoded" : "not dictionary-encoded")}");
        }
    }

    /// <summary>
    /// Columns declared by their physical type (RawDataField), as Lake.NET's backend writes Iceberg columns: a
    /// TIMESTAMP(NANOS) as long nanoseconds, a DATE as int days, a fixed[4] as FIXED_LEN_BYTE_ARRAY(4), a STRING.
    /// </summary>
    static async Task RawColumnsAsync() {
        var nanos = new RawDataField("ts", PMeta.Type.INT64, false,
            new PMeta.LogicalType { TIMESTAMP = new PMeta.TimestampType { IsAdjustedToUTC = true, Unit = new PMeta.TimeUnit { NANOS = new PMeta.NanoSeconds() } } });
        var days = new RawDataField("day", PMeta.Type.INT32, true, new PMeta.LogicalType { DATE = new PMeta.DateType() });
        var fixed4 = new RawDataField("f", PMeta.Type.FIXED_LEN_BYTE_ARRAY, false, typeLength: 4);
        var text = new RawDataField("s", PMeta.Type.BYTE_ARRAY, true, new PMeta.LogicalType { STRING = new PMeta.StringType() });
        long[] ns = [1_700_000_000_123_456_789, -1, 0];
        int?[] dayValues = [19_000, null, -5];
        byte[][] fixedValues = [[1, 2, 3, 4], [0, 0, 0, 0], [255, 254, 253, 252]];
        string?[] texts = ["a", null, "ü"];
        byte[] file = await WriteAsync(new ParquetSchema(nanos, days, fixed4, text), new ParquetOptions(), async rg => {
            await rg.WriteAsync<long>(nanos, ns);
            await rg.WriteAsync<int>(days, dayValues);
            await rg.WriteAsync(fixed4, fixedValues);
            await rg.WriteAsync(text, texts);
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        Check(SequenceEqual(await ReadAsync<long>(rowGroup, f[0], 3), ns), "raw TIMESTAMP(NANOS) as long nanoseconds");
        Check(SequenceEqual(await ReadNullableAsync<int>(rowGroup, f[1], 3), dayValues), "raw DATE as int days");
        var readFixed = new byte[]?[3];
        await rowGroup.ReadAsync(f[2], new Memory<byte[]?>(readFixed));
        Check(f[2].FixedLength == 4 && readFixed.Zip(fixedValues).All(p => p.First!.SequenceEqual(p.Second)), "raw FIXED_LEN_BYTE_ARRAY(4)");
        var readTexts = new string?[3];
        await rowGroup.ReadAsync(f[3], new Memory<string?>(readTexts));
        Check(SequenceEqual(readTexts, texts), "raw STRING");
    }

    /// <summary>Writing to a stream that cannot seek (a network or pipe stream): the writer meters its own position.</summary>
    static async Task UnseekableOutputAsync() {
        var field = new DataField<int>("v");
        int[] values = Enumerable.Range(0, 1000).ToArray();
        using var inner = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), new ForwardOnlyStream(inner))) {
            for(int g = 0; g < 2; g++) {
                using ParquetRowGroupWriter rg = writer.CreateRowGroup();
                await rg.WriteAsync<int>(field, values);
            }
        }
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(inner.ToArray()));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(1);
        Check(reader.RowGroupCount == 2 && SequenceEqual(await ReadAsync<int>(rowGroup, reader.Schema.GetDataFields()[0], values.Length), values),
            "written to a stream that cannot seek");
    }

    /// <summary>
    /// The types the typed-column checks leave out: 16-bit and unsigned integers under DELTA_BINARY_PACKED (their own
    /// generated delta kernels), Interval and BigDecimal; each read back, with its statistics.
    /// </summary>
    static async Task MoreTypesAsync() {
        await RoundTripAsync<short>(new DataField<short>("s"), [short.MinValue, -1, 0, 1, short.MaxValue, 7], EncodingHint.DeltaBinaryPacked);
        await RoundTripAsync<ushort>(new DataField<ushort>("us"), [0, 1, ushort.MaxValue, 300, 7, 2], EncodingHint.DeltaBinaryPacked);
        await RoundTripAsync<uint>(new DataField<uint>("ui"), [0, 1, uint.MaxValue, 300, 7, 2], EncodingHint.DeltaBinaryPacked);
        await RoundTripAsync<Interval>(new DataField<Interval>("iv"), [new Interval(1, 2, 3), new Interval(0, 0, 0), new Interval(12, 31, 86_399_999)], EncodingHint.Default);

        var big = new DecimalDataField("bd", 38, 4, clrType: typeof(BigDecimal));
        BigDecimal[] bigs = [BigDecimal.FromDecimal(12345.6789m, 38, 4), BigDecimal.FromDecimal(-1m, 38, 4), new BigDecimal(System.Numerics.BigInteger.Pow(10, 37), 38, 4)];
        byte[] file = await WriteAsync(new ParquetSchema(big), new ParquetOptions(), rg => rg.WriteAsync<BigDecimal>(big, bigs));
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), new ParquetOptions { UseBigDecimal = true });
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        BigDecimal[] read = await ReadAsync<BigDecimal>(rowGroup, reader.Schema.GetDataFields()[0], bigs.Length);
        Check(read.Select(b => b.ToString()).SequenceEqual(bigs.Select(b => b.ToString())), $"BigDecimal(38,4): {string.Join(", ", read.Select(b => b.ToString()))}");

        static async Task RoundTripAsync<T>(DataField field, T[] values, EncodingHint hint) where T : struct {
            var options = new ParquetOptions();
            options.ColumnEncodingHints[field.Path.ToString()] = hint;
            byte[] file = await WriteAsync(new ParquetSchema(field), options, rg => rg.WriteAsync<T>(field, values));
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            DataField read = reader.Schema.GetDataFields()[0];
            Check(SequenceEqual(await ReadAsync<T>(rowGroup, read, values.Length), values), $"{typeof(T).Name} ({hint})");
            Check(rowGroup.GetStatistics(read) is { NullCount: 0 }, $"{typeof(T).Name} ({hint}): statistics");
        }
    }

    /// <summary>The schema API (lookup, equality, flattening) and custom key-value metadata, file and column level.</summary>
    static async Task SchemaAndMetadataAsync() {
        var id = new DataField<int>("id");
        var name = new DataField<string>("name");
        var schema = new ParquetSchema(id, new StructField("s", name));
        int[] ids = [1, 2];
        byte[] file;
        using(var ms = new MemoryStream()) {
            await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, ms)) {
                writer.CustomMetadata = new Dictionary<string, string> { ["writer"] = "lake" };
                using ParquetRowGroupWriter rg = writer.CreateRowGroup();
                await rg.WriteAsync<int>(id, ids);
                await rg.WriteAsync(name, new string?[] { "a", null });
            }
            file = ms.ToArray();
        }
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        ParquetSchema read = reader.Schema;
        Check(read.Equals(schema) && read.GetHashCode() == schema.GetHashCode(), "schema read back equals the one written");
        Check(!read.Equals(new ParquetSchema(id)) && read.GetNotEqualsMessage(new ParquetSchema(id), "file", "expected").Length > 0, "schema inequality is reported");
        Check(read.FindDataField(new FieldPath("s", "name")) is { Name: "name" } && read.FindDataField("id") is { Name: "id" } && read.DataFields.Length == 2 && read[0].Name == "id", "schema lookup");
        Check(read.Flatten().Count() >= 3, "schema flattened");
        Check(reader.CustomMetadata.TryGetValue("writer", out string? v) && v == "lake", "file key-value metadata");
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Check(rowGroup.ColumnExists(read.DataFields[0]) && rowGroup.GetCustomMetadata(read.DataFields[0]).Count == 0, "column lookup and column metadata");
    }

    /// <summary>INT32 and INT64 columns with an unsigned INTEGER annotation: statistics in unsigned order.</summary>
    static async Task UnsignedStatisticsAsync() {
        var u32 = new RawDataField("u32", PMeta.Type.INT32, false, new PMeta.LogicalType { INTEGER = new PMeta.IntType { BitWidth = 32, IsSigned = false } });
        var u64 = new RawDataField("u64", PMeta.Type.INT64, false, new PMeta.LogicalType { INTEGER = new PMeta.IntType { BitWidth = 64, IsSigned = false } });
        int[] ints = [1, -1, 7];            // -1 is uint.MaxValue: the maximum in unsigned order
        long[] longs = [1L, -1L, 7L];
        byte[] file = await WriteAsync(new ParquetSchema(u32, u64), new ParquetOptions(), async rg => {
            await rg.WriteAsync<int>(u32, ints);
            await rg.WriteAsync<long>(u64, longs);
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        DataColumnStatistics? s32 = rowGroup.GetStatistics(f[0]);
        DataColumnStatistics? s64 = rowGroup.GetStatistics(f[1]);
        Check(s32?.SortOrder == ColumnSortOrder.Unsigned && Equals(s32.PhysicalMin, 1) && Equals(s32.PhysicalMax, -1),
            $"unsigned INT32 statistics: {s32?.SortOrder} {s32?.PhysicalMin} .. {s32?.PhysicalMax}");
        Check(s64?.SortOrder == ColumnSortOrder.Unsigned && Equals(s64.PhysicalMin, 1L) && Equals(s64.PhysicalMax, -1L),
            $"unsigned INT64 statistics: {s64?.SortOrder} {s64?.PhysicalMin} .. {s64?.PhysicalMax}");
    }

    /// <summary>A decimal too large for its column's precision is refused, not written wrong.</summary>
    static async Task DecimalOutOfRangeAsync() {
        var field = new DecimalDataField("d", 18, 2);
        try {
            await WriteAsync(new ParquetSchema(field), new ParquetOptions(), rg => rg.WriteAsync<decimal>(field, new[] { decimal.MaxValue }));
            Check(false, "decimal(18,2) of decimal.MaxValue: written");
        } catch(ParquetException e) {
            Check(e.Message.Length > 0, "decimal(18,2) of decimal.MaxValue: refused");
        }
    }

    /// <summary>INT96 read as BigInteger when timestamps are not wanted (TreatBigIntegersAsDates off).</summary>
    static async Task Int96AsBigIntegerAsync() {
        var field = new DataField<System.Numerics.BigInteger>("i96");
        System.Numerics.BigInteger[] values = [0, 1, -1, System.Numerics.BigInteger.Pow(2, 90)];
        byte[] file = await WriteAsync(new ParquetSchema(field), new ParquetOptions { TreatBigIntegersAsDates = false }, rg => rg.WriteAsync<System.Numerics.BigInteger>(field, values));
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), new ParquetOptions { TreatBigIntegersAsDates = false });
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Check(SequenceEqual(await ReadAsync<System.Numerics.BigInteger>(rowGroup, reader.Schema.GetDataFields()[0], values.Length), values), "INT96 as BigInteger");
    }

    /// <summary>
    /// Strings and byte arrays written from and read into .NET's own types (string, byte[]), plain and
    /// dictionary-encoded, with their statistics. (ParquetOptions.PreferUntypedString/ByteArray are the serializer's,
    /// which Lake.Parquet leaves out: core fields are always untyped memory underneath.)
    /// </summary>
    static async Task TypedStringsAndBytesAsync() {
        var s = new DataField<string>("s");
        var b = new DataField<byte[]>("b");
        string?[] texts = ["x", null, "żółw"];
        byte[]?[] blobs = [[1], null, [2, 3]];
        foreach(EncodingHint hint in new[] { EncodingHint.Default, EncodingHint.Dictionary }) {
            var options = new ParquetOptions();
            options.ColumnEncodingHints["s"] = hint;
            byte[] file = await WriteAsync(new ParquetSchema(s, b), options, async rg => {
                await rg.WriteAsync(s, texts);
                await rg.WriteAsync(b, blobs);
            });
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            DataField[] f = reader.Schema.GetDataFields();
            var readTexts = new string?[3];
            await rowGroup.ReadAsync(f[0], new Memory<string?>(readTexts));
            var readBlobs = new byte[]?[3];
            await rowGroup.ReadAsync(f[1], new Memory<byte[]?>(readBlobs));
            Check(SequenceEqual(readTexts, texts) && readBlobs.Zip(blobs).All(p => p.First == null ? p.Second == null : p.Second != null && p.First.SequenceEqual(p.Second))
                && rowGroup.GetStatistics(f[0]) is { NullCount: 1 }, $"string and byte[] ({hint})");
        }
    }

    /// <summary>
    /// Every logical type a column can be declared with (RawDataField), written and read back with its annotation:
    /// ENUM, JSON, BSON, FLOAT16, GEOMETRY, GEOGRAPHY, TIME, UNKNOWN (always null) and Iceberg v4's FILE.
    /// </summary>
    static async Task LogicalTypesAsync() {
        var fields = new (RawDataField Field, string What)[] {
            (new RawDataField("enum", PMeta.Type.BYTE_ARRAY, false, new PMeta.LogicalType { ENUM = new PMeta.EnumType() }), "ENUM"),
            (new RawDataField("json", PMeta.Type.BYTE_ARRAY, false, new PMeta.LogicalType { JSON = new PMeta.JsonType() }), "JSON"),
            (new RawDataField("bson", PMeta.Type.BYTE_ARRAY, false, new PMeta.LogicalType { BSON = new PMeta.BsonType() }), "BSON"),
            (new RawDataField("f16", PMeta.Type.FIXED_LEN_BYTE_ARRAY, false, new PMeta.LogicalType { FLOAT16 = new PMeta.Float16Type() }, typeLength: 2), "FLOAT16"),
            (new RawDataField("geom", PMeta.Type.BYTE_ARRAY, false, new PMeta.LogicalType { GEOMETRY = new PMeta.GeometryType { Crs = "OGC:CRS84" } }), "GEOMETRY"),
            (new RawDataField("geog", PMeta.Type.BYTE_ARRAY, false, new PMeta.LogicalType { GEOGRAPHY = new PMeta.GeographyType { Crs = "OGC:CRS84", Algorithm = PMeta.EdgeInterpolationAlgorithm.SPHERICAL } }), "GEOGRAPHY"),
            (new RawDataField("time", PMeta.Type.INT64, false, new PMeta.LogicalType { TIME = new PMeta.TimeType { IsAdjustedToUTC = true, Unit = new PMeta.TimeUnit { MICROS = new PMeta.MicroSeconds() } } }), "TIME"),
            (new RawDataField("unknown", PMeta.Type.INT32, true, new PMeta.LogicalType { UNKNOWN = new PMeta.NullType() }), "UNKNOWN"),
            (new RawDataField("file", PMeta.Type.BYTE_ARRAY, false, new PMeta.LogicalType { FILE = new PMeta.FileType() }), "FILE"),
        };
        byte[] wkbPoint = [1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] file = await WriteAsync(new ParquetSchema(fields.Select(f => (Field)f.Field).ToArray()), new ParquetOptions(), async rg => {
            await rg.WriteAsync(fields[0].Field, new string?[] { "RED", "GREEN" });
            await rg.WriteAsync(fields[1].Field, new string?[] { "{}", "[1]" });
            await rg.WriteAsync(fields[2].Field, new byte[]?[] { [5, 0, 0, 0, 0], [5, 0, 0, 0, 0] });
            await rg.WriteAsync(fields[3].Field, new byte[]?[] { [0, 0x3c], [0, 0xc0] });
            await rg.WriteAsync(fields[4].Field, new byte[]?[] { wkbPoint, wkbPoint });
            await rg.WriteAsync(fields[5].Field, new byte[]?[] { wkbPoint, wkbPoint });
            await rg.WriteAsync<long>(fields[6].Field, new long[] { 0, 86_399_999_999 });
            await rg.WriteAsync<int>(fields[7].Field, new int?[] { null, null });
            await rg.WriteAsync(fields[8].Field, new byte[]?[] { [1], [2] });
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        for(int i = 0; i < fields.Length; i++) {
            PMeta.LogicalType? lt = reader.Metadata!.Schema[i + 1].LogicalType;
            bool kept = fields[i].What switch {
                "ENUM" => lt?.ENUM != null, "JSON" => lt?.JSON != null, "BSON" => lt?.BSON != null, "FLOAT16" => lt?.FLOAT16 != null,
                "GEOMETRY" => lt?.GEOMETRY?.Crs == "OGC:CRS84",
                "GEOGRAPHY" => lt?.GEOGRAPHY is { Crs: "OGC:CRS84", Algorithm: PMeta.EdgeInterpolationAlgorithm.SPHERICAL },
                "TIME" => lt?.TIME?.Unit?.MICROS != null, "UNKNOWN" => lt?.UNKNOWN != null, "FILE" => lt?.FILE != null, _ => false
            };
            Check(kept, $"logical type {fields[i].What} written and read back");
        }
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Check(rowGroup.RowCount == 2, "logical types: rows");
    }

    /// <summary>
    /// A file parquet-cpp (pyarrow 26) wrote with everything the library does not write itself: a page index, bloom
    /// filters, page checksums, data page v2, sorting columns, column orders, size statistics and page encoding
    /// statistics, FLOAT16, JSON and UUID columns (all optional, pyarrow's default). Read; then appended to, which rewrites its footer with all of that
    /// metadata, and read again.
    /// </summary>
    static async Task OtherWritersMetadataAsync() {
        byte[] original;
        using(Stream resource = typeof(Api).Assembly.GetManifestResourceStream("lake_metadata_features.parquet")!) {
            using var copy = new MemoryStream();
            await resource.CopyToAsync(copy);
            original = copy.ToArray();
        }
        await CheckAsync(original, "parquet-cpp file", 2);

        using var appended = new MemoryStream();
        appended.Write(original);
        ParquetSchema schema;
        await using(ParquetReader r = await ParquetReader.CreateAsync(new MemoryStream(original))) {
            schema = r.Schema;
        }
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, appended, append: true)) {
        }
        await CheckAsync(appended.ToArray(), "parquet-cpp file, footer rewritten by an append", 2);

        static async Task CheckAsync(byte[] file, string what, int rowGroups) {
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
            PMeta.FileMetaData md = reader.Metadata!;
            PMeta.ColumnChunk chunk = md.RowGroups[0].Columns[0];
            Check(md.RowGroups.Count == rowGroups && md.RowGroups[0].SortingColumns is { Count: 1 } && md.ColumnOrders is { Count: > 0 }
                && chunk.MetaData!.EncodingStats is { Count: > 0 } && chunk.ColumnIndexOffset != null && chunk.OffsetIndexOffset != null
                && chunk.MetaData.BloomFilterOffset != null,
                $"{what}: metadata (sorting columns, column orders, encoding stats, page index and bloom filter offsets)");
            Check(md.RowGroups.Any(g => g.Columns.Any(c => c.MetaData?.SizeStatistics != null)), $"{what}: size statistics");
            DataField[] f = reader.Schema.GetDataFields();
            int read = 0;
            for(int g = 0; g < reader.RowGroupCount; g++) {
                using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
                int n = (int)rowGroup.RowCount;
                long?[] ids = await ReadNullableAsync<long>(rowGroup, f[0], n);
                var halves = new byte[]?[n];
                await rowGroup.ReadAsync(f[1], new Memory<byte[]?>(halves));
                var jsonBytes = new byte[]?[n];      // JSON is read as its UTF-8 bytes
                await rowGroup.ReadAsync(f[2], new Memory<byte[]?>(jsonBytes));
                string?[] json = jsonBytes.Select(j => j == null ? null : System.Text.Encoding.UTF8.GetString(j)).ToArray();
                Guid?[] uuids = await ReadNullableAsync<Guid>(rowGroup, f[3], n);
                double?[] doubles = await ReadNullableAsync<double>(rowGroup, f[4], n);
                var strings = new string?[n];
                await rowGroup.ReadAsync(f[5], new Memory<string?>(strings));
                int?[] optional = await ReadNullableAsync<int>(rowGroup, f[6], n);
                for(int i = 0; i < n; i++, read++) {
                    long id = ids[i] ?? -1;
                    bool ok = id == read && BitConverter.ToHalf(halves[i]!) == (Half)id && json[i] == $"{{\"k\":{id}}}"
                        && uuids[i] == new Guid(new byte[16 - 8].Concat(BitConverter.GetBytes(id).Reverse()).ToArray(), bigEndian: true)
                        && doubles[i] == id / 3.0 && strings[i] == $"v{id % 17}" && optional[i] == (id % 5 == 0 ? null : (int)id);
                    if(!ok) {
                        Check(false, $"{what}: row {read}");
                        return;
                    }
                }
            }
            Check(read == 200, $"{what}: {read} rows, every column");
        }
    }

    static byte[] Resource(string name) {
        using Stream resource = typeof(Api).Assembly.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Encodings and decimals as parquet-cpp (pyarrow 26) writes them, decoded to the column's own CLR type: timestamps,
    /// dates, int8, uint16 and INT32/INT64 decimals under DELTA_BINARY_PACKED; strings under DELTA_BYTE_ARRAY and
    /// DELTA_LENGTH_BYTE_ARRAY; float, int, fixed and decimal columns under BYTE_STREAM_SPLIT; decimals of precision 1 to 38
    /// (INT32, INT64 and FIXED_LEN_BYTE_ARRAY) dictionary-encoded. The same table written plain (the twin) must decode
    /// to the same values, column by column, with decimals as decimal and as BigDecimal and dates as DateTime and
    /// DateOnly.
    /// </summary>
    static async Task OtherWritersEncodingsAsync() {
        byte[] encoded = Resource("lake_encodings_other_writers.parquet");
        byte[] plain = Resource("lake_encodings_plain_twin.parquet");
        foreach((string what, ParquetOptions options) in new[] {
            ("defaults", new ParquetOptions()),
            ("BigDecimal, DateOnly", new ParquetOptions { UseBigDecimal = true, UseDateOnlyTypeForDates = true }) }) {
            List<(string Column, string Values)> a = await LogicalColumnsAsync(encoded, options);
            List<(string Column, string Values)> b = await LogicalColumnsAsync(plain, options);
            Check(a.Count == 24 && b.Count == 24, $"other writers' encodings ({what}): {a.Count} columns");
            foreach(((string column, string values), (string _, string twin)) in a.Zip(b)) {
                Check(values == twin && values.Length > 0, $"other writers' encodings ({what}): {column} decodes as its plain twin does");
            }
        }

        static async Task<List<(string, string)>> LogicalColumnsAsync(byte[] file, ParquetOptions options) {
            var result = new List<(string, string)>();
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), options);
            foreach(DataField field in reader.Schema.GetDataFields()) {
                var text = new System.Text.StringBuilder();
                for(int g = 0; g < reader.RowGroupCount; g++) {
                    using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
                    int n = (int)rowGroup.RowCount;
                    text.Append(field.ClrType switch {
                        Type t when t == typeof(DateTime) => Join(await ReadNullableAsync<DateTime>(rowGroup, field, n), v => v.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Type t when t == typeof(DateOnly) => Join(await ReadNullableAsync<DateOnly>(rowGroup, field, n), v => v.DayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Type t when t == typeof(decimal) => Join(await ReadNullableAsync<decimal>(rowGroup, field, n), v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Type t when t == typeof(BigDecimal) => Join(await ReadNullableAsync<BigDecimal>(rowGroup, field, n), v => v.ToString()),
                        Type t when t == typeof(sbyte) => Join(await ReadNullableAsync<sbyte>(rowGroup, field, n), v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Type t when t == typeof(ushort) => Join(await ReadNullableAsync<ushort>(rowGroup, field, n), v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Type t when t == typeof(int) => Join(await ReadNullableAsync<int>(rowGroup, field, n), v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Type t when t == typeof(long) => Join(await ReadNullableAsync<long>(rowGroup, field, n), v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        Type t when t == typeof(float) => Join(await ReadNullableAsync<float>(rowGroup, field, n), v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                        Type t when t == typeof(ReadOnlyMemory<char>) => await StringsAsync(rowGroup, field, n),
                        _ => await BytesAsync(rowGroup, field, n),
                    });
                }
                result.Add((field.Name, text.ToString()));
            }
            return result;
        }
        static string Join<T>(T?[] values, Func<T, string> format) where T : struct => string.Join('|', values.Select(v => v is null ? "null" : format(v.Value)));
        static async Task<string> StringsAsync(ParquetRowGroupReader rowGroup, DataField field, int n) {
            var values = new string?[n];
            await rowGroup.ReadAsync(field, new Memory<string?>(values));
            return string.Join('|', values.Select(v => v ?? "null"));
        }
        static async Task<string> BytesAsync(ParquetRowGroupReader rowGroup, DataField field, int n) {
            var values = new byte[]?[n];
            await rowGroup.ReadAsync(field, new Memory<byte[]?>(values));
            return string.Join('|', values.Select(v => v == null ? "null" : Convert.ToHexString(v)));
        }
    }

    /// <summary>Every DateTimeDataField format and unit, nulls included: INT96 (Impala), dates, millis, micros, nanos.</summary>
    static async Task DateTimeFormatsAsync() {
        DateTime[] instants = [new DateTime(2024, 2, 29, 23, 59, 59, 999, DateTimeKind.Utc).AddTicks(9_999), new DateTime(1969, 7, 20, 20, 17, 40, DateTimeKind.Utc), DateTime.UnixEpoch];
        var fields = new (DateTimeDataField Field, Func<DateTime, DateTime> Expected)[] {
            (new DateTimeDataField("impala", DateTimeFormat.Impala, isNullable: true), d => d),
            (new DateTimeDataField("date_time", DateTimeFormat.DateAndTime, isNullable: true), d => d.AddTicks(-(d.Ticks % TimeSpan.TicksPerMillisecond))),
            (new DateTimeDataField("date_time_micros", DateTimeFormat.DateAndTimeMicros, isNullable: true), d => d.AddTicks(-(d.Ticks % 10))),
            (new DateTimeDataField("date", DateTimeFormat.Date, isNullable: true), d => d.Date),
            (new DateTimeDataField("ts_millis", DateTimeFormat.Timestamp, unit: DateTimeTimeUnit.Millis, isNullable: true), d => d.AddTicks(-(d.Ticks % TimeSpan.TicksPerMillisecond))),
            (new DateTimeDataField("ts_micros", DateTimeFormat.Timestamp, unit: DateTimeTimeUnit.Micros, isNullable: true), d => d.AddTicks(-(d.Ticks % 10))),
            (new DateTimeDataField("ts_nanos", DateTimeFormat.Timestamp, unit: DateTimeTimeUnit.Nanos, isNullable: true), d => d),
            (new DateTimeDataField("ts_local", DateTimeFormat.Timestamp, isAdjustedToUTC: false, unit: DateTimeTimeUnit.Micros, isNullable: true), d => d.AddTicks(-(d.Ticks % 10))),
        };
        DateTime?[] values = [instants[0], null, instants[1], instants[2]];
        byte[] file = await WriteAsync(new ParquetSchema(fields.Select(f => (Field)f.Field).ToArray()), new ParquetOptions(), async rg => {
            foreach((DateTimeDataField field, _) in fields) {
                await rg.WriteAsync<DateTime>(field, values);
            }
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] read = reader.Schema.GetDataFields();
        for(int i = 0; i < fields.Length; i++) {
            DateTime?[] got = await ReadNullableAsync<DateTime>(rowGroup, read[i], values.Length);
            DateTime?[] expected = values.Select(v => v is null ? (DateTime?)null : fields[i].Expected(v.Value)).ToArray();
            Check(got.Select(g => g?.Ticks).SequenceEqual(expected.Select(e => e?.Ticks)), $"DateTime as {fields[i].Field.Name}: {string.Join(", ", got.Select(g => g?.ToString("O") ?? "null"))}");
        }
    }

    /// <summary>A boolean column long enough for the vectorised bit-packing path, with nulls.</summary>
    static async Task LargeBooleanColumnAsync() {
        var field = new DataField<bool?>("flag");
        var r = new Random(7);
        bool?[] values = Enumerable.Range(0, 100_003).Select(i => i % 13 == 0 ? (bool?)null : r.Next(2) == 1).ToArray();
        byte[] file = await WriteAsync(new ParquetSchema(field), new ParquetOptions(), rg => rg.WriteAsync<bool>(field, values));
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        Check(SequenceEqual(await ReadNullableAsync<bool>(rowGroup, reader.Schema.GetDataFields()[0], values.Length), values), "100,003 booleans with nulls");
    }

    /// <summary>A dictionary of one value: indexes of bit width 0.</summary>
    static async Task SingleValueDictionaryAsync() {
        var field = new DataField<string>("same");
        string?[] values = Enumerable.Repeat<string?>("only", 500).ToArray();
        var options = new ParquetOptions();
        options.ColumnEncodingHints["same"] = EncodingHint.Dictionary;
        byte[] file = await WriteAsync(new ParquetSchema(field), options, rg => rg.WriteAsync(field, values));
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var read = new string?[values.Length];
        await rowGroup.ReadAsync(reader.Schema.GetDataFields()[0], new Memory<string?>(read));
        Check(rowGroup.RowGroup.Columns[0].MetaData!.DictionaryPageOffset != null && SequenceEqual(read, values), "dictionary of one value");
    }

    /// <summary>Appending with a schema that differs from the file's is refused, saying how it differs.</summary>
    static async Task AppendWithAnotherSchemaAsync() {
        var id = new DataField<int>("id");
        var name = new DataField<string>("name");
        byte[] file = await WriteAsync(new ParquetSchema(id, name), new ParquetOptions(), async rg => {
            await rg.WriteAsync<int>(id, new[] { 1 });
            await rg.WriteAsync(name, new string?[] { "a" });
        });
        foreach((string what, ParquetSchema other) in new[] {
            ("fewer fields", new ParquetSchema(id)),
            ("another name", new ParquetSchema(id, new DataField<string>("title"))),
            ("another type", new ParquetSchema(id, new DataField<long>("name"))),
            ("another nullability", new ParquetSchema(id, new DataField<string>("name", nullable: false))) }) {
            using var stream = new MemoryStream();
            stream.Write(file);
            try {
                await using ParquetWriter writer = await ParquetWriter.CreateAsync(other, stream, append: true);
                Check(false, $"append with {what}: accepted");
            } catch(ParquetException e) {
                Check(e.Message.Contains("does not match", StringComparison.Ordinal), $"append with {what}: refused with '{e.Message}'");
            }
        }
    }

    /// <summary>
    /// A GEOMETRY column as parquet-cpp (pyarrow 26) writes it: WKB points with geospatial statistics (bounding box,
    /// geometry types) in the column chunk. Read, then appended to so that the footer is rewritten with those
    /// statistics, and read again.
    /// </summary>
    static async Task GeometryStatisticsAsync() {
        byte[] original = Resource("lake_geometry.parquet");
        await CheckAsync(original, "GEOMETRY from parquet-cpp");
        ParquetSchema schema;
        await using(ParquetReader r = await ParquetReader.CreateAsync(new MemoryStream(original))) {
            schema = r.Schema;
        }
        using var appended = new MemoryStream();
        appended.Write(original);
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, appended, append: true)) {
        }
        await CheckAsync(appended.ToArray(), "GEOMETRY from parquet-cpp, footer rewritten by an append");

        static async Task CheckAsync(byte[] file, string what) {
            await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
            PMeta.ColumnMetaData meta = reader.Metadata!.RowGroups[0].Columns[0].MetaData!;
            PMeta.BoundingBox? box = meta.GeospatialStatistics?.Bbox;
            Check(reader.Metadata.Schema[1].LogicalType?.GEOMETRY != null
                && box is { Xmin: 0, Xmax: 9, Ymin: -9, Ymax: 0 } && meta.GeospatialStatistics!.GeospatialTypes is [1],
                $"{what}: logical type and geospatial statistics ({box?.Xmin}..{box?.Xmax}, {box?.Ymin}..{box?.Ymax})");
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            var points = new byte[]?[10];
            await rowGroup.ReadAsync(reader.Schema.GetDataFields()[0], new Memory<byte[]?>(points));
            Check(points.Select((p, i) => p != null && p.Length == 21 && BitConverter.ToDouble(p, 5) == i && BitConverter.ToDouble(p, 13) == -i).All(ok => ok),
                $"{what}: WKB points");
        }
    }

    /// <summary>
    /// The page index and bloom filters of the parquet-cpp file (lake_metadata_features.parquet): every page located
    /// and bounded, every value of the filtered columns in their bloom filters, absent ones mostly not.
    /// </summary>
    static async Task PageIndexAndBloomFiltersAsync() {
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(Resource("lake_metadata_features.parquet")));
        DataField[] f = reader.Schema.GetDataFields();
        DataField id = f.Single(x => x.Name == "id"), s = f.Single(x => x.Name == "s");
        for(int g = 0; g < reader.RowGroupCount; g++) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
            int n = (int)rowGroup.RowCount;
            PMeta.OffsetIndex? offsets = await rowGroup.ReadOffsetIndexAsync(id);
            PMeta.ColumnIndex? index = await rowGroup.ReadColumnIndexAsync(id);
            long?[] ids = await ReadNullableAsync<long>(rowGroup, id, n);
            bool pages = offsets != null && index != null && offsets.PageLocations.Count == index.MinValues.Count && offsets.PageLocations.Count > 1
                && offsets.PageLocations.Select((p, i) => {
                    int to = i + 1 < offsets.PageLocations.Count ? (int)offsets.PageLocations[i + 1].FirstRowIndex : n;
                    long?[] page = ids[(int)p.FirstRowIndex..to];
                    return BitConverter.ToInt64(index.MinValues[i]) == page.Min() && BitConverter.ToInt64(index.MaxValues[i]) == page.Max();
                }).All(ok => ok);
            Check(pages, $"page index, row group {g}: {offsets?.PageLocations.Count} pages, bounds as the data");

            BloomFilter? idFilter = await rowGroup.ReadBloomFilterAsync(id);
            BloomFilter? sFilter = await rowGroup.ReadBloomFilterAsync(s);
            var strings = new string?[n];
            await rowGroup.ReadAsync(s, new Memory<string?>(strings));
            Check(idFilter != null && sFilter != null && ids.All(v => idFilter.MightContain(v!.Value)) && strings.All(v => sFilter.MightContain(v!)),
                $"bloom filters, row group {g}: every value present");
            int falsePositives = Enumerable.Range(0, 1000).Count(i => idFilter!.MightContain(1_000_000L + i) || sFilter!.MightContain($"absent-{i}"));
            Check(falsePositives < 100, $"bloom filters, row group {g}: {falsePositives} of 1000 absent values reported present");
        }
    }

    /// <summary>A page index and bloom filters written by the library (WritePageIndex, BloomFilters), read back.</summary>
    static async Task PageIndexAndBloomFiltersWrittenAsync() {
        var id = new DataField<long>("id");
        var key = new DataField<string>("key");
        long[] ids = Enumerable.Range(0, 1000).Select(i => i * 7L).ToArray();
        string?[] keys = Enumerable.Range(0, 1000).Select(i => (string?)$"k{i % 300}").ToArray();
        var options = new ParquetOptions { WritePageIndex = true, MaxDataPageValues = 250 };
        options.BloomFilters["id"] = 0.01;
        options.BloomFilters["key"] = 0.01;
        byte[] file = await WriteAsync(new ParquetSchema(id, key), options, async rg => {
            await rg.WriteAsync<long>(id, ids);
            await rg.WriteAsync(key, keys);
        });
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        PMeta.OffsetIndex? offsets = await rowGroup.ReadOffsetIndexAsync(f[0]);
        PMeta.ColumnIndex? index = await rowGroup.ReadColumnIndexAsync(f[0]);
        Check(offsets?.PageLocations.Select(p => p.FirstRowIndex).SequenceEqual(new long[] { 0, 250, 500, 750 }) == true
            && index != null && Enumerable.Range(0, 4).All(p => BitConverter.ToInt64(index.MinValues[p]) == p * 250 * 7L && BitConverter.ToInt64(index.MaxValues[p]) == (p * 250 + 249) * 7L),
            "page index written: four pages, located and bounded");
        BloomFilter? idFilter = await rowGroup.ReadBloomFilterAsync(f[0]);
        BloomFilter? keyFilter = await rowGroup.ReadBloomFilterAsync(f[1]);
        Check(idFilter != null && keyFilter != null && ids.All(v => idFilter.MightContain(v)) && keys.All(v => keyFilter.MightContain(v!))
            && Enumerable.Range(0, 1000).Count(i => idFilter.MightContain(i * 7L + 3) || keyFilter.MightContain($"x{i}")) < 60,
            "bloom filters written: every value present, few false positives");
    }

    /// <summary>
    /// Footer structures the library never writes itself, from files other writers wrote (apache/parquet-testing): the
    /// IEEE 754 total order and INT96 timestamp column orders, a VARIANT group and a GEOGRAPHY column. Each is read,
    /// then the footer is rewritten by an empty append, and each must still be there.
    /// </summary>
    static async Task FooterStructuresFromOtherWritersAsync() {
        foreach((string file, Func<PMeta.FileMetaData, bool> has, string what) in new (string, Func<PMeta.FileMetaData, bool>, string)[] {
            ("floating_orders_nan_count.parquet", m => m.ColumnOrders!.Any(o => o.IEEE_754TOTALORDER != null) && m.ColumnOrders!.Any(o => o.TYPEORDER != null), "IEEE 754 total order"),
            ("int96_timestamp_order.parquet", m => m.ColumnOrders!.Any(o => o.INT96TIMESTAMPORDER != null), "INT96 timestamp order"),
            ("shredded_variant_case_001.parquet", m => m.Schema.Any(se => se.LogicalType?.VARIANT != null), "VARIANT"),
            ("geography_points.parquet", m => m.Schema.Any(se => se.LogicalType?.GEOGRAPHY?.Algorithm == PMeta.EdgeInterpolationAlgorithm.SPHERICAL), "GEOGRAPHY (spherical)"),
        }) {
            byte[] original = Resource(file);
            ParquetSchema schema;
            long rows;
            await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(original))) {
                Check(has(reader.Metadata!), $"{what}: read from {file}");
                schema = reader.Schema;
                rows = reader.Metadata!.NumRows;
            }
            using var appended = new MemoryStream();
            appended.Write(original);
            await using(ParquetWriter writer = await ParquetWriter.CreateAsync(schema, appended, append: true)) {
            }
            await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(appended.ToArray()))) {
                Check(has(reader.Metadata!) && reader.Metadata!.NumRows == rows, $"{what}: kept when the footer is rewritten");
            }
        }
    }

    sealed class ForwardOnlyStream(Stream inner) : Stream {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    /// <summary>
    /// Columns in optional structs and lists through the typed API (lake(0062)): values without nulls under an optional
    /// struct, a list of nullable ints with its repetition levels, and lists of strings and byte arrays with a null
    /// element and a missing list, null below the maximum definition level.
    /// </summary>
    static async Task NestedTypedColumnsAsync() {
        var a = new DataField<long>("a");
        var s = new DataField("s", typeof(string), isNullable: false);
        var x = new DataField<int?>("x");
        var ls = new DataField<string>("s");
        var lb = new DataField<byte[]>("b");
        var schema = new ParquetSchema(new StructField("st", a, s), new ListField("l", x), new ListField("ls", ls), new ListField("lb", lb));
        // rows: [1, null, 3], [], [4], [5]
        int?[] xs = [1, null, 3, null, 4, 5];
        int[] xr = [0, 1, 1, 0, 0, 0];
        // rows: ["a", null], null, ["b"], ["c"]
        string?[] strings = ["a", null, null, "b", "c"];
        byte[]?[] bytes = [[1], null, null, [2, 3], []];
        int[] sr = [0, 1, 0, 0, 0];
        byte[] file = await WriteAsync(schema, new ParquetOptions(), async rg => {
            await rg.WriteAsync<long>(a, new long[] { 7, 8, 9, 10 });
            await rg.WriteAsync(s, new[] { "w", "x", "y", "z" });
            await rg.WriteAsync<int>(x, xs, xr);
            await rg.WriteAsync(ls, strings, sr);
            await rg.WriteAsync(lb, bytes, sr);
        });

        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        Check(rowGroup.RowCount == 4, $"nested: row count {rowGroup.RowCount}");
        Check(SequenceEqual(await ReadNullableAsync<long>(rowGroup, f[0], 4), new long?[] { 7, 8, 9, 10 }), "nested: long under an optional struct");
        var readS = new string?[4];
        await rowGroup.ReadAsync(f[1], new Memory<string?>(readS));
        Check(SequenceEqual(readS, new[] { "w", "x", "y", "z" }), "nested: required string under an optional struct");
        var readX = new int?[6];
        int[] readXr = new int[6];
        await rowGroup.ReadAsync<int>(f[2], new Memory<int?>(readX), readXr);
        Check(SequenceEqual(readX, xs) && SequenceEqual(readXr, xr), "nested: list of int? with repetition levels");
        var readLs = new string?[5];
        int[] readLsr = new int[5];
        await rowGroup.ReadAsync(f[3], new Memory<string?>(readLs), readLsr);
        Check(SequenceEqual(readLs, strings) && SequenceEqual(readLsr, sr), "nested: list of strings, null element and null list");
        var readLb = new byte[]?[5];
        int[] readLbr = new int[5];
        await rowGroup.ReadAsync(f[4], new Memory<byte[]?>(readLb), readLbr);
        Check(readLb.Zip(bytes).All(p => p.First == null ? p.Second == null : p.Second != null && p.First.SequenceEqual(p.Second)) && SequenceEqual(readLbr, sr),
            "nested: list of byte arrays, null element and null list");
    }

    /// <summary>
    /// A DECIMAL read as System.Decimal is exact or refused (lake(0061)): pyarrow's decimal128(38,9) values that fit read
    /// exactly, one beyond decimal is refused naming UseBigDecimal, and a 38-digit value is refused rather than rounded.
    /// </summary>
    static async Task DecimalExactOrRefusedAsync() {
        byte[] file = Resource("lake_decimal_38_9.parquet");
        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file))) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            Check(SequenceEqual(await ReadNullableAsync<decimal>(rowGroup, reader.Schema.FindDataField("d38_fits"), 5),
                new decimal?[] { 1234567890123456789.123456789m, -0.000000001m, null, -1234567890123456789.123456789m, 0m }), "decimal(38,9) from pyarrow, exactly");
            try {
                await ReadNullableAsync<decimal>(rowGroup, reader.Schema.FindDataField("d38"), 5);
                Check(false, "decimal(38,9) beyond decimal: read");
            } catch(OverflowException e) {
                Check(e.Message.Contains(nameof(ParquetOptions.UseBigDecimal), StringComparison.Ordinal), "decimal(38,9) beyond decimal: refused, naming UseBigDecimal");
            }
        }

        var big = new BigDecimalDataField("d", 38, 9);
        var value = new BigDecimal(System.Numerics.BigInteger.Parse("12345678901234567890123456789123456789"), 38, 9);
        byte[] written = await WriteAsync(new ParquetSchema(big), new ParquetOptions(), rg => rg.WriteAsync<BigDecimal>(big, new[] { value }));
        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(written))) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            try {
                await ReadAsync<decimal>(rowGroup, reader.Schema.GetDataFields()[0], 1);
                Check(false, "decimal(38,9) with 38 digits: rounded");
            } catch(OverflowException) {
                Check(true, "decimal(38,9) with 38 digits: refused");
            }
        }
        await using(ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(written), new ParquetOptions { UseBigDecimal = true })) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            BigDecimal[] read = await ReadAsync<BigDecimal>(rowGroup, reader.Schema.GetDataFields()[0], 1);
            Check(read[0].UnscaledValue == value.UnscaledValue, "decimal(38,9) with 38 digits: exact as BigDecimal");
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
