using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test.Types;

/// <summary>
/// A column chunk of nulls only has no bounds in its statistics: min = max = 0 would tell a reader (and an engine
/// answering min() from statistics) that the column holds 0.
/// </summary>
public class NullOnlyChunkStatisticsTest {

    static async Task AssertNoBoundsAsync<T>(DataField field, ParquetOptions? readOptions = null) where T : struct {
        using var ms = new MemoryStream();
        await using(ParquetWriter writer = await ParquetWriter.CreateAsync(new ParquetSchema(field), ms)) {
            using ParquetRowGroupWriter rg = writer.CreateRowGroup();
            await rg.WriteAsync<T>(field, new T?[] { null, null, null });
        }
        ms.Position = 0;
        await using ParquetReader reader = await ParquetReader.CreateAsync(ms, readOptions);
        Meta.Statistics raw = reader.Metadata!.RowGroups[0].Columns[0].MetaData!.Statistics!;
        Assert.Equal(3, raw.NullCount);
        Assert.Null(raw.MinValue);
        Assert.Null(raw.MaxValue);
        Assert.Null(raw.Min);
        Assert.Null(raw.Max);
        using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(0);
        DataColumnStatistics stats = rowGroup.GetStatistics(reader.Schema.GetDataFields()[0])!;
        Assert.Equal((3L, null, null), (stats.NullCount, stats.MinValue, stats.MaxValue));
    }

    [Fact] public Task SByte() => AssertNoBoundsAsync<sbyte>(new DataField<sbyte?>("x"));
    [Fact] public Task Byte() => AssertNoBoundsAsync<byte>(new DataField<byte?>("x"));
    [Fact] public Task Int16() => AssertNoBoundsAsync<short>(new DataField<short?>("x"));
    [Fact] public Task UInt16() => AssertNoBoundsAsync<ushort>(new DataField<ushort?>("x"));
    [Fact] public Task Int32() => AssertNoBoundsAsync<int>(new DataField<int?>("x"));
    [Fact] public Task UInt32() => AssertNoBoundsAsync<uint>(new DataField<uint?>("x"));
    [Fact] public Task Int64() => AssertNoBoundsAsync<long>(new DataField<long?>("x"));
    [Fact] public Task UInt64() => AssertNoBoundsAsync<ulong>(new DataField<ulong?>("x"));
    [Fact] public Task Decimal_on_INT32() => AssertNoBoundsAsync<decimal>(new DecimalDataField("x", 9, 2, isNullable: true));
    [Fact] public Task Decimal_on_INT64() => AssertNoBoundsAsync<decimal>(new DecimalDataField("x", 18, 2, isNullable: true));
    [Fact] public Task Decimal_on_FIXED_LEN_BYTE_ARRAY() => AssertNoBoundsAsync<decimal>(new DecimalDataField("x", 38, 2, isNullable: true));
    [Fact] public Task BigInteger_on_INT96() => AssertNoBoundsAsync<BigInteger>(new DataField<BigInteger?>("x"), new ParquetOptions { TreatBigIntegersAsDates = false });
    [Fact] public Task Double() => AssertNoBoundsAsync<double>(new DataField<double?>("x"));
    [Fact] public Task Timestamp() => AssertNoBoundsAsync<DateTime>(new DateTimeDataField("x", DateTimeFormat.Timestamp, unit: DateTimeTimeUnit.Micros, isNullable: true));
    [Fact] public Task Uuid() => AssertNoBoundsAsync<Guid>(new DataField<Guid?>("x"));
}
