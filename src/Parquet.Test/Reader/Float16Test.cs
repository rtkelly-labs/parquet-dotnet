using System;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Reader;

/// <summary>FLOAT16 columns as parquet-cpp (pyarrow) writes them: FIXED_LEN_BYTE_ARRAY(2), little-endian IEEE 754 half.</summary>
public class Float16Test : TestBase {

    [Fact]
    public async Task Float16_column_reads_as_its_two_bytes() {
        await using ParquetReader reader = await ParquetReader.CreateAsync(OpenTestFile("float16.parquet"));
        DataField field = reader.Schema.GetDataFields()[0];
        Assert.Equal(2, field.FixedLength);

        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var values = new byte[]?[4];
        await rowGroup.ReadAsync(field, new Memory<byte[]?>(values));

        Half?[] halves = Array.ConvertAll(values, v => v == null ? (Half?)null : BitConverter.ToHalf(v));
        Assert.Equal(new Half?[] { (Half)0, (Half)1.5, null, (Half)(-2) }, halves);
    }
}
