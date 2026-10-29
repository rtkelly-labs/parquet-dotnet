using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// Reading and writing columns as their physical Parquet values (<see cref="DataField.PhysicalClrType"/>): no logical
/// conversion, as table formats (Iceberg, DuckLake) hold values.
/// </summary>
public class PhysicalValuesTest {

    private static async Task<MemoryStream> WriteAsync<T>(DataField field, T[] values) where T : struct {
        var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<T>(field, values.AsMemory());
        }
        ms.Position = 0;
        return ms;
    }

    private static async Task<T[]> ReadAsync<T>(Stream file) where T : struct {
        file.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(file, leaveStreamOpen: true);
        DataField field = reader.Schema.DataFields[0];
        ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        var values = new T[rg.RowCount];
        Memory<int>? defs = field.MaxDefinitionLevel > 0 ? new Memory<int>(new int[rg.RowCount]) : (Memory<int>?)null;
        await rg.ReadRawAsync<T>(field, values, defs, null);
        return values;
    }

    [Fact]
    public async Task Nanosecond_timestamps_keep_every_digit() {
        var field = new DateTimeDataField("ts", DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Nanos, isNullable: false);
        long[] nanos = { 1_700_000_000_123_456_789L, -1L, 0L };
        using MemoryStream file = await WriteAsync(field, nanos);

        Assert.Equal(nanos, await ReadAsync<long>(file));
        // The logical view still works (to 100 ns ticks).
        DateTime[] times = await ReadAsync<DateTime>(file);
        Assert.Equal(DateTime.UnixEpoch.AddTicks(17_000_000_001_234_567L), times[0]);
        // And the annotation is the timestamp's.
        file.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(file, leaveStreamOpen: true);
        Assert.NotNull(reader.Metadata!.Schema[1].LogicalType!.TIMESTAMP!.Unit.NANOS);
    }

    [Fact]
    public async Task Dates_as_days_and_decimals_as_unscaled_integers() {
        var date = new DateTimeDataField("d", DateTimeFormat.Date, isNullable: false);
        using MemoryStream dates = await WriteAsync(date, new[] { 0, 19_723, -1 });
        Assert.Equal(new[] { 0, 19_723, -1 }, await ReadAsync<int>(dates));
        Assert.Equal(new DateTime(2024, 1, 1), (await ReadAsync<DateTime>(dates))[1]);

        var amount = new DecimalDataField("amount", 10, 2, isNullable: false);
        using MemoryStream amounts = await WriteAsync(amount, new[] { 12_345L, -1L });
        Assert.Equal(new[] { 12_345L, -1L }, await ReadAsync<long>(amounts));
        Assert.Equal(new[] { 123.45m, -0.01m }, await ReadAsync<decimal>(amounts));
    }

    [Fact]
    public async Task Wide_decimals_read_as_their_big_endian_bytes() {
        var wide = new DecimalDataField("big", 38, 4, isNullable: false);
        using MemoryStream file = await WriteAsync(wide, new[] { 1.5m });
        ReadOnlyMemory<byte>[] raw = await ReadAsync<ReadOnlyMemory<byte>>(file);
        Assert.Equal(15_000, (int)new System.Numerics.BigInteger(raw[0].Span, isUnsigned: false, isBigEndian: true));
    }

    [Fact]
    public async Task Unsigned_integers_move_as_their_bit_patterns() {
        var field = new DataField<uint>("u");
        using MemoryStream file = await WriteAsync(field, new[] { -1, 7 });
        Assert.Equal(new[] { uint.MaxValue, 7u }, await ReadAsync<uint>(file));
        Assert.Equal(new[] { -1, 7 }, await ReadAsync<int>(file));
    }

    [Fact]
    public async Task Repeated_values_dictionary_encoded_read_physically_and_statistics_are_right() {
        var field = new DateTimeDataField("ts", DateTimeFormat.Timestamp, unit: DateTimeTimeUnit.Micros, isNullable: false);
        long[] micros = Enumerable.Range(0, 1000).Select(i => (long)(i % 3) * 1_000_000).ToArray();
        using MemoryStream file = await WriteAsync(field, micros);
        Assert.Equal(micros, await ReadAsync<long>(file));

        // Statistics are the same as when the column is written through its logical type.
        using MemoryStream logical = await WriteAsync(field, micros.Select(m => DateTime.UnixEpoch.AddTicks(m * 10)).ToArray());
        async Task<(object? Min, object? Max)> Stats(Stream s) {
            s.Position = 0;
            await using ParquetReader reader = await ParquetReader.CreateAsync(s, leaveStreamOpen: true);
            DataColumnStatistics? st = reader.OpenRowGroupReader(0).GetStatistics(reader.Schema.DataFields[0]);
            return (st!.MinValue, st.MaxValue);
        }
        Assert.Equal(await Stats(logical), await Stats(file));
    }

    [Fact]
    public async Task Nullable_columns_take_physical_values_with_definition_levels() {
        var field = new DateTimeDataField("ts", DateTimeFormat.Timestamp, unit: DateTimeTimeUnit.Micros, isNullable: true);
        var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<long>(field, new long?[] { 5, null, 7 }.AsMemory());
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        ParquetRowGroupReader group = reader.OpenRowGroupReader(0);
        var values = new long[3];
        var defs = new int[3];
        await group.ReadRawAsync<long>(reader.Schema.DataFields[0], values, defs, null);
        Assert.Equal(new[] { 1, 0, 1 }, defs);
        Assert.Equal(new[] { 5L, 7L }, values.Take(2));
    }

    [Fact]
    public void Types_other_than_the_logical_or_physical_one_are_refused() {
        var field = new DateTimeDataField("ts", DateTimeFormat.Timestamp, unit: DateTimeTimeUnit.Micros);
        Assert.Equal(typeof(long), field.PhysicalClrType);
        Assert.Equal(typeof(int), new DateTimeDataField("d", DateTimeFormat.Date).PhysicalClrType);
        Assert.Equal(typeof(ReadOnlyMemory<byte>), new DataField<Guid>("g").PhysicalClrType);
    }

    [Theory]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(uint))]
    public async Task Unsigned_columns_written_as_their_bits_get_unsigned_bounds(System.Type clrType) {
        var field = new DataField("u", clrType, isNullable: false);
        bool wide = clrType == typeof(ulong);
        Assert.Equal(wide ? typeof(long) : typeof(int), field.PhysicalClrType);
        var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            // the largest unsigned value, as its bits, is -1: compared signed it would be the minimum
            if(wide)
                await rg.WriteAsync<long>(field, new long[] { 1, -1, 2 });
            else
                await rg.WriteAsync<int>(field, new int[] { 1, -1, 2 });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        Meta.Statistics st = reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
        Assert.Equal(1L, wide ? BitConverter.ToInt64(st.MinValue!) : BitConverter.ToInt32(st.MinValue!));
        Assert.Equal(-1L, wide ? BitConverter.ToInt64(st.MaxValue!) : BitConverter.ToInt32(st.MaxValue!));
    }
}
