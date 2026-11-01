using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Meta;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>
/// .NET types with a matching Parquet type (Lake.NET decision 9): System.Half for FLOAT16, TimeOnly and TimeSpan for
/// TIME. Defaults stay lossless and as upstream reads them; TIME as TimeOnly or TimeSpan is asked for.
/// </summary>
public class NativeTypesTest : TestBase {

    static async Task<MemoryStream> WriteAsync<T>(DataField field, T?[] values) where T : struct {
        var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<T>(field, values);
        }
        ms.Position = 0;
        return ms;
    }

    static async Task<(T?[] Values, DataField Field, DataColumnStatistics? Stats, SchemaElement Se)> ReadAsync<T>(Stream file, int n, ParquetOptions? options = null) where T : struct {
        await using ParquetReader reader = await ParquetReader.CreateAsync(file, options);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField field = reader.Schema.GetDataFields()[0];
        var values = new T?[n];
        await rowGroup.ReadAsync<T>(field, values);
        return (values, field, rowGroup.GetStatistics(field), reader.Metadata!.Schema[1]);
    }

    // ---- Half ----

    [Fact]
    public async Task Half_round_trips_as_FLOAT16() {
        Half?[] values = [(Half)1.5, null, Half.NaN, -(Half)0, Half.MaxValue, Half.MinValue, Half.Epsilon, Half.PositiveInfinity, (Half)(-2.25)];
        using MemoryStream file = await WriteAsync(new DataField<Half?>("h"), values);
        (Half?[] read, DataField field, DataColumnStatistics? stats, SchemaElement se) = await ReadAsync<Half>(file, values.Length);

        Assert.Equal((Meta.Type.FIXED_LEN_BYTE_ARRAY, 2), (se.Type!.Value, se.TypeLength!.Value));
        Assert.NotNull(se.LogicalType?.FLOAT16);
        Assert.Equal(typeof(Half), field.ClrType);
        Assert.Equal(values.Select(v => v.HasValue ? BitConverter.HalfToInt16Bits(v.Value) : (short?)null),
            read.Select(v => v.HasValue ? BitConverter.HalfToInt16Bits(v.Value) : (short?)null));   // bit for bit, -0 and NaN included
        // NaN is no bound, but counted; bounds are the half values
        Assert.Equal((Half.MinValue, Half.PositiveInfinity, 1L, 1L), ((Half)stats!.MinValue!, (Half)stats.MaxValue!, stats.NanCount, stats.NullCount));
    }

    [Fact]
    public async Task Half_zero_bounds_are_signed_zeros() {
        using MemoryStream file = await WriteAsync(new DataField<Half>("h"), new Half?[] { (Half)0, (Half)0 });
        DataColumnStatistics stats = (await ReadAsync<Half>(file, 2)).Stats!;
        Assert.True(Half.IsNegative((Half)stats.MinValue!));
        Assert.False(Half.IsNegative((Half)stats.MaxValue!));
    }

    // ---- TIME ----

    public static TheoryData<TimeUnitPrecision, long> Units => new() {
        { TimeUnitPrecision.Millis, TimeSpan.TicksPerMillisecond },
        { TimeUnitPrecision.Micros, TimeSpan.TicksPerMicrosecond },
        { TimeUnitPrecision.Nanos, 1 },
    };

    static TimeOnly[] Times(long tick) => [
        TimeOnly.MinValue, new TimeOnly(12, 34, 56, 789), new TimeOnly((TimeSpan.TicksPerDay - 1) / tick * tick), new TimeOnly(1_234_567_891 / tick * tick)];

    [Theory]
    [MemberData(nameof(Units))]
    public async Task TimeOnly_round_trips_and_reads_as_the_units_integer_by_default(TimeUnitPrecision unit, long tick) {
        TimeOnly?[] values = [.. Times(tick).Select(t => (TimeOnly?)t), null];
        using MemoryStream file = await WriteAsync(new TimeDataField("t", unit, typeof(TimeOnly), isNullable: true), values);

        (TimeOnly?[] read, DataField field, DataColumnStatistics? stats, SchemaElement se) =
            await ReadAsync<TimeOnly>(file, values.Length, new ParquetOptions { TimeMapping = TimeMapping.TimeOnly });
        Assert.Equal(values, read);
        Assert.Equal(typeof(TimeOnly), field.ClrType);
        Assert.Equal(unit, ((TimeDataField)field).Precision);
        Assert.Equal((TimeOnly.MinValue, values.Where(v => v.HasValue).Max()), ((TimeOnly)stats!.MinValue!, (TimeOnly)stats.MaxValue!));
        Assert.NotNull(se.LogicalType?.TIME);

        // without the mapping: the unit's integer, as upstream reads TIME
        file.Position = 0;
        if(unit == TimeUnitPrecision.Millis) {
            int?[] ints = (await ReadAsync<int>(file, values.Length)).Values;
            Assert.Equal(values.Select(v => v.HasValue ? (int?)(v.Value.Ticks / TimeSpan.TicksPerMillisecond) : null), ints);
        } else {
            long?[] longs = (await ReadAsync<long>(file, values.Length)).Values;
            Assert.Equal(values.Select(v => v.HasValue ? (long?)(unit == TimeUnitPrecision.Micros ? v.Value.Ticks / 10 : v.Value.Ticks * 100) : null), longs);
        }
    }

    [Theory]
    [MemberData(nameof(Units))]
    public async Task TimeSpan_round_trips_as_a_time_of_day(TimeUnitPrecision unit, long tick) {
        TimeSpan?[] values = [.. Times(tick).Select(t => (TimeSpan?)t.ToTimeSpan()), null];
        using MemoryStream file = await WriteAsync(new TimeDataField("t", unit, typeof(TimeSpan), isNullable: true), values);
        (TimeSpan?[] read, DataField field, _, _) = await ReadAsync<TimeSpan>(file, values.Length, new ParquetOptions { TimeMapping = TimeMapping.TimeSpan });
        Assert.Equal(values, read);
        Assert.Equal(typeof(TimeSpan), field.ClrType);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(TimeSpan.TicksPerDay)]
    public async Task TimeSpan_outside_a_day_is_refused(long ticks) {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            WriteAsync(new TimeDataField("t", TimeUnitPrecision.Micros, typeof(TimeSpan)), new TimeSpan?[] { new TimeSpan(ticks) }));
    }

    [Fact]
    public void TimeDataField_takes_only_time_types() {
        Assert.Throws<ArgumentException>(() => new TimeDataField("t", TimeUnitPrecision.Micros, typeof(int)));
        Assert.Equal(typeof(long), new TimeDataField("t", TimeUnitPrecision.Micros, typeof(long)).ClrType);
        // DataField<TimeOnly> is upstream's, unchanged: a plain long
        Assert.Equal(typeof(long), new DataField<TimeOnly>("t").ClrType);
    }

    [Fact]
    public async Task Legacy_TIME_MILLIS_reads_as_TimeOnly_when_asked() {
        // the converted type alone, as older writers wrote it
        var field = new RawDataField("t", Meta.Type.INT32, false, convertedType: ConvertedType.TIME_MILLIS);
        using MemoryStream file = await WriteAsync(field, new int?[] { 0, 45_296_789 });
        TimeOnly?[] read = (await ReadAsync<TimeOnly>(file, 2, new ParquetOptions { TimeMapping = TimeMapping.TimeOnly })).Values;
        Assert.Equal(new TimeOnly?[] { TimeOnly.MinValue, new TimeOnly(12, 34, 56, 789) }, read);
    }

    [Fact]
    public async Task Pyarrow_times_and_halves_read_natively() {
        // data/time_and_half.parquet, pyarrow 26: time32[ms], time64[us], time64[ns] (123 ns past each microsecond), float16
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("time_and_half.parquet"), new ParquetOptions { TimeMapping = TimeMapping.TimeOnly });
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField[] f = reader.Schema.GetDataFields();
        TimeOnly?[] Read(TimeOnly?[] v) => v;
        var ms = new TimeOnly?[4]; var us = new TimeOnly?[4]; var ns = new TimeOnly?[4]; var h = new Half?[4];
        await rowGroup.ReadAsync<TimeOnly>(f[0], ms);
        await rowGroup.ReadAsync<TimeOnly>(f[1], us);
        await rowGroup.ReadAsync<TimeOnly>(f[2], ns);
        await rowGroup.ReadAsync<Half>(f[3], h);
        Assert.Equal(new TimeOnly?[] { TimeOnly.MinValue, new TimeOnly(12, 34, 56, 789), null, new TimeOnly(23, 59, 59, 999) }, Read(ms));
        Assert.Equal(new TimeOnly?[] { TimeOnly.MinValue, new TimeOnly(12, 34, 56, 789), null, new TimeOnly(23, 59, 59, 999, 999) }, Read(us));
        // nanoseconds rounded down to 100 ns ticks: the 123 ns become 100
        Assert.Equal(new TimeOnly(1), ns[0]);
        Assert.Equal(new TimeOnly(23, 59, 59, 999, 999).Add(TimeSpan.FromTicks(1)), ns[3]);
        Assert.Equal(new Half?[] { (Half)1.5, -(Half)0, null, Half.MaxValue }, h);
    }
}
