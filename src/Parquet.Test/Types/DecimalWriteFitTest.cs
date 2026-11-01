using System.IO;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>
/// A decimal is written only if its integer part fits its column's DECIMAL(precision, scale): a wider value would make
/// a file that breaks its own schema. Digits beyond the scale are truncated, as upstream does.
/// </summary>
public class DecimalWriteFitTest {

    static async Task<decimal> RoundTripAsync(DecimalDataField field, decimal value) {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<decimal>(field, new[] { value });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var read = new decimal[1];
        await rowGroup.ReadAsync<decimal>(reader.Schema.GetDataFields()[0], read);
        return read[0];
    }

    [Theory]
    [InlineData(5, 2, "12345.6")]      // INT32: seven digits in a five-digit column, written before the change
    [InlineData(5, 2, "1000")]
    [InlineData(12, 2, "12345678901")] // INT64
    [InlineData(20, 2, "1e18")]        // FIXED_LEN_BYTE_ARRAY
    [InlineData(30, 30, "1")]
    public async Task Too_many_integer_digits_are_refused(int precision, int scale, string value) {
        decimal d = decimal.Parse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
        await Assert.ThrowsAsync<ParquetException>(() => RoundTripAsync(new DecimalDataField("d", precision, scale), d));
    }

    [Theory]
    [InlineData(9, 2, "1.239", "1.23")]   // upstream truncates digits beyond the scale, and tests it; unchanged
    [InlineData(20, 2, "-1.239", "-1.23")]
    public async Task Digits_beyond_the_scale_are_truncated_as_upstream(int precision, int scale, string value, string expected) {
        decimal d = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), await RoundTripAsync(new DecimalDataField("d", precision, scale), d));
    }

    [Theory]
    [InlineData(5, 2, "999.99")]
    [InlineData(5, 2, "-999.99")]
    [InlineData(5, 2, "1.2300")]       // trailing zeros are not digits lost
    [InlineData(9, 0, "999999999")]
    [InlineData(18, 4, "99999999999999.9999")]
    [InlineData(38, 18, "79228162514.264337593543950335")]
    [InlineData(38, 9, "0")]
    public async Task Values_that_fit_round_trip(int precision, int scale, string value) {
        decimal d = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(d, await RoundTripAsync(new DecimalDataField("d", precision, scale), d));
    }
}
