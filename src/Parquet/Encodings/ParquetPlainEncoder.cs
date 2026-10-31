using System;
using System.Buffers;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using Parquet.Data;
using Parquet.Extensions;
using Parquet.File.Values.Primitives;
using Parquet.Meta;
using TType = Parquet.Meta.Type;

namespace Parquet.Encodings;

/// <summary>
/// Fast data encoder. See https://github.com/aloneguid/parquet-dotnet/issues/643#issuecomment-3489932123 for future
/// performance ideas. Experimental.
/// </summary>
static class ParquetPlainEncoder {

    private static readonly System.Text.Encoding E = System.Text.Encoding.UTF8;
    private static readonly ArrayPool<byte> BytePool = ArrayPool<byte>.Shared;

    /// <summary>
    /// Memory-friendly method to encode data. This might be ugly, but it's a step forward in migration from legacy
    /// .NET.
    /// </summary>
    /// <param name="sourceSpan">
    /// Reference to <see cref="ReadOnlyMemory{T}"/> passed as an object, due to unclarity of what the T will be.
    /// </param>
    /// <param name="destination">Where do we write this to?</param>
    /// <param name="tse"></param>
    /// <param name="stats"></param>
    public static void Encode<T>(ReadOnlySpan<T> sourceSpan, Stream destination,
        SchemaElement tse,
        DataColumnStatistics? stats = null) where T : struct {

        System.Type t = typeof(T);
        // Stream.Null asks for the statistics alone (byte stream split pages, and chunks split into several pages,
        // compute them this way): the values are not encoded at all
        bool encode = !ReferenceEquals(destination, Stream.Null);

        if(t == typeof(bool)) {
            ReadOnlySpan<bool> span = sourceSpan.AsSpan<T, bool>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(byte)) {
            ReadOnlySpan<byte> span = sourceSpan.AsSpan<T, byte>();
            if(encode)
                Encode(span, destination, tse);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(sbyte)) {
            ReadOnlySpan<sbyte> span = sourceSpan.AsSpan<T, sbyte>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(short)) {
            ReadOnlySpan<short> span = sourceSpan.AsSpan<T, short>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(ushort)) {
            ReadOnlySpan<ushort> span = sourceSpan.AsSpan<T, ushort>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(int)) {
            ReadOnlySpan<int> span = sourceSpan.AsSpan<T, int>();
            if(encode)
                Encode(span, destination);
            if(Data.ColumnStatisticsRules.SortOrder(tse) == Data.ColumnSortOrder.Unsigned)
                StatsCompute.ComputeUnsigned(span, stats);  // unsigned values passed as their bits
            else
                StatsCompute.Compute(span, stats);
        } else if(t == typeof(uint)) {
            ReadOnlySpan<uint> span = sourceSpan.AsSpan<T, uint>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(long)) {
            ReadOnlySpan<long> span = sourceSpan.AsSpan<T, long>();
            if(encode)
                Encode(span, destination);
            if(Data.ColumnStatisticsRules.SortOrder(tse) == Data.ColumnSortOrder.Unsigned)
                StatsCompute.ComputeUnsigned(span, stats);
            else
                StatsCompute.Compute(span, stats);
        } else if(t == typeof(ulong)) {
            ReadOnlySpan<ulong> span = sourceSpan.AsSpan<T, ulong>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(BigInteger)) {
            ReadOnlySpan<BigInteger> span = sourceSpan.AsSpan<T, BigInteger>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(decimal)) {
            ReadOnlySpan<decimal> span = sourceSpan.AsSpan<T, decimal>();
            if(encode)
                Encode(span, destination, tse);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(BigDecimal)) {
            ReadOnlySpan<BigDecimal> span = sourceSpan.AsSpan<T, BigDecimal>();
            if(encode)
                Encode(span, destination, tse);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(double)) {
            ReadOnlySpan<double> span = sourceSpan.AsSpan<T, double>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(float)) {
            ReadOnlySpan<float> span = sourceSpan.AsSpan<T, float>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(DateTime)) {
            ReadOnlySpan<DateTime> span = sourceSpan.AsSpan<T, DateTime>();
            if(encode)
                Encode(span, destination, tse);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(DateOnly)) {
            ReadOnlySpan<DateOnly> span = sourceSpan.AsSpan<T, DateOnly>();
            if(encode)
                Encode(span, destination, tse);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(TimeOnly)) {
            ReadOnlySpan<TimeOnly> span = sourceSpan.AsSpan<T, TimeOnly>();
            if(encode)
                Encode(span, destination, tse);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(TimeSpan)) {
            ReadOnlySpan<TimeSpan> span = sourceSpan.AsSpan<T, TimeSpan>();
            if(encode)
                Encode(span, destination, tse);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(Interval)) {
            ReadOnlySpan<Interval> span = sourceSpan.AsSpan<T, Interval>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(ReadOnlyMemory<char>)) {
            ReadOnlySpan<ReadOnlyMemory<char>> span = sourceSpan.AsSpan<T, ReadOnlyMemory<char>>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else if(t == typeof(ReadOnlyMemory<byte>)) {
            ReadOnlySpan<ReadOnlyMemory<byte>> span = sourceSpan.AsSpan<T, ReadOnlyMemory<byte>>();
            if(encode) {
                if(tse.Type == TType.FIXED_LEN_BYTE_ARRAY)
                    EncodeFixed(span, destination, tse);
                else
                    Encode(span, destination);
            }
            // byte-wise min/max is the order of byte arrays, but not of those holding decimals or INTERVAL
            if(Data.ColumnStatisticsRules.SortOrder(tse) == Data.ColumnSortOrder.Unsigned)
                StatsCompute.Compute(span, stats);
        } else if(t == typeof(Guid)) {
            ReadOnlySpan<Guid> span = sourceSpan.AsSpan<T, Guid>();
            if(encode)
                Encode(span, destination);
            StatsCompute.Compute(span, stats);
        } else {
            throw new NotSupportedException($"no PLAIN encoder exists for {typeof(T)}");
        }
    }

    /// <summary>
    /// Generic overload for decoding directly into a <see cref="Span{T}"/> with element count control.
    /// </summary>
    public static void Decode<T>(
        Span<T> dest,
        int count,
        SchemaElement tse,
        Span<byte> source,
        out int elementsRead) where T : struct {

        int rem = dest.Length;
        if(count > rem)
            count = rem;

        System.Type t = typeof(T);

        if(t == typeof(bool)) {
            Span<bool> span = dest.AsSpan<T, bool>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(byte)) {
            Span<byte> span = dest.AsSpan<T, byte>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(sbyte)) {
            Span<sbyte> span = dest.AsSpan<T, sbyte>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(short)) {
            Span<short> span = dest.AsSpan<T, short>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(ushort)) {
            Span<ushort> span = dest.AsSpan<T, ushort>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(int)) {
            Span<int> span = dest.AsSpan<T, int>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(uint)) {
            Span<uint> span = dest.AsSpan<T, uint>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(long)) {
            Span<long> span = dest.AsSpan<T, long>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(ulong)) {
            Span<ulong> span = dest.AsSpan<T, ulong>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(BigInteger)) {
            Span<BigInteger> span = dest.AsSpan<T, BigInteger>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(decimal)) {
            Span<decimal> span = dest.AsSpan<T, decimal>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else if(t == typeof(BigDecimal)) {
            Span<BigDecimal> span = dest.AsSpan<T, BigDecimal>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else if(t == typeof(double)) {
            Span<double> span = dest.AsSpan<T, double>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(float)) {
            Span<float> span = dest.AsSpan<T, float>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(DateTime)) {
            Span<DateTime> span = dest.AsSpan<T, DateTime>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else if(t == typeof(DateOnly)) {
            Span<DateOnly> span = dest.AsSpan<T, DateOnly>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else if(t == typeof(TimeOnly)) {
            Span<TimeOnly> span = dest.AsSpan<T, TimeOnly>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else if(t == typeof(TimeSpan)) {
            Span<TimeSpan> span = dest.AsSpan<T, TimeSpan>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else if(t == typeof(Interval)) {
            Span<Interval> span = dest.AsSpan<T, Interval>();
            elementsRead = Decode(source, span.Slice(0, count));
        } else if(t == typeof(ReadOnlyMemory<char>)) {
            Span<ReadOnlyMemory<char>> span = dest.AsSpan<T, ReadOnlyMemory<char>>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else if(t == typeof(ReadOnlyMemory<byte>)) {
            Span<ReadOnlyMemory<byte>> span = dest.AsSpan<T, ReadOnlyMemory<byte>>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else if(t == typeof(Guid)) {
            Span<Guid> span = dest.AsSpan<T, Guid>();
            elementsRead = Decode(source, span.Slice(0, count), tse);
        } else {
            elementsRead = 0;
            throw new NotSupportedException($"no PLAIN decoder exists for {typeof(T)}");
        }
    }

    #region [ Single Value Encoding ]

    public static bool TryEncode(object? value, SchemaElement tse, out byte[]? result) {
        if(value == null) {
            result = null;
            return true;    // we've just successfully encoded null
        }

        System.Type t = value.GetType();
        if(t == typeof(bool)) {
            result = new[] { (bool)value ? (byte)1 : (byte)0 };    // PLAIN boolean, as one byte
            return true;
        } else if(t == typeof(byte)) {
            result = BitConverter.GetBytes((int)(byte)value);
            return true;
        } else if(t == typeof(sbyte)) {
            result = BitConverter.GetBytes((int)(sbyte)value);
            return true;
        } else if(t == typeof(short)) {
            result = BitConverter.GetBytes((int)(short)value);
            return true;
        } else if(t == typeof(ushort)) {
            result = BitConverter.GetBytes((int)(ushort)value);
            return true;
        } else if(t == typeof(int)) {
            result = BitConverter.GetBytes((int)value);
            return true;
        } else if(t == typeof(uint)) {
            result = BitConverter.GetBytes((int)(uint)value);
            return true;
        } else if(t == typeof(long)) {
            result = BitConverter.GetBytes((long)value);
            return true;
        } else if(t == typeof(ulong)) {
            result = BitConverter.GetBytes((ulong)value);
            return true;
        } else if(t == typeof(BigInteger)) {
            result = ((BigInteger)value).ToByteArray();
            return true;
        } else if(t == typeof(decimal))
            return TryEncode((decimal)value, tse, out result);
        else if(t == typeof(BigDecimal))
            return TryEncode((BigDecimal)value, tse, out result);
        else if(t == typeof(double)) {
            result = BitConverter.GetBytes((double)value);
            return true;
        } else if(t == typeof(float)) {
            result = BitConverter.GetBytes((float)value);
            return true;
        } else if(t == typeof(byte[])) {
            result = (byte[])value;
            return true;
        } else if(t == typeof(DateTime))
            return TryEncode((DateTime)value, tse, out result);
        else if(t == typeof(DateOnly))
            return TryEncode((DateOnly)value, tse, out result);
        else if(t == typeof(TimeOnly))
            return TryEncode((TimeOnly)value, tse, out result);
        else if(t == typeof(TimeSpan))
            return TryEncode((TimeSpan)value, tse, out result);
        else if(t == typeof(Interval)) {
            result = ((Interval)value).GetBytes();
            return true;
        } else if(t == typeof(Guid)) {
            result = ((Guid)value).ToByteArray(bigEndian: true);   // the UUID's RFC byte order, as its values are written
            return true;
        } else if(t == typeof(string)) {
            result = System.Text.Encoding.UTF8.GetBytes((string)value);
            return true;
        }

        result = null;
        return false;
    }

    public static bool TryDecode(byte[]? value, SchemaElement tse, ParquetOptions? options,
        out object? result) {
        if(value == null) {
            result = null;
            return true;    // we've just successfully encoded null
        }

        if(tse.Type == TType.BOOLEAN) {
            result = BitConverter.ToBoolean(value, 0);
            return true;
        }
        // before INT32 and INT64, which decimals can be stored as
        if(tse.IsDecimal()) {
            result = TryDecodeDecimal(value, tse);
            return true;
        }
        if(tse.Type == TType.INT32) {
            int i = BitConverter.ToInt32(value, 0);
            // narrower and unsigned integers as the column's CLR type (see SchemaEncoder.GetSystemType)
            result = IntegerBits(tse) switch {
                (8, true) => (sbyte)i,
                (8, false) => (byte)i,
                (16, true) => (short)i,
                (16, false) => (ushort)i,
                (32, false) => (uint)i,
                _ => i
            };
            return true;
        } else if(tse.Type == TType.INT64) {
            long l = BitConverter.ToInt64(value, 0);
            result = IntegerBits(tse) is (64, false) ? (ulong)l : l;
            return true;
        } else if(tse.Type == TType.INT96) {
            if(value.Length == 12)
                if(options?.TreatBigIntegersAsDates ?? false)
                    result = new NanoTime(value.AsSpan()).ToDateTime();
                else
                    result = new BigInteger(value);
            else
                result = null;
            return true;
        } else if(tse.Type == TType.DOUBLE) {
            result = BitConverter.ToDouble(value, 0);
            return true;
        } else if(tse.Type == TType.FLOAT) {
            result = BitConverter.ToSingle(value, 0);
            return true;
        } else if(tse.Type == TType.BYTE_ARRAY)
            if(tse.IsString()) {
                result = E.GetString(value);
                return true;
            } else {
                result = value;
                return true;
            }
        else if(tse.Type == TType.FIXED_LEN_BYTE_ARRAY && tse.LogicalType?.UUID != null && value.Length == 16) {
            result = new Guid(value, bigEndian: true);  // the RFC 4122 bytes, as the values are written
            return true;
        }
        else if(tse.Type == TType.FIXED_LEN_BYTE_ARRAY && !tse.IsDecimal() && tse.LogicalType?.UUID == null) {
            result = value;     // fixed[n] (and INTERVAL) as written
            return true;
        }

        result = null;
        return false;
    }

    private static (int bits, bool signed)? IntegerBits(SchemaElement tse) {
        if(tse.LogicalType?.INTEGER is IntType it)
            return (it.BitWidth, it.IsSigned);
        return tse.ConvertedType switch {
            ConvertedType.INT_8 => (8, true),
            ConvertedType.UINT_8 => (8, false),
            ConvertedType.INT_16 => (16, true),
            ConvertedType.UINT_16 => (16, false),
            ConvertedType.UINT_32 => (32, false),
            ConvertedType.UINT_64 => (64, false),
            _ => null
        };
    }

    /// <summary>
    /// The decimal an INT32/INT64-backed column stores as <paramref name="unscaled"/> times 10^-scale, built from its
    /// bits: exact on every runtime, and cheaper than a decimal multiplication. Multiplying by a power of ten
    /// converted from double was exact only while the runtime rounded double-to-decimal conversions to 15 digits;
    /// from .NET 11 that factor carries the double's error into every value.
    /// </summary>
    internal static decimal Unscaled(long unscaled, int scale) {
        bool negative = unscaled < 0;
        ulong magnitude = negative ? unchecked((ulong)-(unscaled + 1)) + 1 : (ulong)unscaled;
        return new decimal(unchecked((int)(uint)magnitude), unchecked((int)(uint)(magnitude >> 32)), 0, negative, (byte)scale);
    }

    private static object TryDecodeDecimal(byte[] value, SchemaElement tse) {
        switch(tse.Type) {
            case TType.INT32:
                return Unscaled(BitConverter.ToInt32(value, 0), tse.Scale ?? 0);
            case TType.INT64:
                return Unscaled(BitConverter.ToInt64(value, 0), tse.Scale ?? 0);
            case TType.FIXED_LEN_BYTE_ARRAY:
                return new BigDecimal(value, tse);
            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent a decimal");
        }
    }

    private static bool TryEncode(DateTime value, SchemaElement tse, out byte[] result) {
        switch(tse.Type) {
            case TType.INT32:
                int days = value.ToUnixDays();
                result = BitConverter.GetBytes(days);
                return true;
            case TType.INT64:
                // In the column's unit, exactly as Encode(ReadOnlySpan<DateTime>) writes its values: statistics in
                // another unit make readers prune row groups that hold matching rows.
                using(var ms = new MemoryStream()) {
                    Encode(new ReadOnlySpan<DateTime>(new[] { value }), ms, tse);
                    result = ms.ToArray();
                }
                return true;
            case TType.INT96:
                var nano = new NanoTime(value.ToUtc());
                result = nano.GetBytes();
                return true;
            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent any date types");

        }
    }

    private static bool TryEncode(DateOnly value, SchemaElement tse, out byte[] result) {
        int days = value.ToUnixDays();
        result = BitConverter.GetBytes(days);
        return true;
    }

    private static bool TryEncode(TimeOnly value, SchemaElement tse, out byte[] result) {
        switch(tse.Type) {
            case TType.INT32:
                int ms = (int)(value.Ticks / TimeSpan.TicksPerMillisecond);
                result = BitConverter.GetBytes(ms);
                return true;
            case TType.INT64:
                long micros = value.Ticks / 10;
                result = BitConverter.GetBytes(micros);
                return true;
            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent any time types");
        }
    }

    private static bool TryEncode(decimal value, SchemaElement tse, out byte[] result) {
        try {
            switch(tse.Type) {
                case TType.INT32:
                    double sf32 = Math.Pow(10, tse.Scale!.Value);
                    int i = (int)(value * (decimal)sf32);
                    result = BitConverter.GetBytes(i);
                    return true;
                case TType.INT64:
                    double sf64 = Math.Pow(10, tse.Scale!.Value);
                    long l = (long)(value * (decimal)sf64);
                    result = BitConverter.GetBytes(l);
                    return true;
                case TType.FIXED_LEN_BYTE_ARRAY:
                    var bd = BigDecimal.FromDecimal(value, tse.Precision, tse.Scale);
                    result = bd.GetBytes();
                    return true;
                default:
                    throw new InvalidDataException($"data type '{tse.Type}' does not represent a decimal");
            }
        } catch(OverflowException) {
            throw new ParquetException(
               $"value '{value}' is too large to fit into scale {tse.Scale} and precision {tse.Precision}");
        }
    }

    private static bool TryEncode(BigDecimal value, SchemaElement tse, out byte[] result) {
        result = value.GetBytes();
        return true;
    }

    private static bool TryEncode(TimeSpan value, SchemaElement tse, out byte[] result) {
        switch(tse.Type) {
            case TType.INT32:
                int ms = (int)value.TotalMilliseconds;
                result = BitConverter.GetBytes(ms);
                return true;
            case TType.INT64:
                long micros = value.Ticks / 10;
                result = BitConverter.GetBytes(micros);
                return true;
            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent any date types");
        }
    }

    #endregion


    public static void Encode(ReadOnlySpan<bool> data, Stream destination) {
        if(ParquetOptions.UseHardwareAcceleration)
            EncodeHwx(data, destination);
        else
            EncodeOnCpu(data, destination);
    }

    internal static void EncodeOnCpu(ReadOnlySpan<bool> data, Stream destination) {
        int targetLength = (data.Length + 7) / 8;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(targetLength);

        int n = 0;
        byte b = 0;
        int ib = 0;

        try {
            foreach(bool flag in data) {
                if(flag)
                    b |= (byte)(1 << n);

                n++;
                if(n == 8) {
                    buffer[ib++] = b;
                    n = 0;
                    b = 0;
                }
            }

            if(n != 0)
                buffer[ib] = b;

            destination.Write(buffer.AsSpan(0, targetLength));

        } finally {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static void EncodeHwx(ReadOnlySpan<bool> data, Stream destination) {
        if(Vector512.IsHardwareAccelerated && Vector512<byte>.IsSupported && data.Length > Vector512<byte>.Count) {
            ReadOnlySpan<byte> boolBytes = MemoryMarshal.AsBytes(data);
            ref byte current = ref MemoryMarshal.GetReference(boolBytes);

            int fullVectors = boolBytes.Length / Vector512<byte>.Count;

            long vl;
            for(int i = 0; i < fullVectors; i++) {
                Vector512<byte> isFalse = Vector512.Equals(Vector512.LoadUnsafe(ref current), Vector512<byte>.Zero);
                vl = (long)(~isFalse.ExtractMostSignificantBits());
                destination.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref vl, 1)));
                current = ref Unsafe.Add(ref current, Vector512<byte>.Count);
            }

            byte b = 0;
            int n = 0;

            //Process the last few bits
            for(int i = fullVectors * Vector512<byte>.Count; i < boolBytes.Length; i++) {
                b |= (byte)((data[i] ? 1 : 0) << n);
                n++;
                if(n == 8) {
                    destination.WriteByte(b);
                    n = 0;
                    b = 0;
                }
            }
            if(n != 0) {
                destination.WriteByte(b);
            }
        } else if(Vector256.IsHardwareAccelerated && Vector256<byte>.IsSupported && data.Length > Vector256<byte>.Count) {
            ReadOnlySpan<byte> boolBytes = MemoryMarshal.AsBytes(data);
            ref byte current = ref MemoryMarshal.GetReference(boolBytes);

            int fullVectors = boolBytes.Length / Vector256<byte>.Count;

            int vl;
            for(int i = 0; i < fullVectors; i++) {
                Vector256<byte> isFalse = Vector256.Equals(Vector256.LoadUnsafe(ref current), Vector256<byte>.Zero);

                vl = (int)(~isFalse.ExtractMostSignificantBits());
                destination.Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref vl, 1)));
                current = ref Unsafe.Add(ref current, Vector256<byte>.Count);
            }

            byte b = 0;
            int n = 0;

            //Process the last few bits
            for(int i = fullVectors * Vector256<byte>.Count; i < boolBytes.Length; i++) {
                b |= (byte)((data[i] ? 1 : 0) << n);
                n++;
                if(n == 8) {
                    destination.WriteByte(b);
                    n = 0;
                    b = 0;
                }
            }
            if(n != 0) {
                destination.WriteByte(b);
            }
        } else {
            EncodeOnCpu(data, destination);
        }
    }

    public static int Decode(Span<byte> source, Span<bool> data) {
        int tgtOffset = 0;
        int srcOffset = 0;

        int ibit = 0;
        while(srcOffset < source.Length && tgtOffset < data.Length) {
            byte b = source[srcOffset++];

            while(ibit < 8 && tgtOffset < data.Length && tgtOffset < data.Length) {
                bool set = ((b >> ibit++) & 1) == 1;
                data[tgtOffset++] = set;
            }

            ibit = 0;
        }

        return tgtOffset;
    }

    public static void Encode(ReadOnlySpan<byte> data, Stream destination, SchemaElement tse) {

        // copy shorts into ints
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        try {
            for(int i = 0; i < data.Length; i++)
                ints[i] = data[i];
        } finally {
            ArrayPool<int>.Shared.Return(ints);
        }

        Encode(ints.AsSpan(0, data.Length), destination);
    }

    public static int Decode(Span<byte> source, Span<byte> data) {
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        int r = Decode(source, ints.AsSpan(0, data.Length));
        for(int i = 0; i < data.Length; i++)
            data[i] = (byte)ints[i];
        return r;
    }

    public static void Encode(ReadOnlySpan<sbyte> data, Stream destination) {

        // copy shorts into ints
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        try {
            for(int i = 0; i < data.Length; i++)
                ints[i] = data[i];
        } finally {
            ArrayPool<int>.Shared.Return(ints);
        }

        Encode(ints.AsSpan(0, data.Length), destination);
    }

    public static int Decode(Span<byte> source, Span<sbyte> data) {
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        try {
            int r = Decode(source, ints.AsSpan(0, data.Length));
            for(int i = 0; i < data.Length; i++)
                data[i] = (sbyte)ints[i];
            return r;
        } finally {
            ArrayPool<int>.Shared.Return(ints);
        }
    }

    public static void Encode(ReadOnlySpan<short> data, Stream destination) {

        // copy shorts into ints
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        try {
            for(int i = 0; i < data.Length; i++)
                ints[i] = data[i];
        } finally {
            ArrayPool<int>.Shared.Return(ints);
        }

        Encode(ints.AsSpan(0, data.Length), destination);
    }

    public static int Decode(Span<byte> source, Span<short> data) {
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        try {
            int r = Decode(source, ints.AsSpan(0, data.Length));
            for(int i = 0; i < data.Length; i++)
                data[i] = (short)ints[i];
            return r;
        } finally {
            ArrayPool<int>.Shared.Return(ints);
        }
    }

    public static void Encode(ReadOnlySpan<ushort> data, Stream destination) {

        // copy ushorts into ints
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        try {
            for(int i = 0; i < data.Length; i++)
                ints[i] = data[i];
        } finally {
            ArrayPool<int>.Shared.Return(ints);
        }

        Encode(ints.AsSpan(0, data.Length), destination);
    }

    public static int Decode(Span<byte> source, Span<ushort> data) {
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        try {
            int r = Decode(source, ints.AsSpan(0, data.Length));
            for(int i = 0; i < data.Length; i++)
                data[i] = (ushort)ints[i];
            return r;
        } finally {
            ArrayPool<int>.Shared.Return(ints);
        }
    }

    public static void Encode(ReadOnlySpan<int> data, Stream destination) {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        destination.Write(bytes);
    }

    public static void Encode(ReadOnlySpan<uint> data, Stream destination) {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        destination.Write(bytes);
    }

    public static void Encode(ReadOnlySpan<long> data, Stream destination) {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        destination.Write(bytes);
    }

    public static void Encode(ReadOnlySpan<ulong> data, Stream destination) {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        destination.Write(bytes);
    }

    public static void Encode(ReadOnlySpan<BigInteger> data, Stream destination) {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        destination.Write(bytes);
    }

    public static int Decode<T>(Span<byte> source, Span<T> data) where T : struct {
        Span<byte> bytes = MemoryMarshal.AsBytes(data);
        source.CopyWithLimitTo(bytes);
        return Math.Min(data.Length, source.Length / Unsafe.SizeOf<T>());
    }

    private static readonly ulong[] PowersOfTen = {
        1, 10, 100, 1_000, 10_000, 100_000, 1_000_000, 10_000_000, 100_000_000, 1_000_000_000, 10_000_000_000,
        100_000_000_000, 1_000_000_000_000, 10_000_000_000_000, 100_000_000_000_000, 1_000_000_000_000_000,
        10_000_000_000_000_000, 100_000_000_000_000_000, 1_000_000_000_000_000_000 };

    /// <summary>
    /// The unscaled integer an INT32/INT64-backed decimal column stores for <paramref name="value"/>: the value times
    /// 10^scale, truncated. From the value's bits when it has no more than <paramref name="scale"/> decimal places and
    /// fits, which is the usual case and costs a multiplication of integers; otherwise by decimal arithmetic, as before.
    /// </summary>
    internal static long ToUnscaled(decimal value, int scale, SchemaElement tse) {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        int valueScale = (bits[3] >> 16) & 0xFF;
        if(bits[2] == 0 && valueScale <= scale && scale - valueScale < PowersOfTen.Length) {
            ulong magnitude = ((ulong)(uint)bits[1] << 32) | (uint)bits[0];
            ulong factor = PowersOfTen[scale - valueScale];
            if(magnitude <= long.MaxValue / factor) {
                long unscaled = (long)(magnitude * factor);
                return bits[3] < 0 ? -unscaled : unscaled;
            }
        }
        try {
            return (long)(value * (decimal)Math.Pow(10, scale));
        } catch(OverflowException) {
            throw TooLarge(value, tse);
        }
    }

    private static ParquetException TooLarge(decimal value, SchemaElement tse) =>
        new ParquetException($"value '{value}' is too large to fit into scale {tse.Scale} and precision {tse.Precision}");

    public static void Encode(ReadOnlySpan<decimal> data, Stream destination, SchemaElement tse) {
        switch(tse.Type) {
            case TType.INT32: {
                    int scale = tse.Scale ?? 0;
                    using var w = new ValueWriter(destination);
                    foreach(decimal d in data) {
                        long unscaled = ToUnscaled(d, scale, tse);
                        if(unscaled < int.MinValue || unscaled > int.MaxValue)
                            throw TooLarge(d, tse);
                        w.Write((int)unscaled);
                    }
                }
                break;
            case TType.INT64: {
                    int scale = tse.Scale ?? 0;
                    using var w = new ValueWriter(destination);
                    foreach(decimal d in data)
                        w.Write(ToUnscaled(d, scale, tse));
                }
                break;
            case TType.FIXED_LEN_BYTE_ARRAY:
                Span<byte> decBuf = stackalloc byte[16];
                foreach(decimal d in data) {
                    var bd = BigDecimal.FromDecimal(d, tse.Precision, tse.Scale);
                    int written = bd.WriteBytes(decBuf);
                    tse.TypeLength = written;
                    destination.Write(decBuf.Slice(0, written));
                }
                break;
            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent a decimal");
        }
    }

    public static void Encode(ReadOnlySpan<BigDecimal> data, Stream destination, SchemaElement tse) {
        Span<byte> buf = stackalloc byte[16];
        foreach(BigDecimal bd in data) {
            int written = bd.WriteBytes(buf);
            tse.TypeLength = written;
            destination.Write(buf.Slice(0, written));
        }
    }

    public static int Decode(Span<byte> source, Span<decimal> data, SchemaElement tse) {
        switch(tse.Type) {
            case TType.INT32: {
                    byte scale = (byte)(tse.Scale ?? 0);
                    int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
                    try {
                        Decode(source, ints.AsSpan(0, data.Length));
                        for(int i = 0; i < data.Length; i++)
                            data[i] = Unscaled(ints[i], scale);
                        return data.Length;
                    } finally {
                        ArrayPool<int>.Shared.Return(ints);
                    }
                }
            case TType.INT64: {
                    byte scale = (byte)(tse.Scale ?? 0);
                    long[] longs = ArrayPool<long>.Shared.Rent(data.Length);
                    try {
                        Decode(source, longs.AsSpan(0, data.Length));
                        for(int i = 0; i < data.Length; i++)
                            data[i] = Unscaled(longs[i], scale);
                        return data.Length;
                    } finally {
                        ArrayPool<long>.Shared.Return(longs);
                    }
                }
            case TType.FIXED_LEN_BYTE_ARRAY: {
                    int tl = tse.TypeLength ?? 0;
                    if(tl == 0)
                        return 0;

                    int i = 0;
                    for(int offset = 0; offset + tl <= source.Length && i < data.Length; offset += tl, i++) {
                        data[i] = BigDecimal.ToSystemDecimal(source.Slice(offset, tl).ToArray(), tse);
                    }
                    return i;
                }
            case TType.BYTE_ARRAY: {
                    // type_length: 0
                    // precision: 4
                    // scale: 2
                    // see https://github.com/apache/arrow/pull/2646/files

                    int read = 0;
                    // convert each byte chunk to valid decimal bytes
                    int spos = 0;
                    while(read < data.Length) {
                        int length = source.ReadInt32(spos);
                        spos += sizeof(int);
                        if(length > 0) {
                            decimal dc = BigDecimal.ToSystemDecimal(source.Slice(spos, length).ToArray(), tse);
                            spos += length;
                            data[read++] = dc;
                        }
                    }
                    return read;
                }

            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent a decimal");
        }
    }

    public static int Decode(Span<byte> source, Span<BigDecimal> data, SchemaElement tse) {
        switch(tse.Type) {
            case TType.INT32: {
                    byte scale = (byte)(tse.Scale ?? 0);
                    int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
                    try {
                        Decode(source, ints.AsSpan(0, data.Length));
                        for(int i = 0; i < data.Length; i++)
                            data[i] = BigDecimal.FromDecimal(Unscaled(ints[i], scale), tse.Precision, tse.Scale);
                        return data.Length;
                    } finally {
                        ArrayPool<int>.Shared.Return(ints);
                    }
                }
            case TType.INT64: {
                    byte scale = (byte)(tse.Scale ?? 0);
                    long[] longs = ArrayPool<long>.Shared.Rent(data.Length);
                    try {
                        Decode(source, longs.AsSpan(0, data.Length));
                        for(int i = 0; i < data.Length; i++)
                            data[i] = BigDecimal.FromDecimal(Unscaled(longs[i], scale), tse.Precision, tse.Scale);
                        return data.Length;
                    } finally {
                        ArrayPool<long>.Shared.Return(longs);
                    }
                }
            case TType.FIXED_LEN_BYTE_ARRAY: {
                    int tl = tse.TypeLength ?? 0;
                    if(tl == 0)
                        return 0;

                    int i = 0;
                    for(int offset = 0; offset + tl <= source.Length && i < data.Length; offset += tl, i++) {
                        data[i] = new BigDecimal(source.Slice(offset, tl).ToArray(), tse);
                    }
                    return i;
                }
            case TType.BYTE_ARRAY: {
                    // type_length: 0
                    // precision: 4
                    // scale: 2
                    // see https://github.com/apache/arrow/pull/2646/files

                    int read = 0;
                    // convert each byte chunk to valid decimal bytes
                    int spos = 0;
                    while(read < data.Length) {
                        int length = source.ReadInt32(spos);
                        spos += sizeof(int);
                        if(length > 0) {
                            var bd = new BigDecimal(source.Slice(spos, length).ToArray(), tse);
                            spos += length;
                            data[read++] = bd;
                        }
                    }
                    return read;
                }

            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent a decimal");
        }
    }

    public static void Encode(ReadOnlySpan<double> data, Stream destination) {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        destination.Write(bytes);
    }

    public static void Encode(ReadOnlySpan<float> data, Stream destination) {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        destination.Write(bytes);
    }

    public static int Decode(Span<byte> source, Span<float> data) {
        Span<byte> bytes = MemoryMarshal.AsBytes(data);
        source.CopyWithLimitTo(bytes);
        return data.Length;
    }

    /// <summary>
    /// Buffers fixed-width values and length prefixes for a stream, in place of an array and a stream write per value
    /// (<c>BitConverter.GetBytes</c>), which cost 32 bytes of garbage for every string, binary, date or time written.
    /// </summary>
    private sealed class ValueWriter : IDisposable {
        private readonly Stream _destination;
        private readonly byte[] _buffer = BytePool.Rent(8192);
        private int _used;

        public ValueWriter(Stream destination) => _destination = destination;

        public void Write(int value) {
            if(_used + sizeof(int) > _buffer.Length)
                Flush();
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_used), value);
            _used += sizeof(int);
        }

        public void Write(long value) {
            if(_used + sizeof(long) > _buffer.Length)
                Flush();
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_used), value);
            _used += sizeof(long);
        }

        public void Write(ReadOnlySpan<byte> bytes) {
            if(_used + bytes.Length > _buffer.Length) {
                Flush();
                if(bytes.Length > _buffer.Length) {
                    _destination.Write(bytes);
                    return;
                }
            }
            bytes.CopyTo(_buffer.AsSpan(_used));
            _used += bytes.Length;
        }

        public void Flush() {
            if(_used > 0) {
                _destination.Write(_buffer, 0, _used);
                _used = 0;
            }
        }

        public void Dispose() {
            Flush();
            BytePool.Return(_buffer);
        }
    }

    public static void Encode(ReadOnlySpan<byte[]> data, Stream destination) {
        using var w = new ValueWriter(destination);
        foreach(byte[] element in data) {
            w.Write(element.Length);
            w.Write(element.AsSpan());
        }
    }

    public static void Encode(ReadOnlySpan<ReadOnlyMemory<byte>> data, Stream destination) {
        using var w = new ValueWriter(destination);
        foreach(ReadOnlyMemory<byte> element in data) {
            w.Write(element.Length);
            w.Write(element.Span);
        }
    }

    /// <summary>FIXED_LEN_BYTE_ARRAY: the values back to back, no length prefix; each must be exactly the type length.</summary>
    public static void EncodeFixed(ReadOnlySpan<ReadOnlyMemory<byte>> data, Stream destination, SchemaElement tse) {
        int length = tse.TypeLength ?? throw new InvalidDataException($"type length must be set for {nameof(TType.FIXED_LEN_BYTE_ARRAY)}");
        foreach(ReadOnlyMemory<byte> element in data) {
            if(element.Length != length)
                throw new ParquetException($"'{tse.Name}' is FIXED_LEN_BYTE_ARRAY({length}) but a value has {element.Length} bytes");
            destination.Write(element.Span);
        }
    }

    public static void Encode(ReadOnlySpan<Guid> data, Stream destination) {
        foreach(Guid element in data) {
            // Since .NET 8 there's a built-in support for big-endian encoding
            // see https://learn.microsoft.com/en-us/dotnet/api/system.guid.tobytearray?view=net-8.0
            byte[] b = element.ToByteArray(true);
            destination.Write(b, 0, b.Length);
        }
    }

    public static int Decode(Span<byte> source, Span<byte[]> data, SchemaElement tse) {
        int read = 0;
        int sourceOffset = 0;

        if(tse.Type == TType.FIXED_LEN_BYTE_ARRAY) {
            if(tse.TypeLength == null)
                throw new InvalidDataException($"type length must be set for {nameof(TType.FIXED_LEN_BYTE_ARRAY)}");
            int length = tse.TypeLength.Value;
            while(read < data.Length) {
                byte[] el = source.Slice(sourceOffset, length).ToArray();
                sourceOffset += length;
                data[read++] = el;
            }
        } else {
            while(read < data.Length) {
                int length = source.ReadInt32(sourceOffset);
                sourceOffset += sizeof(int);
                if(length > 0) {
                    byte[] el = source.Slice(sourceOffset, length).ToArray();
                    sourceOffset += length;
                    data[read++] = el;
                } else {
                    data[read++] = [];
                }
            }
        }
        return read;
    }

    public static int Decode(Span<byte> source, Span<ReadOnlyMemory<byte>> data, SchemaElement tse) {
        int read = 0;
        int sourceOffset = 0;

        // The page buffer is reused once decoded, so the values are copied: into one array per page, of which each
        // value is a slice (rather than an array per value).
        byte[] copy = source.ToArray();
        if(tse.Type == TType.FIXED_LEN_BYTE_ARRAY) {
            if(tse.TypeLength == null)
                throw new InvalidDataException($"type length must be set for {nameof(TType.FIXED_LEN_BYTE_ARRAY)}");
            int length = tse.TypeLength.Value;
            while(read < data.Length) {
                if(sourceOffset + length > copy.Length)
                    throw new InvalidDataException("FIXED_LEN_BYTE_ARRAY page is shorter than its values");
                data[read++] = new ReadOnlyMemory<byte>(copy, sourceOffset, length);
                sourceOffset += length;
            }
        } else {
            while(read < data.Length) {
                int length = source.ReadInt32(sourceOffset);
                sourceOffset += sizeof(int);
                if(length > 0) {
                    data[read++] = new ReadOnlyMemory<byte>(copy, sourceOffset, length);
                    sourceOffset += length;
                } else {
                    data[read++] = ReadOnlyMemory<byte>.Empty;
                }
            }
        }
        return read;
    }

    public static void Encode(ReadOnlySpan<DateTime> data, Stream destination, SchemaElement tse) {
        using var w = new ValueWriter(destination);

        switch(tse.Type) {
            case TType.INT32:
                foreach(DateTime element in data) {
                    int days = element.ToUnixDays();
                    w.Write(days);
                }
                break;
            case TType.INT64:
                if(tse.LogicalType?.TIMESTAMP is not null) {
                    foreach(DateTime element in data) {
                        if(tse.LogicalType.TIMESTAMP.Unit.MILLIS is not null) {
                            long unixTime = element.ToUnixMilliseconds();
                            w.Write(unixTime);
                        } else if(tse.LogicalType.TIMESTAMP.Unit.MICROS is not null) {
                            long unixTime = element.ToUtc().ToUnixMicroseconds();
                            w.Write(unixTime);
                        } else if(tse.LogicalType.TIMESTAMP.Unit.NANOS is not null) {
                            long unixTime = element.ToUtc().ToUnixNanoseconds();
                            w.Write(unixTime);
                        } else {
                            throw new ParquetException($"Unexpected TimeUnit: {tse.LogicalType.TIMESTAMP.Unit}");
                        }
                    }
                } else if(tse.IsTimestampMillis()) {
                    foreach(DateTime element in data) {
                        long unixTime = element.ToUtc().ToUnixMilliseconds();
                        w.Write(unixTime);
                    }
                } else if(tse.IsTimestampMicros()) {
                    foreach(DateTime element in data) {
                        long unixTime = element.ToUtc().ToUnixMicroseconds();
                        w.Write(unixTime);
                    }
                } else {
                    throw new ArgumentException(
                        $"Unsupported {nameof(DateTime)} encoding for schema element. " +
                        $"Type={tse.Type}, ConvertedType={tse.ConvertedType}, " +
                        $"HasLogicalTimestamp={tse.LogicalType?.TIMESTAMP is not null}",
                        nameof(tse));
                }
                break;
            case TType.INT96:
                w.Flush();
                foreach(DateTime element in data) {
                    var nano = new NanoTime(element.ToUtc());
                    nano.Write(destination);
                }
                break;
            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent any date types");

        }
    }

    public static void Encode(ReadOnlySpan<DateOnly> data, Stream destination, SchemaElement tse) {
        using var w = new ValueWriter(destination);
        foreach(DateOnly element in data) {
            int days = element.ToUnixDays();
            w.Write(days);
        }
    }
    public static void Encode(ReadOnlySpan<TimeOnly> data, Stream destination, SchemaElement tse) {
        using var w = new ValueWriter(destination);
        switch(tse.Type) {
            case TType.INT32:
                foreach(TimeOnly element in data) {
                    int ticks = (int)(element.Ticks / TimeSpan.TicksPerMillisecond);
                    w.Write(ticks);
                }
                break;
            case TType.INT64:
                foreach(TimeOnly element in data) {
                    long ticks = element.Ticks / 10;
                    w.Write(ticks);
                }
                break;
        }
    }

    private const long UnixEpochTicks = 621_355_968_000_000_000;   // 1970-01-01T00:00:00Z

    private static void UnixToDateTimes(ReadOnlySpan<long> values, Span<DateTime> data, long ticksPerUnit, DateTimeKind kind) {
        for(int i = 0; i < values.Length; i++)
            data[i] = new DateTime(checked(UnixEpochTicks + values[i] * ticksPerUnit), kind);
    }

    private static void UnixNanosToDateTimes(ReadOnlySpan<long> values, Span<DateTime> data, DateTimeKind kind) {
        for(int i = 0; i < values.Length; i++)
            data[i] = new DateTime(checked(UnixEpochTicks + values[i] / TimeSpan.NanosecondsPerTick), kind);
    }

    public static int Decode(Span<byte> source, Span<DateTime> data, SchemaElement tse) {
        switch(tse.Type) {
            case TType.INT32:
                int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
                try {
                    int intsRead = Decode(source, ints.AsSpan(0, data.Length));
                    for(int i = 0; i < intsRead; i++)
                        data[i] = new DateTime(checked(UnixEpochTicks + ints[i] * TimeSpan.TicksPerDay), DateTimeKind.Utc);
                    return intsRead;
                } finally {
                    ArrayPool<int>.Shared.Return(ints);
                }
            case TType.INT64:
                long[] longs = ArrayPool<long>.Shared.Rent(data.Length);
                try {
                    int longsRead = Decode(source, longs.AsSpan(0, data.Length));
                    ReadOnlySpan<long> values = longs.AsSpan(0, longsRead);
                    // the unit and kind once per page, and each value straight to ticks: the same DateTime as
                    // epoch.AddMilliseconds(ms).AddTicks(rest) then SpecifyKind, at a fraction of the cost
                    if(tse.LogicalType?.TIMESTAMP is { } timestamp) {
                        DateTimeKind kind = timestamp.IsAdjustedToUTC ? DateTimeKind.Utc : DateTimeKind.Local;
                        if(timestamp.Unit.MILLIS is not null)
                            UnixToDateTimes(values, data, TimeSpan.TicksPerMillisecond, kind);
                        else if(timestamp.Unit.MICROS is not null)
                            UnixToDateTimes(values, data, TimeSpan.TicksPerMicrosecond, kind);
                        else if(timestamp.Unit.NANOS is not null)
                            UnixNanosToDateTimes(values, data, kind);
                        else
                            throw new ParquetException($"Unexpected TimeUnit: {timestamp.Unit}");
                    } else if(tse.IsTimestampMicros()) {
                        UnixToDateTimes(values, data, TimeSpan.TicksPerMicrosecond, DateTimeKind.Utc);
                    } else {
                        UnixToDateTimes(values, data, TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
                    }
                    return longsRead;
                } finally {
                    ArrayPool<long>.Shared.Return(longs);
                }
            case TType.INT96:
                byte[] buf = ArrayPool<byte>.Shared.Rent(NanoTime.BinarySize * data.Length);
                try {
                    int offset = 0;
                    int els = 0;
                    while(offset + NanoTime.BinarySize <= source.Length) {
                        data[els++] = new NanoTime(source.Slice(offset, NanoTime.BinarySize)).ToDateTime();
                        offset += NanoTime.BinarySize;
                    }
                    return els;
                } finally {
                    ArrayPool<byte>.Shared.Return(buf);
                }
            default:
                throw new NotSupportedException();
        }
    }

    public static int Decode(Span<byte> source, Span<DateOnly> data, SchemaElement tse) {
        int[] ints = ArrayPool<int>.Shared.Rent(data.Length);
        try {
            int intsRead = Decode(source, ints.AsSpan(0, data.Length));
            for(int i = 0; i < intsRead; i++)
                data[i] = DateOnly.FromDateTime(ints[i].AsUnixDaysInDateTime());
            return intsRead;
        } finally {
            ArrayPool<int>.Shared.Return(ints);
        }
    }

    public static int Decode(Span<byte> source, Span<TimeOnly> data, SchemaElement tse) {
        switch(tse.Type) {
            case TType.INT32: {
                    int i = 0;
                    int srcPos = 0;
                    while(srcPos + sizeof(int) <= source.Length && i < data.Length) {
                        int iv = source.ReadInt32(srcPos);
                        srcPos += sizeof(int);
                        data[i++] = new TimeOnly(iv * TimeSpan.TicksPerMillisecond);
                    }
                    return i;
                }
            case TType.INT64: {
                    int i = 0;
                    int srcPos = 0;
                    while(srcPos + sizeof(long) <= source.Length && i < data.Length) {
                        long lv = source.ReadInt64(srcPos);
                        srcPos += sizeof(long);
                        data[i++] = new TimeOnly(lv * 10);
                    }
                    return i;
                }
            default:
                throw new NotSupportedException();
        }
    }

    public static void Encode(ReadOnlySpan<TimeSpan> data, Stream destination, SchemaElement tse) {
        using var w = new ValueWriter(destination);
        switch(tse.Type) {
            case TType.INT32:
                foreach(TimeSpan ts in data) {
                    int ms = (int)ts.TotalMilliseconds;
                    w.Write(ms);
                }
                break;
            case TType.INT64:
                foreach(TimeSpan ts in data) {
                    long micros = ts.Ticks / 10;
                    w.Write(micros);
                }
                break;
            default:
                throw new InvalidDataException($"data type '{tse.Type}' does not represent any date types");
        }
    }

    public static int Decode(Span<byte> source, Span<TimeSpan> data, SchemaElement tse) {
        switch(tse.Type) {
            case TType.INT32: {
                    int i = 0;
                    int srcPos = 0;
                    while(srcPos + sizeof(int) <= source.Length && i < data.Length) {
                        int iv = source.ReadInt32(srcPos);
                        srcPos += sizeof(int);
                        data[i++] = new TimeSpan(0, 0, 0, 0, iv);
                    }
                    return i;
                }
            case TType.INT64: {
                    int i = 0;
                    int srcPos = 0;
                    while(srcPos + sizeof(long) <= source.Length && i < data.Length) {
                        long lv = source.ReadInt64(srcPos);
                        srcPos += sizeof(long);
                        data[i++] = new TimeSpan(lv * 10);
                    }
                    return i;
                }
            default:
                throw new NotSupportedException();
        }
    }

    public static void Encode(ReadOnlySpan<Interval> data, Stream destination) {
        foreach(Interval iv in data) {
            byte[] b = iv.GetBytes();
            destination.Write(b, 0, b.Length);
        }
    }

    public static int Decode(Span<byte> source, Span<Interval> data) {
        int i = 0;
        int pos = 0;
        while(pos + Interval.BinarySize <= source.Length && i < data.Length) {
            int months = source.ReadInt32(pos);
            pos += sizeof(int);
            int days = source.ReadInt32(pos);
            pos += sizeof(int);
            int millis = source.ReadInt32(pos);
            pos += sizeof(int);
            var e = new Interval(months, days, millis);

            data[i++] = e;
        }
        return i;
    }

    public static void Encode(ReadOnlySpan<ReadOnlyMemory<char>> data, Stream destination) {

        // rent a buffer large enough not to reallocate often and not call stream write often
        byte[] rb = BytePool.Rent(1024 * 10);
        int rbOffset = 0;
        try {

            foreach(ReadOnlyMemory<char> s in data) {
                int len = s.IsEmpty ? 0 : E.GetByteCount(s.Span);
                int minLen = len + sizeof(int);
                int rem = rb.Length - rbOffset;

                // check we have enough space left
                if(rem < minLen) {
                    destination.Write(rb, 0, rbOffset); // dump current buffer
                    rbOffset = 0;

                    // do we need to reallocate for more space?
                    if(minLen > rb.Length) {
                        BytePool.Return(rb);
                        rb = BytePool.Rent(minLen);
                    }
                }

                // write our data
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(rb.AsSpan(rbOffset), len);
                rbOffset += sizeof(int);
                if(len > 0) {
                    E.GetBytes(s.Span, rb.AsSpan(rbOffset));
                    rbOffset += len;
                }
            }

            if(rbOffset > 0)
                destination.Write(rb, 0, rbOffset);

        } finally {
            BytePool.Return(rb);
        }
    }

    public static int Decode(Span<byte> source, Span<string> data, SchemaElement tse) {
        //int remLength = (int)(source.Length - source.Position);

        if(source.Length == 0)
            return 0;

        int i = 0;

        if(tse.Type == TType.FIXED_LEN_BYTE_ARRAY) {
            if(tse.TypeLength == null)
                throw new InvalidDataException($"type length must be set for {nameof(TType.FIXED_LEN_BYTE_ARRAY)}");
            int length = tse.TypeLength.Value;

            for(int spanIdx = 0; spanIdx < source.Length && i < data.Length; i++) {
                data[i] = E.GetString(source.Slice(spanIdx, length));
                spanIdx += length;
            }

        } else {

            for(int spanIdx = 0; spanIdx < source.Length && i < data.Length; i++) {
                int length = source.ReadInt32(spanIdx);
                spanIdx += sizeof(int);
                data[i] = E.GetString(source.Slice(spanIdx, length));
                spanIdx += length;
            }
        }
        return i;
    }

    public static int Decode(Span<byte> source, Span<ReadOnlyMemory<char>> data, SchemaElement tse) {
        //int remLength = (int)(source.Length - source.Position);

        if(source.Length == 0)
            return 0;

        int i = 0;

        Decoder decoder = System.Text.Encoding.UTF8.GetDecoder();

        if(tse.Type == TType.FIXED_LEN_BYTE_ARRAY) {
            if(tse.TypeLength == null)
                throw new InvalidDataException($"type length must be set for {nameof(TType.FIXED_LEN_BYTE_ARRAY)}");
            int length = tse.TypeLength.Value;

            for(int spanIdx = 0; spanIdx < source.Length && i < data.Length; i++) {

                Span<byte> elementSpan = source.Slice(spanIdx, length);
                char[] charBuffer = new char[System.Text.Encoding.UTF8.GetCharCount(elementSpan)];
                decoder.GetChars(elementSpan, charBuffer, flush: true);
                ReadOnlyMemory<char> charMemory = charBuffer.AsMemory();
                data[i] = charMemory;
                spanIdx += length;
            }

        } else {

            // One char array per page (UTF-8 never decodes to more chars than it has bytes), each value a slice of it,
            // rather than an array per value.
            char[] chars = new char[source.Length];
            int used = 0;
            for(int spanIdx = 0; spanIdx < source.Length && i < data.Length; i++) {
                int length = source.ReadInt32(spanIdx);
                spanIdx += sizeof(int);
                Span<byte> elementSpan = source.Slice(spanIdx, length);
                int count = decoder.GetChars(elementSpan, chars.AsSpan(used), flush: true);
                data[i] = new ReadOnlyMemory<char>(chars, used, count);
                used += count;
                spanIdx += length;
            }
        }
        return i;
    }

    public static int Decode(Span<byte> source, Span<Guid> data, SchemaElement tse) {
        if(tse.TypeLength != 16)
            throw new InvalidOperationException($"'{tse.TypeLength}' is invalid type length for UUID (should be 16)");

        int i = 0;
        for(int offset = 0; offset + 16 <= source.Length && i < data.Length; offset += 16, i++) {
            data[i] = new Guid(((ReadOnlySpan<byte>)source.Slice(offset, 16)), true);
        }
        return i;
    }
}