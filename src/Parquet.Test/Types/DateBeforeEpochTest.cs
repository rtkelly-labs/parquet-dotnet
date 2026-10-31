using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>DATE columns written from DateTime keep the value's date, whatever its time of day and on either side of 1970.</summary>
public class DateBeforeEpochTest {

    [Theory]
    [InlineData("1969-07-20T20:17:40", "1969-07-20")]   // before the epoch, after midday: truncating toward zero gave the next day
    [InlineData("1969-12-31T00:00:01", "1969-12-31")]
    [InlineData("2024-02-29T23:59:59.9999999", "2024-02-29")]   // a double day count rounds up to the next day
    [InlineData("2024-02-29T12:00:00", "2024-02-29")]
    [InlineData("1900-01-01T06:00:00", "1900-01-01")]
    public async Task DateTime_written_as_DATE_keeps_its_date(string value, string date) {
        var field = new DateTimeDataField("d", DateTimeFormat.Date);
        DateTime written = DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<DateTime>(field, new[] { written });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var read = new DateTime[1];
        await rowGroup.ReadAsync<DateTime>(reader.Schema.GetDataFields()[0], read);
        Assert.Equal(DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture), read[0].Date);
    }
}
