using System;
using System.IO;
using System.Runtime.CompilerServices;
using CommunityToolkit.HighPerformance;
using Parquet.Data;
using Parquet.Extensions;
using Parquet.Meta;

namespace Parquet.Encodings;

/// <summary>
/// DELTA_BINARY_PACKED
/// (https://github.com/apache/parquet-format/blob/master/Encodings.md#delta-encoding-delta_binary_packed--5).
/// Good video explainer: https://youtu.be/vNQqe6PGCH4.
/// 
/// Fastparquet sample:
/// https://github.com/dask/fastparquet/blob/c59e105537a8e7673fa30676dfb16d9fa5fb1cac/fastparquet/cencoding.pyx#L232
/// 
/// Golang sample:
/// https://github.com/xitongsys/parquet-go/blob/62cf52a8dad4f8b729e6c38809f091cd134c3749/encoding/encodingread.go#L270
/// 
/// Supported Types: short, ushort, int, uint, long, ulong
/// </summary>
static partial class DeltaBinaryPackedEncoder {

    public static bool IsSupported(System.Type t) =>
        t == typeof(int) || t == typeof(long) ||           // native types
        t == typeof(short) || t == typeof(ushort) ||       // int32 compatible
        t == typeof(uint) || t == typeof(ulong);           // int64 compatible

