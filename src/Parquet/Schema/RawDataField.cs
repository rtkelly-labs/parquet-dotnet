using System;
using Parquet.Meta;
using Type = System.Type;

namespace Parquet.Schema;

/// <summary>
/// A column declared exactly as stored: its physical type and the annotations to write with it (logical type,
/// converted type, length, precision and scale), with no mapping from a CLR type. Values are read and written as the
/// physical type (<see cref="DataField.PhysicalClrType"/>): <c>bool</c>, <c>int</c>, <c>long</c>, <c>float</c>,
/// <c>double</c>, or bytes; text columns (STRING, ENUM, JSON) also as strings. For table formats (Iceberg, DuckLake) whose schemas say exactly how each column is stored.
/// </summary>
public sealed class RawDataField : DataField {

    /// <summary>
    /// Declares a column. For FIXED_LEN_BYTE_ARRAY, <paramref name="typeLength"/> is required; for decimals pass a
    /// <see cref="LogicalType"/> with <c>DECIMAL</c> set (precision and scale are copied to the schema element).
    /// </summary>
    public RawDataField(string name, Meta.Type physicalType, bool isNullable,
        LogicalType? logicalType = null, ConvertedType? convertedType = null, int? typeLength = null)
        : base(name, ClrTypeOf(physicalType, logicalType, convertedType), isNullable, isArray: false) {
        if(physicalType == Meta.Type.FIXED_LEN_BYTE_ARRAY && typeLength is not > 0)
            throw new ArgumentException("FIXED_LEN_BYTE_ARRAY needs a type length", nameof(typeLength));
        PhysicalType = physicalType;
        LogicalType = logicalType;
        ConvertedType = convertedType;
        if(physicalType == Meta.Type.FIXED_LEN_BYTE_ARRAY)
            FixedLength = typeLength;
    }

    /// <summary>The physical type.</summary>
    public Meta.Type PhysicalType { get; }

    /// <summary>The logical type annotation written with the column, if any.</summary>
    public LogicalType? LogicalType { get; }

    /// <summary>The legacy converted type written with the column, if any.</summary>
    public ConvertedType? ConvertedType { get; }

    private static Type ClrTypeOf(Meta.Type physicalType, LogicalType? logical, ConvertedType? converted) => physicalType switch {
        // text: also written from strings (and dictionary-encoded as strings are)
        Meta.Type.BYTE_ARRAY when logical?.STRING != null || logical?.ENUM != null || logical?.JSON != null
            || converted is Meta.ConvertedType.UTF8 or Meta.ConvertedType.ENUM or Meta.ConvertedType.JSON => typeof(string),
        Meta.Type.BOOLEAN => typeof(bool),
        Meta.Type.INT32 => typeof(int),
        Meta.Type.INT64 => typeof(long),
        Meta.Type.FLOAT => typeof(float),
        Meta.Type.DOUBLE => typeof(double),
        Meta.Type.BYTE_ARRAY or Meta.Type.FIXED_LEN_BYTE_ARRAY => typeof(byte[]),
        _ => throw new NotSupportedException($"physical type {physicalType} cannot be declared")
    };

    /// <summary>The schema element this column is written with (name, repetition and field ID set by the writer).</summary>
    internal SchemaElement ToSchemaElement() {
        var se = new SchemaElement {
            Name = Name,
            Type = PhysicalType,
            LogicalType = LogicalType,
            ConvertedType = ConvertedType,
            TypeLength = PhysicalType == Meta.Type.FIXED_LEN_BYTE_ARRAY ? FixedLength : null,
        };
        if(LogicalType?.DECIMAL is DecimalType d) {
            se.Precision = d.Precision;
            se.Scale = d.Scale;
            se.ConvertedType ??= Meta.ConvertedType.DECIMAL;
        }
        if(FieldId != -1)
            se.FieldId = FieldId;
        return se;
    }
}
