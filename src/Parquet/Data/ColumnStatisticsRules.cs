using Parquet.Meta;

namespace Parquet.Data;

static class ColumnStatisticsRules {

    /// <summary>A column's sort order from its type (parquet-format LogicalTypes.md and parquet.thrift ColumnOrder).</summary>
    public static ColumnSortOrder SortOrder(SchemaElement se) {
        if(se.LogicalType?.DECIMAL != null || se.ConvertedType == ConvertedType.DECIMAL)
            return ColumnSortOrder.Signed;
        if(se.LogicalType?.INTEGER is { IsSigned: false })
            return ColumnSortOrder.Unsigned;
        if(se.ConvertedType is ConvertedType.UINT_8 or ConvertedType.UINT_16 or ConvertedType.UINT_32 or ConvertedType.UINT_64)
            return ColumnSortOrder.Unsigned;
        if(se.ConvertedType == ConvertedType.INTERVAL)
            return ColumnSortOrder.Unknown;
        if(se.LogicalType?.FLOAT16 != null)
            return ColumnSortOrder.Signed;
        return se.Type switch {
            Type.BOOLEAN or Type.INT32 or Type.INT64 or Type.FLOAT or Type.DOUBLE => ColumnSortOrder.Signed,
            Type.BYTE_ARRAY or Type.FIXED_LEN_BYTE_ARRAY => ColumnSortOrder.Unsigned,
            _ => ColumnSortOrder.Unknown,   // INT96
        };
    }
}
