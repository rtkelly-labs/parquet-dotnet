using System;
using System.Linq;
using Parquet.Encodings;
using Parquet.Meta;
using Xunit;

namespace Parquet.Test.Encodings;

/// <summary>
/// INT32/INT64-backed decimals are encoded from their bits when the value has no more decimal places than the column
/// and fits; the result is what decimal arithmetic gives (value times 10^scale, truncated) for every value.
/// </summary>
public class DecimalEncodeTest {

    static readonly SchemaElement Tse = new() { Type = Meta.Type.INT64, Scale = 4, Precision = 18 };

    static long Reference(decimal value, int scale) => (long)(value * (decimal)Math.Pow(10, scale));

    [Theory]
    [InlineData("0", 2)]
    [InlineData("-0.00", 2)]
    [InlineData("123.45", 2)]
    [InlineData("-123.45", 4)]
    [InlineData("123.456789", 2)]          // more places than the column: truncated, as before
    [InlineData("-123.456789", 2)]
    [InlineData("1", 18)]
    [InlineData("-9.223372036854775807", 18)]
    [InlineData("922337203685477580.7", 1)]
    [InlineData("79228162514264337593543950335", 0)]   // does not fit
    public void Matches_decimal_arithmetic(string text, int scale) {
        decimal value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        long expected;
        try {
            expected = Reference(value, scale);
        } catch(OverflowException) {
            Assert.Throws<ParquetException>(() => ParquetPlainEncoder.ToUnscaled(value, scale, Tse));
            return;
        }
        Assert.Equal(expected, ParquetPlainEncoder.ToUnscaled(value, scale, Tse));
    }

    [Fact]
    public void Matches_decimal_arithmetic_for_random_values() {
        var r = new Random(7);
        foreach(int _ in Enumerable.Range(0, 100_000)) {
            int scale = r.Next(0, 19);
            decimal value = new decimal(r.Next(), r.Next(), 0, r.Next(2) == 0, (byte)r.Next(0, 19));
            long expected;
            try {
                expected = Reference(value, scale);
            } catch(OverflowException) {
                Assert.Throws<ParquetException>(() => ParquetPlainEncoder.ToUnscaled(value, scale, Tse));
                continue;
            }
            Assert.Equal(expected, ParquetPlainEncoder.ToUnscaled(value, scale, Tse));
        }
    }
}
