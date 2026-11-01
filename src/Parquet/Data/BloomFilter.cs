using System;
using System.Buffers.Binary;
using System.Text;
using Parquet.Utils;

namespace Parquet.Data;

/// <summary>
/// A column chunk's split block bloom filter (parquet-format <c>BloomFilter.md</c>): whether a value may be in the chunk.
/// A false answer is certain; a true one may be a false positive. Values are hashed with xxHash64 (seed 0) over their
/// PLAIN encoding in the column's physical type: INT32 and INT64 as little-endian integers, FLOAT and DOUBLE as their
/// IEEE 754 bits, little-endian, and BYTE_ARRAY and FIXED_LEN_BYTE_ARRAY as their bytes (a string as its UTF-8 bytes).
/// Ask in the physical type: a DATE column is INT32 days, a TIMESTAMP INT64 in its unit, a DECIMAL its unscaled value
/// or bytes.
/// </summary>
public sealed class BloomFilter {

    /// <summary>A block is eight 32-bit words.</summary>
    internal const int BlockBytes = 32;

    private static readonly uint[] Salt = [
        0x47b6137bU, 0x44974d91U, 0x8824ad5bU, 0xa2b7289dU, 0x705495c7U, 0x2df1424bU, 0x9efc4947U, 0x5c6bfb31U ];

    private readonly byte[] _bitset;

    internal BloomFilter(byte[] bitset) {
        _bitset = bitset;
    }

    /// <summary>An empty filter of <paramref name="numBytes"/> bytes (a whole number of blocks), for writing.</summary>
    internal BloomFilter(int numBytes) : this(new byte[numBytes]) { }

    internal ReadOnlySpan<byte> Bitset => _bitset;

    /// <summary>The largest filter written: 128 MiB, as parquet-mr and parquet-cpp cap it.</summary>
    internal const int MaxBytes = 128 * 1024 * 1024;

    /// <summary>
    /// The bitset size for <paramref name="distinctValues"/> values at false positive probability <paramref name="fpp"/>
    /// (parquet-format BloomFilter.md: m = -8 n / ln(1 - p^(1/8)) bits), rounded up to a power of two between one block
    /// and <see cref="MaxBytes"/>.
    /// </summary>
    internal static int OptimalBytes(long distinctValues, double fpp) {
        if(!(fpp > 0 && fpp < 1))
            throw new ArgumentOutOfRangeException(nameof(fpp), fpp, "a bloom filter's false positive probability is between 0 and 1");
        double bits = -8 * distinctValues / Math.Log(1 - Math.Pow(fpp, 1.0 / 8));
        long bytes = (long)Math.Ceiling(bits / 8);
        int size = BlockBytes;
        while(size < bytes && size < MaxBytes)
            size *= 2;
        return size;
    }

    /// <summary>Adds a value, given as its hash (<see cref="Hash"/>): sets its bit in each word of its block.</summary>
    internal void InsertHash(ulong hash) {
        ulong blocks = (ulong)(_bitset.Length / BlockBytes);
        int block = (int)(((hash >> 32) * blocks) >> 32);
        uint key = (uint)hash;
        Span<byte> words = _bitset.AsSpan(block * BlockBytes, BlockBytes);
        for(int i = 0; i < Salt.Length; i++) {
            Span<byte> word = words.Slice(i * sizeof(uint), sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(word, BinaryPrimitives.ReadUInt32LittleEndian(word) | (1U << (int)((key * Salt[i]) >> 27)));
        }
    }

    /// <summary>The size of the bitset, in bytes.</summary>
    public int SizeInBytes => _bitset.Length;

    /// <summary>Whether an INT32 value may be in the chunk.</summary>
    public bool MightContain(int value) {
        Span<byte> plain = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(plain, value);
        return MightContain(plain);
    }

    /// <summary>Whether an INT64 value may be in the chunk.</summary>
    public bool MightContain(long value) {
        Span<byte> plain = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(plain, value);
        return MightContain(plain);
    }

    /// <summary>Whether a FLOAT value may be in the chunk.</summary>
    public bool MightContain(float value) {
        Span<byte> plain = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(plain, value);
        return MightContain(plain);
    }

    /// <summary>Whether a DOUBLE value may be in the chunk.</summary>
    public bool MightContain(double value) {
        Span<byte> plain = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(plain, value);
        return MightContain(plain);
    }

    /// <summary>Whether a string (a BYTE_ARRAY value, as UTF-8) may be in the chunk.</summary>
    public bool MightContain(string value) => MightContain(Encoding.UTF8.GetBytes(value));

    /// <summary>Whether a value, given as its PLAIN bytes (a BYTE_ARRAY or FIXED_LEN_BYTE_ARRAY value as-is), may be in the chunk.</summary>
    public bool MightContain(ReadOnlySpan<byte> plain) => MightContainHash(Hash(plain));

    /// <summary>The hash the filter uses for a value given as its PLAIN bytes: xxHash64 with seed 0.</summary>
    public static ulong Hash(ReadOnlySpan<byte> plain) => XxHash64.Hash(plain);

    /// <summary>Whether a value with this hash (see <see cref="Hash"/>) may be in the chunk.</summary>
    public bool MightContainHash(ulong hash) {
        // the block: the hash's top 32 bits scaled to the number of blocks; within it, one bit per word, chosen by the
        // low 32 bits multiplied by that word's salt
        ulong blocks = (ulong)(_bitset.Length / BlockBytes);
        int block = (int)(((hash >> 32) * blocks) >> 32);
        uint key = (uint)hash;
        ReadOnlySpan<byte> words = _bitset.AsSpan(block * BlockBytes, BlockBytes);
        for(int i = 0; i < Salt.Length; i++) {
            uint mask = 1U << (int)((key * Salt[i]) >> 27);
            if((BinaryPrimitives.ReadUInt32LittleEndian(words.Slice(i * sizeof(uint))) & mask) == 0)
                return false;
        }
        return true;
    }
}
