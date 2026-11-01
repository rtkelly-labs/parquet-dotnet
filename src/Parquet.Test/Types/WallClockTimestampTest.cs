using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>
/// ParquetOptions.UnadjustedTimestampsAsWallClock: a TIMESTAMP that is not UTC-adjusted holds a wall-clock time, written
/// as the DateTime reads whatever its Kind and read back Unspecified; the default stays upstream's.
/// </summary>
public class WallClockTimestampTest {

    static readonly DateTime Local = new(2024, 2, 29, 12, 34, 56, DateTimeKind.Local);
    static readonly DateTime Utc = new(2024, 2, 29, 12, 34, 56, DateTimeKind.Utc);
    static readonly DateTime Unspecified = new(2024, 2, 29, 12, 34, 56, DateTimeKind.Unspecified);

    static async Task<(DateTime[] Values, long[] Stored, DataColumnStatistics Stats)> RoundTripAsync(DateTimeTimeUnit unit, DateTime[] values, bool wallClock, bool adjusted = false) {
        var field = new DateTimeDataField("t", DateTimeFormat.Timestamp, isAdjustedToUTC: adjusted, unit: unit);
        var options = new ParquetOptions { UnadjustedTimestampsAsWallClock = wallClock, WritePageIndex = true };
        options.BloomFilters["t"] = 0.01;
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<DateTime>(field, values);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms, options);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataField read = reader.Schema.GetDataFields()[0];
        var back = new DateTime[values.Length];
        await rowGroup.ReadAsync<DateTime>(read, back);
        var stored = new long[values.Length];
        await rowGroup.ReadAsync<long>(read, stored);   // the physical value
        // the bloom filter holds the stored value
        BloomFilter bloom = (await rowGroup.ReadBloomFilterAsync(read))!;
        Assert.All(stored, s => Assert.True(bloom.MightContain(s)));
        return (back, stored, rowGroup.GetStatistics(read)!);
    }

    static long Micros(DateTime wall) => (wall.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    [Fact]
    public async Task Wall_clock_values_are_stored_as_they_read_and_read_back_unspecified() {
        (DateTime[] back, long[] stored, DataColumnStatistics stats) = await RoundTripAsync(DateTimeTimeUnit.Micros, [Local, Utc, Unspecified], wallClock: true);
        Assert.All(stored, s => Assert.Equal(Micros(Unspecified), s));       // 12:34:56 whatever the Kind
        Assert.All(back, d => Assert.Equal((Unspecified, DateTimeKind.Unspecified), (d, d.Kind)));
        // statistics of a TIMESTAMP decode as its stored value: the wall clock too
        Assert.Equal((Micros(Unspecified), Micros(Unspecified)), ((long)stats.MinValue!, (long)stats.MaxValue!));
    }

    [Theory]
    [InlineData(DateTimeTimeUnit.Millis)]
    [InlineData(DateTimeTimeUnit.Nanos)]
    public async Task Every_unit(DateTimeTimeUnit unit) {
        DateTime[] back = (await RoundTripAsync(unit, [Local], wallClock: true)).Values;
        Assert.Equal((Unspecified, DateTimeKind.Unspecified), (back[0], back[0].Kind));
    }

    [Fact]
    public async Task Default_stays_upstreams() {
        (DateTime[] back, long[] stored, _) = await RoundTripAsync(DateTimeTimeUnit.Micros, [Local], wallClock: false);
        Assert.Equal(Micros(Local.ToUniversalTime()), stored[0]);            // converted to UTC
        Assert.Equal(DateTimeKind.Local, back[0].Kind);
    }

    [Fact]
    public async Task UTC_adjusted_columns_are_unchanged_by_the_option() {
        (DateTime[] back, long[] stored, _) = await RoundTripAsync(DateTimeTimeUnit.Micros, [Local], wallClock: true, adjusted: true);
        Assert.Equal(Micros(Local.ToUniversalTime()), stored[0]);
        Assert.Equal(DateTimeKind.Utc, back[0].Kind);
    }
}
