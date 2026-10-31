using System;
using System.Linq;
using System.Runtime.InteropServices;
using Parquet.Encodings;
using Parquet.Meta;
using Xunit;

namespace Parquet.Test.Encodings;

/// <summary>
/// INT64 timestamps decode, in every unit, to the DateTime and kind that epoch.AddMilliseconds(ms).AddTicks(rest)
/// then SpecifyKind gave, before and after the epoch.
/// </summary>
public class TimestampDecodeTest {

    static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly long[] Values = { 0, 1, -1, 999, -999, 1_000, -1_001, 1_700_000_000_123_456, -2_208_988_800_000_001, 123_456_789_012 };

    public static TheoryData<string, bool> Units => new() {
        { "millis", true }, { "millis", false }, { "micros", true }, { "micros", false }, { "nanos", true }, { "nanos", false } };

    [Theory]
    [MemberData(nameof(Units))]
    public void Logical_timestamps(string unit, bool utc) {
        var tse = new SchemaElement {
            Type = Meta.Type.INT64,
            LogicalType = new LogicalType {
                TIMESTAMP = new TimestampType {
                    IsAdjustedToUTC = utc,
                    Unit = unit switch {
                        "millis" => new TimeUnit { MILLIS = new MilliSeconds() },
                        "micros" => new TimeUnit { MICROS = new MicroSeconds() },
                        _ => new TimeUnit { NANOS = new NanoSeconds() }
                    }
                }
            }
        };
        long[] values = unit == "millis" ? Values.Select(v => v / 1000).ToArray() : Values;
        DateTime[] expected = values.Select(v => DateTime.SpecifyKind(unit switch {
            "millis" => Epoch.AddMilliseconds(v),
            "micros" => Epoch.AddMilliseconds(v / 1000).AddTicks(v % 1000 * 10),
            _ => Epoch.AddMilliseconds(v / 1_000_000).AddTicks(v % 1_000_000 / 100)
        }, utc ? DateTimeKind.Utc : DateTimeKind.Local)).ToArray();

        DateTime[] actual = Decode(values, tse);
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Select(d => d.Kind), actual.Select(d => d.Kind));
    }

    [Fact]
    public void Legacy_converted_types() {
        var micros = new SchemaElement { Type = Meta.Type.INT64, ConvertedType = ConvertedType.TIMESTAMP_MICROS };
        var millis = new SchemaElement { Type = Meta.Type.INT64, ConvertedType = ConvertedType.TIMESTAMP_MILLIS };
        Assert.Equal(Values.Select(v => Epoch.AddMilliseconds(v / 1000).AddTicks(v % 1000 * 10)), Decode(Values, micros));
        long[] ms = Values.Select(v => v / 1000).ToArray();
        Assert.Equal(ms.Select(v => Epoch.AddMilliseconds(v)), Decode(ms, millis));
        Assert.All(Decode(Values, micros), d => Assert.Equal(DateTimeKind.Utc, d.Kind));
    }

    static DateTime[] Decode(long[] values, SchemaElement tse) {
        byte[] bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var data = new DateTime[values.Length];
        Assert.Equal(values.Length, ParquetPlainEncoder.Decode(bytes, data, tse));
        return data;
    }
}
