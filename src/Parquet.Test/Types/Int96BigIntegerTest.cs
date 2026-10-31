using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>INT96 as BigInteger (TreatBigIntegersAsDates off): 12 little-endian two's complement bytes per value.</summary>
public class Int96BigIntegerTest {

    [Fact]
    public async Task BigInteger_column_round_trips_as_INT96() {
        var field = new DataField<BigInteger>("i96");
        BigInteger[] values = [0, 1, -1, BigInteger.Pow(2, 90), -BigInteger.Pow(2, 95)];
        var options = new ParquetOptions { TreatBigIntegersAsDates = false };
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms, options)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<BigInteger>(field, values);
        }
        byte[] file = ms.ToArray();

        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file), options);
        Assert.Equal(Meta.Type.INT96, reader.Metadata!.Schema[1].Type);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        var read = new BigInteger[values.Length];
        await rowGroup.ReadAsync<BigInteger>(reader.Schema.GetDataFields()[0], read);
        Assert.Equal(values, read);
    }
}
