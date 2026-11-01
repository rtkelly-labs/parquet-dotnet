using System.Diagnostics.CodeAnalysis;
using System;

namespace Parquet.Schema;

/// <summary>
/// Represents TIME logical type, which is a time of day since midnight. See
/// https://github.com/apache/parquet-format/blob/master/LogicalTypes.md#time
/// Maps to either <see cref="int"/> or <see cref="long"/> data type.
/// </summary>
public class TimeDataField : DataField {
    /// <summary>
    /// Time data field
    /// </summary>
    /// <param name="name"></param>
    /// <param name="precision"></param>
    /// <param name="isNullable"></param>
    /// <param name="isArray"></param>
    /// <param name="propertyName"></param>
    public TimeDataField(string name, TimeUnitPrecision precision, bool? isNullable = null, bool? isArray = null, string? propertyName = null)
        : base(name, GetUnitType(precision), isNullable, isArray, propertyName) =>
        Precision = precision;

    /// <summary>
    /// A TIME column whose values are <paramref name="clrType"/>: <see cref="TimeOnly"/> or <see cref="TimeSpan"/> (a time
    /// of day, 0 to 24 hours), or the unit's integer (<c>int</c> milliseconds, <c>long</c> micro- or nanoseconds). .NET
    /// times are 100 ns ticks: nanoseconds are written as ticks times 100 and read rounded down to a tick.
    /// </summary>
    public TimeDataField(string name, TimeUnitPrecision precision,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type clrType,
        bool? isNullable = null, bool? isArray = null, string? propertyName = null)
        : base(name, CheckClrType(precision, clrType), isNullable, isArray, propertyName) =>
        Precision = precision;

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
    private static Type CheckClrType(TimeUnitPrecision precision,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type clrType) =>
        clrType == typeof(TimeOnly) || clrType == typeof(TimeSpan) || clrType == GetUnitType(precision)
            ? clrType
            : throw new ArgumentException($"a TIME column in {precision} is TimeOnly, TimeSpan or {GetUnitType(precision).Name}, not {clrType.Name}", nameof(clrType));

    /// <summary>
    /// Indicates if the time is adjusted to UTC.
    /// </summary>
    public bool IsAdjustedToUtc { get; set; } = true;
    
    /// <summary>
    /// Time precision.
    /// </summary>
    public TimeUnitPrecision Precision { get; }
    
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
    private static Type GetUnitType(TimeUnitPrecision precision) =>
        precision switch {
            TimeUnitPrecision.Millis => typeof(int),
            TimeUnitPrecision.Micros or TimeUnitPrecision.Nanos => typeof(long),
            _ => throw new ArgumentOutOfRangeException(nameof(precision), precision, null)
        };
}