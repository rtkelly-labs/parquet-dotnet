using System;
using Parquet.Encodings;
using Parquet.Meta;

namespace Parquet.Data;

/// <summary>
/// Basic statistics for data column
/// </summary>
public class DataColumnStatistics {
    /// <summary>
    /// Creates an uninitalised instance of column statistics
    /// </summary>
    public DataColumnStatistics() {

    }

    /// <summary>
    /// 
    /// </summary>
    public DataColumnStatistics(long? nullCount, long? distinctCount, object? minValue, object? maxValue) {
        NullCount = nullCount;
        DistinctCount = distinctCount;
        MinValue = minValue;
        MaxValue = maxValue;
    }

    /// <summary>
    /// Number of null values
    /// </summary>
    public long? NullCount { get; internal set; }

    /// <summary>
    /// Number of distinct values if set.
    /// </summary>
    public long? DistinctCount { get; internal set; }

    /// <summary>
    /// Minimum value, casted to CLR type
    /// </summary>
    public object? MinValue { get; internal set; }

    /// <summary>
    /// Maximum value, casted to CLR type
    /// </summary>
    public object? MaxValue { get; internal set; }

    /// <summary>
    /// Whether <see cref="MinBytes"/> and <see cref="MaxBytes"/> (and so the min and max values) are present and
    /// usable: written in the column's sort order, which is known.
    /// </summary>
    public bool HasMinMax => MinBytes != null && MaxBytes != null;

    /// <summary>The minimum as written (PLAIN encoding of the column's physical type, without a length prefix).</summary>
    public byte[]? MinBytes { get; internal set; }

    /// <summary>The maximum as written (PLAIN encoding of the column's physical type, without a length prefix).</summary>
    public byte[]? MaxBytes { get; internal set; }

    /// <summary>
    /// The minimum as a value of the column's physical type:
    /// <c>bool</c>, <c>int</c>, <c>long</c>, <c>float</c>, <c>double</c>, or <c>byte[]</c> for byte arrays and INT96.
    /// </summary>
    public object? PhysicalMin => DecodePhysical(MinBytes);

    /// <summary>The maximum as a value of the column's physical type (see <see cref="PhysicalMin"/>).</summary>
    public object? PhysicalMax => DecodePhysical(MaxBytes);

    /// <summary>The order the min and max follow, from the column's type (parquet-format "Sort Order").</summary>
    public ColumnSortOrder SortOrder { get; internal set; } = ColumnSortOrder.Unknown;

    /// <summary>
    /// The min and max came from the deprecated <c>min</c>/<c>max</c> statistics fields (older writers), which are
    /// only used when the column sorts signed.
    /// </summary>
    public bool IsLegacyMinMax { get; internal set; }

    /// <summary>Whether the minimum is a value of the column (false: a lower bound, such as a truncated string).</summary>
    public bool? IsMinExact { get; internal set; }

    /// <summary>Whether the maximum is a value of the column (false: an upper bound).</summary>
    public bool? IsMaxExact { get; internal set; }

    /// <summary>Number of NaN values in a floating-point column, if written.</summary>
    public long? NanCount { get; internal set; }

    internal Meta.Type? PhysicalType { get; set; }

    private object? DecodePhysical(byte[]? value) {
        if(value == null)
            return null;
        return PhysicalType switch {
            Meta.Type.BOOLEAN => value.Length > 0 && value[0] != 0,
            Meta.Type.INT32 => BitConverter.ToInt32(value, 0),
            Meta.Type.INT64 => BitConverter.ToInt64(value, 0),
            Meta.Type.FLOAT => BitConverter.ToSingle(value, 0),
            Meta.Type.DOUBLE => BitConverter.ToDouble(value, 0),
            _ => value,
        };
    }

    internal Statistics ToThriftStatistics(SchemaElement tse) {

        if(!ParquetPlainEncoder.TryEncode(MinValue, tse, out byte[]? min)) {
            throw new ArgumentException($"could not encode {MinValue}", nameof(MinValue));
        }

        if(!ParquetPlainEncoder.TryEncode(MaxValue, tse, out byte[]? max)) {
            throw new ArgumentException($"could not encode {MinValue}", nameof(MinValue));
        }

        // The deprecated min/max are compared signed by the readers that use them: like parquet-cpp, written only for
        // columns whose order is signed (not strings, byte arrays or unsigned integers).
        bool legacy = ColumnStatisticsRules.SortOrder(tse) == ColumnSortOrder.Signed;
        return new Statistics {
            NullCount = NullCount,
            DistinctCount = DistinctCount,
            NanCount = NanCount,
            Min = legacy ? min : null,
            MinValue = min,
            Max = legacy ? max : null,
            MaxValue = max
        };
    }
}

/// <summary>
/// How a column's values are ordered for statistics (parquet-format LogicalTypes.md, "Sort Order").
/// </summary>
public enum ColumnSortOrder {
    /// <summary>Signed comparison: numbers, dates, times, timestamps, decimals, booleans.</summary>
    Signed,

    /// <summary>Unsigned (byte-wise) comparison: strings and byte arrays, unsigned integers, UUIDs.</summary>
    Unsigned,

    /// <summary>No defined order (INT96, INTERVAL, or a column order the reader does not know): min and max are not usable.</summary>
    Unknown,
}
