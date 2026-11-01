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
    /// <summary>FLOAT16: FIXED_LEN_BYTE_ARRAY(2) compared as the half it holds (NaN no bound, a zero min -0, a zero max +0).</summary>
    Float16,
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
        if((typeof(T) == typeof(TimeOnly) || typeof(T) == typeof(TimeSpan)) && field is TimeDataField time && field.ClrType != typeof(T)) {
            // TIME reads back as its unit's integer unless ParquetOptions.TimeMapping says otherwise
            (List<object> raw, int[]? d, int[]? r) = time.Precision == TimeUnitPrecision.Millis
                ? await Kinds.TimeMillisAsInt.ReadAsync(reader, field, entries)
                : await Kinds.TimeAsLong.ReadAsync(reader, field, entries);
            long Ticks(object v) => time.Precision switch {
                TimeUnitPrecision.Millis => (int)v * TimeSpan.TicksPerMillisecond,
                TimeUnitPrecision.Micros => (long)v * TimeSpan.TicksPerMicrosecond,
                _ => (long)v / TimeSpan.NanosecondsPerTick,
            };
            return (raw.Select(v => typeof(T) == typeof(TimeOnly) ? (object)new TimeOnly(Ticks(v)) : new TimeSpan(Ticks(v))).ToList(), d, r);
        }
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

    // TIME columns read as their unit's integer (the default), for kinds that write TimeOnly or TimeSpan
    internal static readonly LeafKind<int> TimeMillisAsInt = new() {
        Name = "time_millis(int)", Order = Order.Signed,
        Declare = (n, nullable) => new TimeDataField(n, TimeUnitPrecision.Millis, nullable),
        Generate = r => r.Next(0, 86_400_000), ToPhysical = (v, _) => v,
    };
    internal static readonly LeafKind<long> TimeAsLong = new() {
        Name = "time_micros(long)", Order = Order.Signed,
        Declare = (n, nullable) => new TimeDataField(n, TimeUnitPrecision.Micros, nullable),
        Generate = r => r.NextInt64(0, 86_400_000_000), ToPhysical = (v, _) => v,
    };

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
        // Native .NET types (Lake.NET decision 9): FLOAT16 as Half, TIME as TimeOnly and TimeSpan in each unit.
        new LeafKind<Half> {
            Name = "float16", Order = Order.Float16,
            Declare = (n, nullable) => new DataField(n, typeof(Half), nullable),
            Generate = r => r.Next(10) switch {
                0 => Half.NaN, 1 => (Half)0, 2 => -(Half)0, 3 => Half.MaxValue, 4 => Half.MinValue, 5 => Half.Epsilon,
                6 => Half.PositiveInfinity, _ => (Half)(r.NextDouble() * 2000 - 1000),
            },
            ToPhysical = (v, _) => BitConverter.GetBytes(v),
            Equal = (a, b) => a.Equals(b) || (Half.IsNaN(a) && Half.IsNaN(b)),
        },
        Time<TimeOnly>("timeonly", TimeUnitPrecision.Millis, t => new TimeOnly(t), v => v.Ticks),
        Time<TimeOnly>("timeonly", TimeUnitPrecision.Micros, t => new TimeOnly(t), v => v.Ticks),
        Time<TimeOnly>("timeonly", TimeUnitPrecision.Nanos, t => new TimeOnly(t), v => v.Ticks),
        Time<TimeSpan>("timespan", TimeUnitPrecision.Millis, t => new TimeSpan(t), v => v.Ticks),
        Time<TimeSpan>("timespan", TimeUnitPrecision.Micros, t => new TimeSpan(t), v => v.Ticks),
        // Physical values (lake(0017)): a TIMESTAMP(NANOS) column written and read as long nanoseconds.
        new LeafKind<long> {
            Name = "timestamp_nanos(physical)", Order = Order.Signed,
            Declare = (n, nullable) => new DateTimeDataField(n, DateTimeFormat.Timestamp, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Nanos, isNullable: nullable),
            Generate = r => r.Next(5) == 0 ? -r.NextInt64(0, 1L << 60) : r.NextInt64(0, 1L << 62),
            ToPhysical = (v, _) => v,
        },
    ];

    /// <summary>A TIME column written from TimeOnly or TimeSpan, a time of day in whole units of its precision.</summary>
    static LeafKind Time<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)] T>(string name, TimeUnitPrecision unit, Func<long, T> fromTicks, Func<T, long> toTicks) where T : struct {
        long tick = unit switch { TimeUnitPrecision.Millis => TimeSpan.TicksPerMillisecond, TimeUnitPrecision.Micros => TimeSpan.TicksPerMicrosecond, _ => 1 };
        return new LeafKind<T> {
            Name = $"{name}({unit.ToString().ToLowerInvariant()})", Order = Order.Signed,
            Declare = (n, nullable) => new TimeDataField(n, unit, typeof(T), nullable),
            Generate = r => fromTicks(r.Next(6) switch {
                0 => 0,
                1 => TimeSpan.TicksPerDay - tick,   // the last instant of the day in this unit
                _ => r.NextInt64(0, TimeSpan.TicksPerDay) / tick * tick,
            }),
            ToPhysical = (v, _) => unit switch {
                TimeUnitPrecision.Millis => (object)(int)(toTicks(v) / TimeSpan.TicksPerMillisecond),
                TimeUnitPrecision.Micros => toTicks(v) / TimeSpan.TicksPerMicrosecond,
                _ => toTicks(v) * TimeSpan.NanosecondsPerTick,
            },
        };
    }

    static LeafKind Integer<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)] T>(string name, Func<Random, T> generate, Func<T, object> physical, Order order) where T : struct =>
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
