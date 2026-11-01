using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>A TIMESTAMP in nanoseconds holds 1677-09-21 .. 2262-04-11: a DateTime outside it is refused, not wrapped.</summary>
public class TimestampNanosRangeTest {

    static async Task<DateTime> RoundTripAsync(DateTime value, DateTimeTimeUnit unit) {
        var field = new DateTimeDataField("t", DateTimeFormat.Timestamp, unit: unit);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<DateTime>(field, new[] { value });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var read = new DateTime[1];
        await rowGroup.ReadAsync<DateTime>(reader.Schema.GetDataFields()[0], read);
        return read[0];
    }

    [Theory]
    [InlineData(1600, 1, 1)]   // read back as 2184-07-20 before the change
    [InlineData(9999, 12, 31)]
    [InlineData(1677, 9, 20)]
    [InlineData(2262, 4, 12)]
    public async Task Out_of_range_is_refused(int y, int m, int d) {
        ArgumentOutOfRangeException e = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RoundTripAsync(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc), DateTimeTimeUnit.Nanos));
        Assert.Contains("1677-09-21", e.Message);
    }

    [Theory]
    [InlineData("1677-09-21T00:12:44Z")]
    [InlineData("2262-04-11T23:47:16Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    [InlineData("1969-12-31T23:59:59.9999999Z")]
    public async Task The_range_round_trips(string text) {
        DateTime value = DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(value, await RoundTripAsync(value, DateTimeTimeUnit.Nanos));
    }

    [Fact]
    public async Task Microseconds_hold_every_DateTime() {
        DateTime value = new(1600, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(value, await RoundTripAsync(value, DateTimeTimeUnit.Micros));
    }
}
