using System;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Reader;

/// <summary>FLOAT16 columns as parquet-cpp (pyarrow) writes them: FIXED_LEN_BYTE_ARRAY(2), little-endian IEEE 754 half.</summary>
public class Float16Test : TestBase {

    [Fact]
    public async Task Float16_column_reads_as_half() {
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("float16.parquet"));
        DataField field = reader.Schema.GetDataFields()[0];
        Assert.Equal(typeof(Half), field.ClrType);

        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var values = new Half?[4];
        await rowGroup.ReadAsync<Half>(field, values);
        Assert.Equal(new Half?[] { (Half)0, (Half)1.5, null, (Half)(-2) }, values);
    }

    [Fact]
    public async Task Float16_column_reads_as_its_two_bytes_through_the_physical_type() {
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("float16.parquet"));
        DataField field = reader.Schema.GetDataFields()[0];
        Assert.Equal(typeof(ReadOnlyMemory<byte>), field.PhysicalClrType);

        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var values = new ReadOnlyMemory<byte>[4];
        int[] definitions = new int[4];
        await rowGroup.ReadRawAsync<ReadOnlyMemory<byte>>(field, values, definitions, null);
        Assert.Equal(new[] { 1, 1, 0, 1 }, definitions);
        Assert.Equal((Half)1.5, BitConverter.ToHalf(values[1].Span));
    }
}