    public static bool CanEncode<T>(ReadOnlySpan<T> data) where T : struct {
        if(data.IsEmpty)
            return false;

        // Fast path for ulong overflow check
        if(typeof(T) == typeof(ulong))
            return CanEncodeULong(data.AsSpan<T, ulong>());

        return IsSupported(typeof(T));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanEncodeULongArray(ulong[] data, int offset, int count) {
        const ulong maxValue = (ulong)long.MaxValue;
        int end = offset + count;

        for(int i = offset; i < end; i++) {
            if(data[i] > maxValue) {
                return false;
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CanEncodeULong(ReadOnlySpan<ulong> data) {
        const ulong maxValue = (ulong)long.MaxValue;
        foreach(ulong v in data) {
            if(v > maxValue) {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Encodes the provided data using a delta encoding scheme and writes it to the given destination stream.
    /// Optionally, collects statistics about the encoded data if the 'stats' parameter is provided.
    /// </summary>
    /// <typeparam name="T">The type of elements to encode. Supported types: short, ushort, int, uint, long, ulong.</typeparam>
    /// <param name="data">The input span to be encoded.</param>
    /// <param name="destination">The stream where the encoded data will be written.</param>
    /// <param name="stats">Optional parameter to collect statistics about the encoded data (can be null).</param>
    /// <exception cref="NotSupportedException">Thrown when T is not a supported type or ulong values exceed long.MaxValue.</exception>
    public static void Encode<T>(ReadOnlySpan<T> data, Stream destination, DataColumnStatistics? stats = null) where T : struct {
        if(typeof(T) == typeof(int)) {
            ReadOnlySpan<int> span = data.AsSpan<T, int>();
            EncodeInt(span, destination, 1024, 32);
            StatsCompute.Compute(span, stats);
        } else if(typeof(T) == typeof(long)) {
            ReadOnlySpan<long> span = data.AsSpan<T, long>();
            EncodeLong(span, destination, 1024, 32);
            StatsCompute.Compute(span, stats);
        } else if(typeof(T) == typeof(short)) {
            ReadOnlySpan<short> span = data.AsSpan<T, short>();
            EncodeAsInt(data, destination);
            StatsCompute.Compute(span, stats);
        } else if(typeof(T) == typeof(ushort)) {
            ReadOnlySpan<ushort> span = data.AsSpan<T, ushort>();
            EncodeAsInt(data, destination);
            StatsCompute.Compute(span, stats);
        } else if(typeof(T) == typeof(uint)) {
            ReadOnlySpan<uint> span = data.AsSpan<T, uint>();
            EncodeAsInt(data, destination);
            StatsCompute.Compute(span, stats);
        } else if(typeof(T) == typeof(ulong)) {
            ReadOnlySpan<ulong> span = data.AsSpan<T, ulong>();
            if(!CanEncodeULong(span)) {
                throw new NotSupportedException($"ulong values exceed long.MaxValue range and cannot be encoded with {Encoding.DELTA_BINARY_PACKED}. Use plain encoding instead.");
            }
            EncodeUlong(span, destination, 1024, 32);
            StatsCompute.Compute(span, stats);
        } else {
            throw new NotSupportedException($"type {typeof(T)} is not supported in {Encoding.DELTA_BINARY_PACKED}");
        }
    }

    public static int Decode(Span<byte> s, Array dest, int destOffset, int valueCount, out int consumedBytes) {
        if(s.Length == 0 && valueCount == 0) {
            consumedBytes = 0;
            return 0;
        }

        System.Type? elementType = dest.GetType().GetElementType();
        if(elementType == null) {
            throw new NotSupportedException($"element type {elementType} is not supported");
        }

        // Native types - no conversion needed
        if(elementType == typeof(long)) {
            return DecodeLong(s, ((long[])dest).AsSpan(destOffset), out consumedBytes);
        }
        if(elementType == typeof(int)) {
            return DecodeInt(s, ((int[])dest).AsSpan(destOffset), out consumedBytes);
        }

        // INT32-backed types are decoded as int (the stored type), then narrowed
        if(elementType == typeof(short)) {
            return DecodeFromInt(s, ((short[])dest).AsSpan(destOffset), out consumedBytes);
        }
        if(elementType == typeof(ushort)) {
            return DecodeFromInt(s, ((ushort[])dest).AsSpan(destOffset), out consumedBytes);
        }
        if(elementType == typeof(uint)) {
            return DecodeFromInt(s, ((uint[])dest).AsSpan(destOffset), out consumedBytes);
        }
        if(elementType == typeof(ulong)) {
            return DecodeUlong(s, ((ulong[])dest).AsSpan(destOffset), out consumedBytes);
        }

        throw new NotSupportedException($"element type {elementType} is not supported in {Encoding.DELTA_BINARY_PACKED}");
    }

    public static int Decode<T>(Span<byte> s, Span<T> dest, int valueCount, out int consumedBytes) where T : struct {
        if(s.Length == 0 && valueCount == 0) {
            consumedBytes = 0;
            return 0;
        }

        if(typeof(T) == typeof(long)) {
            return DecodeLong(s, dest.AsSpan<T, long>(), out consumedBytes);
        }
        if(typeof(T) == typeof(int)) {
            return DecodeInt(s, dest.AsSpan<T, int>(), out consumedBytes);
        }
        if(typeof(T) == typeof(short) || typeof(T) == typeof(ushort) || typeof(T) == typeof(uint)) {
            return DecodeFromInt(s, dest, out consumedBytes);
        }
        if(typeof(T) == typeof(ulong)) {
            return DecodeUlong(s, dest.AsSpan<T, ulong>(), out consumedBytes);
        }

        throw new NotSupportedException($"element type {typeof(T)} is not supported in {Encoding.DELTA_BINARY_PACKED}");
    }


    /// <summary>
    /// short, ushort and uint are stored as INT32, whose deltas the format computes in 32-bit two's complement
    /// arithmetic. Encoding them in their own width wrapped deltas at 16 bits (short, ushort) or computed them
    /// unsigned (uint), which other readers decode to different values. They are widened to int, sign-extended
    /// for short and zero-extended for ushort, and uint keeps its bits.
    /// </summary>
    static void EncodeAsInt<T>(ReadOnlySpan<T> data, Stream destination) where T : struct {
        int[] wide = System.Buffers.ArrayPool<int>.Shared.Rent(data.Length);
        try {
            Span<int> values = wide.AsSpan(0, data.Length);
            if(typeof(T) == typeof(short)) {
                ReadOnlySpan<short> span = data.AsSpan<T, short>();
                for(int i = 0; i < span.Length; i++) {
                    values[i] = span[i];
                }
            } else if(typeof(T) == typeof(ushort)) {
                ReadOnlySpan<ushort> span = data.AsSpan<T, ushort>();
                for(int i = 0; i < span.Length; i++) {
                    values[i] = span[i];
                }
            } else {
                ReadOnlySpan<uint> span = data.AsSpan<T, uint>();
                for(int i = 0; i < span.Length; i++) {
                    values[i] = unchecked((int)span[i]);
                }
            }
            EncodeInt(values, destination, 1024, 32);
        } finally {
            System.Buffers.ArrayPool<int>.Shared.Return(wide);
        }
    }

    /// <summary>Decodes INT32 deltas, then narrows them to short, ushort or uint.</summary>
    static int DecodeFromInt<T>(Span<byte> s, Span<T> dest, out int consumedBytes) where T : struct {
        int[] wide = System.Buffers.ArrayPool<int>.Shared.Rent(Math.Max(dest.Length, 1));
        try {
            Span<int> values = wide.AsSpan(0, dest.Length);
            int read = DecodeInt(s, values, out consumedBytes);
            if(typeof(T) == typeof(short)) {
                Span<short> span = dest.AsSpan<T, short>();
                for(int i = 0; i < read; i++) {
                    span[i] = unchecked((short)values[i]);
                }
            } else if(typeof(T) == typeof(ushort)) {
                Span<ushort> span = dest.AsSpan<T, ushort>();
                for(int i = 0; i < read; i++) {
                    span[i] = unchecked((ushort)values[i]);
                }
            } else {
                Span<uint> span = dest.AsSpan<T, uint>();
                for(int i = 0; i < read; i++) {
                    span[i] = unchecked((uint)values[i]);
                }
            }
            return read;
        } finally {
            System.Buffers.ArrayPool<int>.Shared.Return(wide);
        }
    }

    //this extension method calculates the position of the most significant bit that is set to 1 
    static int CalculateBitWidth(this Span<int> span) {
        int mask = 0;
        for(int i = 0; i < span.Length; i++) {
            mask |= span[i];
        }
        return 32 - mask.NumberOfLeadingZerosInt();
    }

    //this extension method calculates the position of the most significant bit that is set to 1
    static int CalculateBitWidth(this Span<long> span) {
        long mask = 0;
        for(int i = 0; i < span.Length; i++) {
            mask |= span[i];
        }
        return 64 - mask.NumberOfLeadingZerosLong();
    }

    //this extension method calculates the position of the most significant bit that is set to 1
    static int CalculateBitWidth(this Span<short> span) {
        int mask = 0;
        for(int i = 0; i < span.Length; i++) {
            mask |= (int)span[i];
        }
        return 32 - mask.NumberOfLeadingZerosInt();
    }

    //this extension method calculates the position of the most significant bit that is set to 1
    static int CalculateBitWidth(this Span<ushort> span) {
        int mask = 0;
        for(int i = 0; i < span.Length; i++) {
            mask |= span[i];
        }
        return 32 - mask.NumberOfLeadingZerosInt();
    }

    //this extension method calculates the position of the most significant bit that is set to 1
    static int CalculateBitWidth(this Span<uint> span) {
        long mask = 0;
        for(int i = 0; i < span.Length; i++) {
            mask |= span[i];
        }
        return 64 - mask.NumberOfLeadingZerosLong();
    }

    //this extension method calculates the position of the most significant bit that is set to 1
    static int CalculateBitWidth(this Span<ulong> span) {
        ulong mask = 0;
        for(int i = 0; i < span.Length; i++) {
            mask |= span[i];
        }
        return 64 - ((long)mask).NumberOfLeadingZerosLong();
    }
}
