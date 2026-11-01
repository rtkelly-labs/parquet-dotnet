using System;
using System.IO;
using Parquet.Encodings;
using Xunit;

namespace Parquet.Test.Encodings;

/// <summary>
/// RLE runs of values four bytes wide (bit widths 25 to 32: dictionary indexes of 2^24 and more) round-trip; the
/// top byte was written from <c>value &gt;&gt; 32</c>, which C# takes as <c>value &gt;&gt; 0</c>.
/// </summary>
public class RleWideValuesTest {

    [Theory]
    [InlineData(25, new[] { 1 << 24, 1 << 24, 1 << 24, (1 << 24) + 5 })]
    [InlineData(31, new[] { int.MaxValue, int.MaxValue, 0x12345678, 5, 5 })]
    [InlineData(32, new[] { -1, -1, int.MinValue, 0x7F00FF01 })]
    public void Four_byte_values_round_trip(int bitWidth, int[] values) {
        using var ms = new MemoryStream();
        RleBitpackedHybridEncoder.Encode(ms, values.AsSpan(), bitWidth);
        int[] back = new int[values.Length];
        RleBitpackedHybridEncoder.Decode(ms.ToArray().AsSpan(), bitWidth, (int)ms.Length, out _, back.AsSpan(), back.Length);
        Assert.Equal(values, back);
    }
}
