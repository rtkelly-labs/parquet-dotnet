using System;
using System.IO;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>
/// A DECIMAL read as System.Decimal is exact or refused, never rounded (Lake.NET decision 9).
/// </summary>
public class DecimalPrecisionTest : TestBase {

    // data/decimal_38_9.parquet, pyarrow 26, decimal128(38, 9) as FIXED_LEN_BYTE_ARRAY(16):
    //   d38:      1234567890123456789.123456789, -0.000000001, null, -1234567890123456789.123456789, 99999999999999999999999999999.999999999
    //   d38_fits: the first four, then 0

    async Task<decimal?[]> ReadAsync(string column, ParquetOptions? options = null) {
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("decimal_38_9.parquet"), options);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var values = new decimal?[5];
        await rowGroup.ReadAsync<decimal>(reader.Schema.FindDataField(column), values);
        return values;
    }

    [Fact]
    public async Task Values_that_fit_read_exactly() {
        decimal?[] values = await ReadAsync("d38_fits");
        Assert.Equal(new decimal?[] { 1234567890123456789.123456789m, -0.000000001m, null, -1234567890123456789.123456789m, 0m }, values);
    }

    [Fact]
    public async Task A_value_beyond_decimal_is_refused_and_points_to_BigDecimal() {
        OverflowException e = await Assert.ThrowsAsync<OverflowException>(() => ReadAsync("d38"));
        Assert.Contains(nameof(ParquetOptions.UseBigDecimal), e.Message);
    }

    [Fact]
    public async Task Digits_beyond_decimal_are_refused_not_rounded() {
        // 29 integer digits leave no room for the fraction: upstream read 12345678901234567890123456789, the fraction rounded away
        var field = new BigDecimalDataField("d", 38, 9);
        var value = new BigDecimal(System.Numerics.BigInteger.Parse("12345678901234567890123456789123456789"), 38, 9);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<BigDecimal>(field, new[] { value });
        }

        ms.Position = 0;
        await using(ParquetReader reader = await ParquetReader.CreateAsync(ms)) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            await Assert.ThrowsAsync<OverflowException>(async () => await rowGroup.ReadAsync<decimal>(reader.Schema.GetDataFields()[0], new decimal[1]));
        }

        ms.Position = 0;
        await using(ParquetReader reader = await ParquetReader.CreateAsync(ms, new ParquetOptions { UseBigDecimal = true })) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
            var read = new BigDecimal[1];
            await rowGroup.ReadAsync<BigDecimal>(reader.Schema.GetDataFields()[0], read);
            Assert.Equal(value.UnscaledValue, read[0].UnscaledValue);
        }
    }

    [Theory]
    [InlineData("79228162514264337593543950335")]      // decimal.MaxValue, 96 bits
    [InlineData("-79228162514264337593543950335")]
    [InlineData("7.9228162514264337593543950335")]     // 96 bits at scale 28
    [InlineData("0.0000000000000000000000000001")]     // the smallest scale-28 step
    [InlineData("12.5")]
    public async Task Decimal_edges_round_trip_through_a_wide_column(string text) {
        decimal d = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var field = new DecimalDataField("d", 38, 28);
        if(Math.Abs(d) >= 1e10m)
            field = new DecimalDataField("d", 38, 0);
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<decimal>(field, new[] { d });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var read = new decimal[1];
        await rowGroup.ReadAsync<decimal>(reader.Schema.GetDataFields()[0], read);
        Assert.Equal(d, read[0]);
    }
}
