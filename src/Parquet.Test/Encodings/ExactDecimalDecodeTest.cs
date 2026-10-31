using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Encodings;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Encodings;

/// <summary>
/// INT32/INT64-backed decimals decode to exactly the unscaled value times 10^-scale, with the column's scale, built
/// from the value's bits rather than by multiplying by a power of ten converted from double.
/// </summary>
public class ExactDecimalDecodeTest {

    [Theory]
    [InlineData(0L, 0)]
    [InlineData(1L, 18)]
    [InlineData(-1L, 18)]
    [InlineData(999_999_999_999_999_999L, 18)]
    [InlineData(-999_999_999_999_999_999L, 2)]
    [InlineData(123_456_789L, 9)]
    [InlineData(long.MaxValue, 0)]
    [InlineData(long.MinValue, 0)]
    [InlineData(long.MinValue, 28)]
    public void Unscaled_is_exact(long unscaled, int scale) {
        decimal expected = decimal.Parse(
            scale == 0 ? unscaled.ToString() : InsertPoint(unscaled.ToString(), scale),
            System.Globalization.CultureInfo.InvariantCulture);
        decimal actual = ParquetPlainEncoder.Unscaled(unscaled, scale);
        Assert.Equal(expected, actual);
        Assert.Equal(scale, actual.Scale);
    }

    static string InsertPoint(string digits, int scale) {
        bool negative = digits.StartsWith('-');
        string magnitude = (negative ? digits[1..] : digits).PadLeft(scale + 1, '0');
        return (negative ? "-" : "") + magnitude[..^scale] + "." + magnitude[^scale..];
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(9, 2)]
    [InlineData(9, 9)]
    [InlineData(18, 0)]
    [InlineData(18, 4)]
    [InlineData(18, 18)]
    public async Task Round_trips_with_the_column_scale(int precision, int scale) {
        decimal one = ParquetPlainEncoder.Unscaled(1, scale);
        decimal max = ParquetPlainEncoder.Unscaled(precision == 9 ? 999_999_999L : 999_999_999_999_999_999L, scale);
        decimal[] values = { ParquetPlainEncoder.Unscaled(0, scale), one, -one, max, -max, ParquetPlainEncoder.Unscaled(1234567, scale) };
        var field = new DecimalDataField("d", precision, scale);

        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<decimal>(field, values);
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        decimal[] read = new decimal[values.Length];
        await rowGroup.ReadAsync<decimal>(reader.Schema.GetDataFields()[0], read);

        Assert.Equal(values, read);
        Assert.All(read, d => Assert.Equal(scale, d.Scale));
        Assert.Equal(values.Select(v => v.ToString()), read.Select(v => v.ToString()));
    }
}
