namespace Parquet;

/// <summary>
/// The CLR type TIME columns are read as (<see cref="ParquetOptions.TimeMapping"/>). Whatever the mapping, the values
/// are the same instants: a time of day, in the column's unit.
/// </summary>
public enum TimeMapping {
    /// <summary>The unit's integer: <c>int</c> milliseconds, <c>long</c> micro- or nanoseconds. Lossless; the default.</summary>
    Integer = 0,

    /// <summary><see cref="System.TimeOnly"/>. Nanoseconds are rounded down to 100 ns ticks.</summary>
    TimeOnly,

    /// <summary><see cref="System.TimeSpan"/> since midnight. Nanoseconds are rounded down to 100 ns ticks.</summary>
    TimeSpan,
}
