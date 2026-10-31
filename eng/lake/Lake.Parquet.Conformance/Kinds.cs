using System.Numerics;
using System.Text;
using Parquet;
using Parquet.Schema;

namespace Lake.Parquet.Conformance;

/// <summary>How a column's values compare in its footer statistics (parquet-format, "sort order").</summary>
public enum Order {
    /// <summary>No defined order: statistics carry no min/max.</summary>
    None,
    Signed,
    /// <summary>Unsigned integers, and byte strings compared byte by byte.</summary>
    Unsigned,
}

/// <summary>
/// One kind of leaf column: how to declare it, generate values for it, write and read them through the
/// library's raw API, and what each value is physically in the file. Values are generated per kind; the
/// shape (flat, list, struct, map) decides where they go.
/// </summary>
public abstract class LeafKind {

    public required string Name { get; init; }

    /// <summary>The order of the physical values in statistics.</summary>
    public required Order Order { get; init; }

    /// <summary>Whether the library writes min/max for it (it leaves byte-array decimals without, as parquet-cpp).</summary>
    public bool WritesBounds { get; init; } = true;

    public abstract DataField Field(string name, bool nullable);

    public abstract object Next(Random random);

    public abstract Task WriteAsync(ParquetRowGroupWriter writer, DataField field, IReadOnlyList<object> values, int[]? definitions, int[]? repetitions);

    /// <summary>Reads a column chunk back: its defined values (in order) and its levels.</summary>
    public abstract Task<(List<object> Values, int[]? Definitions, int[]? Repetitions)> ReadAsync(ParquetRowGroupReader reader, DataField field, int entries);

    /// <summary>The physical value parquet-cpp reads for one value (int, long, float, double, bool or byte[]).</summary>
    public abstract object Physical(object value, int typeLength);

    public virtual bool Same(object expected, object actual) => Equals(expected, actual);

    public override string ToString() => Name;
}

public sealed class LeafKind<T> : LeafKind where T : struct {

    public required Func<string, bool, DataField> Declare { get; init; }
    public required Func<Random, T> Generate { get; init; }
    public required Func<T, int, object> ToPhysical { get; init; }
    public Func<T, T, bool>? Equal { get; init; }

    public override DataField Field(string name, bool nullable) => Declare(name, nullable);

    public override object Next(Random random) => Generate(random);

    public override Task WriteAsync(ParquetRowGroupWriter writer, DataField field, IReadOnlyList<object> values, int[]? definitions, int[]? repetitions) =>
        // A null array would convert to an empty, non-null ReadOnlyMemory<int>?, which the writer takes as levels.
        writer.WriteAllPartsAsync<T>(field, values.Cast<T>().ToArray(),
            definitions == null ? null : (ReadOnlyMemory<int>?)definitions,
            repetitions == null ? null : (ReadOnlyMemory<int>?)repetitions, default);

    public override async Task<(List<object> Values, int[]? Definitions, int[]? Repetitions)> ReadAsync(ParquetRowGroupReader reader, DataField field, int entries) {
        if(typeof(T) == typeof(DateOnly) && field.ClrType == typeof(DateTime)) {
            // A DATE column reads back as DateTime unless ParquetOptions.UseDateOnlyTypeForDates is set.
            (List<object> dates, int[]? d, int[]? r) = await Kinds.DateAsDateTime.ReadAsync(reader, field, entries);
            return (dates.Select(v => (object)DateOnly.FromDateTime((DateTime)v)).ToList(), d, r);
        }
        var values = new T[Math.Max(entries, 1)];
        int[]? definitions = field.MaxDefinitionLevel > 0 ? new int[Math.Max(entries, 1)] : null;
        int[]? repetitions = field.MaxRepetitionLevel > 0 ? new int[Math.Max(entries, 1)] : null;
        await reader.ReadRawAsync<T>(field, values,
            definitions == null ? null : (Memory<int>?)definitions,
            repetitions == null ? null : (Memory<int>?)repetitions);
        int defined = definitions == null ? entries : definitions.Take(entries).Count(d => d == field.MaxDefinitionLevel);
        return (values.Take(defined).Select(v => (object)v).ToList(),
            definitions?.Take(entries).ToArray(), repetitions?.Take(entries).ToArray());
    }

