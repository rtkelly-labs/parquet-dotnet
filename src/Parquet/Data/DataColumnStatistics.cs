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

    /// <summary>Number of NaN values (floating point columns only), written as nan_count.</summary>
    internal long? NanCount { get; set; }

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
