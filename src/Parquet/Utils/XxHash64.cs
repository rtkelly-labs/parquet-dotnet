using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Parquet.Utils;

/// <summary>
/// xxHash64 (https://github.com/Cyan4973/xxHash/blob/dev/doc/xxhash_spec.md), seed 0: the hash Parquet's bloom filters
/// use. Inline rather than System.IO.Hashing, so the package takes no dependency for it.
/// </summary>
static class XxHash64 {

    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> data) {
        unchecked {
            int length = data.Length;
            int i = 0;
            ulong h;
            if(length >= 32) {
                ulong v1 = Prime1 + Prime2, v2 = Prime2, v3 = 0, v4 = 0 - Prime1;
                for(; i <= length - 32; i += 32) {
                    v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i)));
                    v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i + 8)));
                    v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i + 16)));
                    v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i + 24)));
                }
                h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7)
                    + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
                h = Merge(h, v1);
                h = Merge(h, v2);
                h = Merge(h, v3);
                h = Merge(h, v4);
            } else {
                h = Prime5;
            }
            h += (ulong)length;
            for(; i + 8 <= length; i += 8) {
                h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i)));
                h = BitOperations.RotateLeft(h, 27) * Prime1 + Prime4;
            }
            if(i + 4 <= length) {
                h ^= BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i)) * Prime1;
                h = BitOperations.RotateLeft(h, 23) * Prime2 + Prime3;
                i += 4;
            }
            for(; i < length; i++) {
                h ^= data[i] * Prime5;
                h = BitOperations.RotateLeft(h, 11) * Prime1;
            }
            h ^= h >> 33;
            h *= Prime2;
            h ^= h >> 29;
            h *= Prime3;
            h ^= h >> 32;
            return h;
        }
    }

    private static ulong Round(ulong acc, ulong input) {
        unchecked {
            acc += input * Prime2;
            acc = BitOperations.RotateLeft(acc, 31);
            return acc * Prime1;
        }
    }

    private static ulong Merge(ulong acc, ulong value) {
        unchecked {
            acc ^= Round(0, value);
            return acc * Prime1 + Prime4;
        }
    }
}