    public override object Physical(object value, int typeLength) => ToPhysical((T)value, typeLength);

    public override bool Same(object expected, object actual) =>
        Equal != null ? Equal((T)expected, (T)actual) : base.Same(expected, actual);
}

/// <summary>Every leaf kind the harness generates.</summary>
public static class Kinds {

    static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly LeafKind<DateTime> DateAsDateTime = new() {
        Name = "date", Order = Order.Signed,
        Declare = (n, nullable) => new DateTimeDataField(n, DateTimeFormat.Date, isNullable: nullable),
        Generate = r => Instant(r, TimeSpan.TicksPerDay, DateTimeKind.Utc),
        ToPhysical = (v, _) => (int)((v.Ticks - Epoch.Ticks) / TimeSpan.TicksPerDay),
    };

    public static readonly IReadOnlyList<LeafKind> All = [
        Integer<sbyte>("int8", r => (sbyte)Pick(r, sbyte.MinValue, sbyte.MaxValue, 0, -1, r.Next(sbyte.MinValue, sbyte.MaxValue + 1)), v => (int)v, Order.Signed),
        Integer<byte>("uint8", r => (byte)Pick(r, byte.MinValue, byte.MaxValue, 1, r.Next(0, 256)), v => (int)v, Order.Unsigned),
        Integer<short>("int16", r => (short)Pick(r, short.MinValue, short.MaxValue, 0, r.Next(short.MinValue, short.MaxValue + 1)), v => (int)v, Order.Signed),
        Integer<ushort>("uint16", r => (ushort)Pick(r, ushort.MinValue, ushort.MaxValue, r.Next(0, 65536)), v => (int)v, Order.Unsigned),
        Integer<int>("int32", r => Pick(r, int.MinValue, int.MaxValue, 0, -1, r.Next()), v => v, Order.Signed),
        Integer<uint>("uint32", r => (uint)Pick(r, 0L, uint.MaxValue, 0x8000_0000L, r.NextInt64(0, 1L << 32)), v => unchecked((int)v), Order.Unsigned),
        Integer<long>("int64", r => Pick(r, long.MinValue, long.MaxValue, 0, r.NextInt64()), v => v, Order.Signed),
        Integer<ulong>("uint64", r => r.Next(4) switch { 0 => ulong.MaxValue, 1 => 1UL << 63, _ => (ulong)r.NextInt64() }, v => unchecked((long)v), Order.Unsigned),
        new LeafKind<bool> {
            Name = "bool", Order = Order.Unsigned,
            Declare = (n, nullable) => new DataField(n, typeof(bool), nullable),
            Generate = r => r.Next(2) == 0, ToPhysical = (v, _) => v,
        },
        new LeafKind<float> {
            Name = "float", Order = Order.Signed,
            Declare = (n, nullable) => new DataField(n, typeof(float), nullable),
            Generate = r => r.Next(12) switch {
                0 => float.NaN, 1 => -0.0f, 2 => 0.0f, 3 => float.MaxValue, 4 => float.MinValue, 5 => float.PositiveInfinity,
                _ => (float)(r.NextDouble() * 2e6 - 1e6),
            },
            ToPhysical = (v, _) => v, Equal = (a, b) => a.Equals(b),
        },
        new LeafKind<double> {
            Name = "double", Order = Order.Signed,
            Declare = (n, nullable) => new DataField(n, typeof(double), nullable),
            Generate = r => r.Next(12) switch {
                0 => double.NaN, 1 => -0.0, 2 => 0.0, 3 => double.Epsilon, 4 => double.NegativeInfinity,
                _ => r.NextDouble() * 2e12 - 1e12,
            },
            ToPhysical = (v, _) => v, Equal = (a, b) => a.Equals(b),
        },
        Decimal(5, 2), Decimal(18, 6), Decimal(28, 10), Decimal(10, 0, forceByteArray: true),
        new LeafKind<ReadOnlyMemory<char>> {
            Name = "string", Order = Order.Unsigned,
            Declare = (n, nullable) => new DataField(n, typeof(string), nullable),
            Generate = r => Text(r).AsMemory(),
            ToPhysical = (v, _) => Encoding.UTF8.GetBytes(v.ToString()),
            Equal = (a, b) => a.Span.SequenceEqual(b.Span),
        },
        new LeafKind<ReadOnlyMemory<byte>> {
            Name = "binary", Order = Order.Unsigned,
            Declare = (n, nullable) => new DataField(n, typeof(byte[]), nullable),
            Generate = r => Bytes(r, r.Next(0, 24)),
            ToPhysical = (v, _) => v.ToArray(), Equal = (a, b) => a.Span.SequenceEqual(b.Span),
        },
        new LeafKind<ReadOnlyMemory<byte>> {
            Name = "fixed[6]", Order = Order.Unsigned,
            Declare = (n, nullable) => new DataField(n, typeof(byte[]), nullable) { FixedLength = 6 },
            Generate = r => Bytes(r, 6),
            ToPhysical = (v, _) => v.ToArray(), Equal = (a, b) => a.Span.SequenceEqual(b.Span),
        },
        new LeafKind<Guid> {
            Name = "uuid", Order = Order.Unsigned,
            Declare = (n, nullable) => new DataField(n, typeof(Guid), nullable),
            Generate = r => r.Next(6) == 0 ? Guid.Empty : new Guid(Bytes(r, 16).ToArray()),
            ToPhysical = (v, _) => v.ToByteArray(bigEndian: true),
        },
        Timestamp(DateTimeTimeUnit.Millis, utc: true), Timestamp(DateTimeTimeUnit.Micros, utc: false), Timestamp(DateTimeTimeUnit.Nanos, utc: true),
        new LeafKind<DateTime> {
            Name = "timestamp_millis(legacy)", Order = Order.Signed,
            Declare = (n, nullable) => new DateTimeDataField(n, DateTimeFormat.DateAndTime, isNullable: nullable),
            Generate = r => Instant(r, TimeSpan.TicksPerMillisecond, DateTimeKind.Utc),
            ToPhysical = (v, _) => (v.Ticks - Epoch.Ticks) / TimeSpan.TicksPerMillisecond,
        },
        DateAsDateTime,
        new LeafKind<DateOnly> {
            Name = "dateonly", Order = Order.Signed,
            Declare = (n, nullable) => new DataField(n, typeof(DateOnly), nullable),
            Generate = r => DateOnly.FromDateTime(Instant(r, TimeSpan.TicksPerDay, DateTimeKind.Utc)),
            ToPhysical = (v, _) => v.DayNumber - DateOnly.FromDateTime(Epoch).DayNumber,
        },
        // Physical values (lake(0017)): a TIMESTAMP(NANOS) column written and read as long nanoseconds.
        new LeafKind<long> {
            Name = "timestamp_nanos(physical)", Order = Order.Signed,
            Declare = (n, nullable) => new DateTimeDataField(n, DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Nanos, isNullable: nullable),
            Generate = r => r.Next(5) == 0 ? -r.NextInt64(0, 1L << 60) : r.NextInt64(0, 1L << 62),
            ToPhysical = (v, _) => v,
        },
    ];

