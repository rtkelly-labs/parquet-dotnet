using BenchmarkDotNet.Attributes;
using Parquet;
using Parquet.Schema;

namespace Lake.Parquet.Benchmarks;

/// <summary>
/// Reading a million-row column the way Parquet.SourceGenerator's generated readers do: required columns
/// through ReadRawAsync into a rented buffer, nullable ones through ReadAsync into T?[], strings through
/// ReadRawAsync of ReadOnlyMemory&lt;char&gt; with definition levels, then a UTF-8 length check and a string each.
/// </summary>
[MemoryDiagnoser]
public class ReadBenchmarks {

    byte[] _longs = [], _doubles = [], _doublesNoNulls = [], _timestamps = [], _decimals = [], _strings = [], _dictionaryStrings = [];
    readonly long[] _longBuffer = new long[Data.Rows];
    readonly double?[] _doubleBuffer = new double?[Data.Rows];
    readonly DateTime?[] _timestampBuffer = new DateTime?[Data.Rows];
    readonly decimal?[] _decimalBuffer = new decimal?[Data.Rows];
    readonly ReadOnlyMemory<char>[] _charBuffer = new ReadOnlyMemory<char>[Data.Rows];
    readonly ReadOnlyMemory<byte>[] _byteBuffer = new ReadOnlyMemory<byte>[Data.Rows];
    readonly int[] _definitions = new int[Data.Rows];
    readonly string?[] _stringBuffer = new string?[Data.Rows];

    [GlobalSetup]
    public async Task Setup() {
        long[] longs = Data.Longs();
        var longField = new DataField<long>("v");
        _longs = await Data.WriteAsync(longField, rg => rg.WriteAsync<long>(longField, longs));

        var doubleField = new DataField<double?>("v");
        double?[] doubles = Data.Doubles(0.1);
        _doubles = await Data.WriteAsync(doubleField, rg => rg.WriteAsync<double>(doubleField, doubles));
        double?[] full = Data.Doubles(0);
        _doublesNoNulls = await Data.WriteAsync(doubleField, rg => rg.WriteAsync<double>(doubleField, full));

        var timestampField = new DateTimeDataField("v", DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Micros, isNullable: true);
        DateTime?[] timestamps = Data.Timestamps();
        _timestamps = await Data.WriteAsync(timestampField, rg => rg.WriteAsync<DateTime>(timestampField, timestamps));

        var decimalField = new DecimalDataField("v", 18, 2, isNullable: true);
        decimal?[] decimals = Data.Decimals();
        _decimals = await Data.WriteAsync(decimalField, rg => rg.WriteAsync<decimal>(decimalField, decimals));

        var stringField = new DataField<string>("v");
        string?[] strings = Data.Strings(500_000);
        _strings = await Data.WriteAsync(stringField, rg => rg.WriteAsync(stringField, strings));
        string?[] few = Data.Strings(1_000);
        _dictionaryStrings = await Data.WriteAsync(stringField, rg => rg.WriteAsync(stringField, few));
    }

    static async Task<(ParquetReader, ParquetRowGroupReader, DataField)> OpenAsync(byte[] file) {
        ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        return (reader, reader.OpenRowGroupReader(0), reader.Schema.GetDataFields()[0]);
    }

    [Benchmark]
    public async Task RequiredInt64() {
        (ParquetReader reader, ParquetRowGroupReader rg, DataField field) = await OpenAsync(_longs);
        await using(reader) using(rg)
            await rg.ReadRawAsync<long>(field, _longBuffer, null, null);
    }

    [Benchmark]
    public async Task NullableDouble_10pcNulls() {
        (ParquetReader reader, ParquetRowGroupReader rg, DataField field) = await OpenAsync(_doubles);
        await using(reader) using(rg)
            await rg.ReadAsync<double>(field, new Memory<double?>(_doubleBuffer));
    }

    [Benchmark]
    public async Task NullableDouble_NoNulls() {
        (ParquetReader reader, ParquetRowGroupReader rg, DataField field) = await OpenAsync(_doublesNoNulls);
        await using(reader) using(rg)
            await rg.ReadAsync<double>(field, new Memory<double?>(_doubleBuffer));
    }

    [Benchmark]
    public async Task NullableTimestampMicros() {
        (ParquetReader reader, ParquetRowGroupReader rg, DataField field) = await OpenAsync(_timestamps);
        await using(reader) using(rg)
            await rg.ReadAsync<DateTime>(field, new Memory<DateTime?>(_timestampBuffer));
    }

    [Benchmark]
    public async Task NullableDecimal18_2() {
        (ParquetReader reader, ParquetRowGroupReader rg, DataField field) = await OpenAsync(_decimals);
        await using(reader) using(rg)
            await rg.ReadAsync<decimal>(field, new Memory<decimal?>(_decimalBuffer));
    }

    [Benchmark]
    public Task Strings_HighCardinality() => ReadStringsAsync(_strings);

    [Benchmark]
    public Task Strings_Dictionary1000() => ReadStringsAsync(_dictionaryStrings);

    /// <summary>
    /// Strings read as their stored UTF-8 bytes (a string field's physical type, <c>lake(0017)</c>): no char
    /// decoding into an intermediate buffer, and the length limit is the byte length. Fork only: upstream rejects
    /// <c>ReadOnlyMemory&lt;byte&gt;</c> for string fields.
    /// </summary>
    [Benchmark]
    public Task Strings_HighCardinality_Utf8() => ReadUtf8StringsAsync(_strings);

    [Benchmark]
    public Task Strings_Dictionary1000_Utf8() => ReadUtf8StringsAsync(_dictionaryStrings);

    async Task ReadUtf8StringsAsync(byte[] file) {
        (ParquetReader reader, ParquetRowGroupReader rg, DataField field) = await OpenAsync(file);
        await using(reader) using(rg) {
            await rg.ReadRawAsync<ReadOnlyMemory<byte>>(field, _byteBuffer, _definitions, null);
            int packed = 0;
            for(int i = 0; i < Data.Rows; i++) {
                if(_definitions[i] == 0) {
                    _stringBuffer[i] = null;
                    continue;
                }
                ReadOnlySpan<byte> value = _byteBuffer[packed++].Span;
                if(value.Length > 1 << 20)
                    throw new InvalidDataException();
                _stringBuffer[i] = System.Text.Encoding.UTF8.GetString(value);
            }
        }
    }

    /// <summary>The generated reader's string path: raw chars and levels, a UTF-8 length check, a string each.</summary>
    async Task ReadStringsAsync(byte[] file) {
        (ParquetReader reader, ParquetRowGroupReader rg, DataField field) = await OpenAsync(file);
        await using(reader) using(rg) {
            await rg.ReadRawAsync<ReadOnlyMemory<char>>(field, _charBuffer, _definitions, null);
            int packed = 0;
            for(int i = 0; i < Data.Rows; i++) {
                if(_definitions[i] == 0) {
                    _stringBuffer[i] = null;
                    continue;
                }
                ReadOnlySpan<char> value = _charBuffer[packed++].Span;
                if(System.Text.Encoding.UTF8.GetByteCount(value) > 1 << 20)
                    throw new InvalidDataException();
                _stringBuffer[i] = value.ToString();
            }
        }
    }
}