    static LeafKind Integer<T>(string name, Func<Random, T> generate, Func<T, object> physical, Order order) where T : struct =>
        new LeafKind<T> {
            Name = name, Order = order,
            Declare = (n, nullable) => new DataField(n, typeof(T), nullable),
            Generate = generate, ToPhysical = (v, _) => physical(v),
        };

    static LeafKind Decimal(int precision, int scale, bool forceByteArray = false) {
        BigInteger limit = BigInteger.Pow(10, precision) - 1;
        return new LeafKind<decimal> {
            Name = $"decimal({precision},{scale}){(forceByteArray ? "fixed" : "")}",
            Order = Order.Signed,
            WritesBounds = !forceByteArray && precision <= 18,
            Declare = (n, nullable) => new DecimalDataField(n, precision, scale, forceByteArray, nullable),
            Generate = r => {
                BigInteger unscaled = r.Next(5) switch {
                    0 => limit, 1 => -limit, 2 => BigInteger.Zero,
                    _ => new BigInteger(r.NextInt64()) * new BigInteger(r.NextInt64()) % (limit + 1),
                };
                return FromUnscaled(unscaled, scale);
            },
            ToPhysical = (v, typeLength) => {
                BigInteger unscaled = Unscaled(v, scale);
                if(!forceByteArray && precision <= 9) {
                    return (int)unscaled;
                }
                if(!forceByteArray && precision <= 18) {
                    return (long)unscaled;
                }
                byte[] bytes = unscaled.ToByteArray(isUnsigned: false, isBigEndian: true);
                byte[] padded = new byte[typeLength];
                byte fill = unscaled.Sign < 0 ? (byte)0xFF : (byte)0;
                Array.Fill(padded, fill, 0, typeLength - bytes.Length);
                bytes.CopyTo(padded, typeLength - bytes.Length);
                return padded;
            },
        };
    }

    /// <summary>The decimal with this unscaled value and scale, built from its bits so nothing is rounded.</summary>
    static decimal FromUnscaled(BigInteger unscaled, int scale) {
        BigInteger magnitude = BigInteger.Abs(unscaled);
        uint Word(int shift) => (uint)((magnitude >> shift) & uint.MaxValue);
        return new decimal((int)Word(0), (int)Word(32), (int)Word(64), unscaled.Sign < 0, (byte)scale);
    }

    /// <summary>The value times 10^scale, exactly.</summary>
    public static BigInteger Unscaled(decimal value, int scale) {
        int[] bits = decimal.GetBits(value);
        var magnitude = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);
        int valueScale = (bits[3] >> 16) & 0xFF;
        magnitude = valueScale <= scale ? magnitude * BigInteger.Pow(10, scale - valueScale) : magnitude / BigInteger.Pow(10, valueScale - scale);
        return bits[3] < 0 ? -magnitude : magnitude;
    }

    static LeafKind Timestamp(DateTimeTimeUnit unit, bool utc) {
        long ticksPerUnit = unit switch { DateTimeTimeUnit.Millis => TimeSpan.TicksPerMillisecond, DateTimeTimeUnit.Micros => 10, _ => 1 };
        return new LeafKind<DateTime> {
            Name = $"timestamp_{unit.ToString().ToLowerInvariant()}{(utc ? "_utc" : "")}",
            Order = Order.Signed,
            Declare = (n, nullable) => new DateTimeDataField(n, DateTimeFormat.Timestamp, isAdjustedToUTC: utc, unit: unit, isNullable: nullable),
            Generate = r => Instant(r, ticksPerUnit, utc ? DateTimeKind.Utc : DateTimeKind.Unspecified),
            // Nanoseconds are 100 ns ticks times 100: DateTime holds nothing finer.
            ToPhysical = (v, _) => unit == DateTimeTimeUnit.Nanos ? (v.Ticks - Epoch.Ticks) * 100 : (v.Ticks - Epoch.Ticks) / ticksPerUnit,
            Equal = (a, b) => a.Ticks == b.Ticks,
        };
    }

    /// <summary>An instant between 1900 and 2200, truncated to a whole number of ticks per unit.</summary>
    static DateTime Instant(Random r, long ticksPerUnit, DateTimeKind kind) {
        long from = new DateTime(1900, 1, 1).Ticks, to = new DateTime(2200, 1, 1).Ticks;
        long ticks = r.Next(8) == 0 ? Epoch.Ticks : r.NextInt64(from, to);
        long offset = (ticks - Epoch.Ticks) / ticksPerUnit * ticksPerUnit;
        return new DateTime(Epoch.Ticks + offset, kind);
    }

    static T Pick<T>(Random r, params T[] choices) => choices[r.Next(choices.Length)];

    static ReadOnlyMemory<byte> Bytes(Random r, int length) {
        byte[] bytes = new byte[length];
        r.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Short text with the characters that break naive orderings: empty, ASCII, U+E000-U+FFFF and supplementary.</summary>
    static string Text(Random r) {
        if(r.Next(10) == 0) {
            return "";
        }
        var text = new StringBuilder();
        for(int i = r.Next(1, 12); i > 0; i--) {
            text.Append(r.Next(6) switch {
                0 => char.ConvertFromUtf32(r.Next(0x10000, 0x10FFFF + 1) is var c && c is >= 0xD800 and <= 0xDFFF ? 0x1F600 : c),
                1 => ((char)r.Next(0xE000, 0xFFFE)).ToString(),
                2 => ((char)r.Next(0x80, 0x800)).ToString(),
                _ => ((char)r.Next(0x20, 0x7F)).ToString(),
            });
        }
        return text.ToString();
    }
}
