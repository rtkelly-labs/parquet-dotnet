using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Parquet.Encryption;

/// <summary>Module types, bound into each module's AAD (parquet-format Encryption.md, "Module AAD").</summary>
enum ModuleType : byte {
    Footer = 0,
    ColumnMetaData = 1,
    DataPage = 2,
    DictionaryPage = 3,
    DataPageHeader = 4,
    DictionaryPageHeader = 5,
    ColumnIndex = 6,
    OffsetIndex = 7,
    BloomFilterHeader = 8,
    BloomFilterBitset = 9,
}

/// <summary>
/// Encrypted modules: a 4-byte little-endian length, then for AES-GCM a 12-byte nonce, the ciphertext and a 16-byte
/// tag; for AES-CTR a 12-byte nonce and the ciphertext.
/// </summary>
static class AesModules {
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int LengthPrefix = 4;
    public const int GcmOverhead = LengthPrefix + NonceLength + TagLength;
    public const int CtrOverhead = LengthPrefix + NonceLength;

    public static void CheckKey(byte[] key, string what) {
        if(key.Length is not (16 or 24 or 32))
            throw new ArgumentException($"{what} must be 16, 24 or 32 bytes, not {key.Length}");
    }

    /// <summary>The AAD of a module: the file AAD, the module type, then the ordinals that apply to it (little-endian int16s).</summary>
    public static byte[] Aad(byte[] fileAad, ModuleType type, int rowGroup = -1, int column = -1, int page = -1) {
        int extra = 1 + (rowGroup >= 0 ? 2 : 0) + (column >= 0 ? 2 : 0) + (page >= 0 ? 2 : 0);
        byte[] aad = new byte[fileAad.Length + extra];
        fileAad.CopyTo(aad, 0);
        int i = fileAad.Length;
        aad[i++] = (byte)type;
        foreach(int ordinal in new[] { rowGroup, column, page }) {
            if(ordinal < 0)
                continue;
            if(ordinal > short.MaxValue)
                throw new NotSupportedException($"encrypted files hold at most {short.MaxValue + 1} row groups, columns and pages per column chunk");
            BinaryPrimitives.WriteInt16LittleEndian(aad.AsSpan(i), (short)ordinal);
            i += 2;
        }
        return aad;
    }

    public static byte[] EncryptGcm(byte[] key, ReadOnlySpan<byte> plaintext, byte[] aad) {
        byte[] module = new byte[GcmOverhead + plaintext.Length];
        BinaryPrimitives.WriteInt32LittleEndian(module, module.Length - LengthPrefix);
        Span<byte> nonce = module.AsSpan(LengthPrefix, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagLength);
        gcm.Encrypt(nonce, plaintext,
            module.AsSpan(LengthPrefix + NonceLength, plaintext.Length),
            module.AsSpan(LengthPrefix + NonceLength + plaintext.Length, TagLength), aad);
        return module;
    }

    /// <summary>Decrypts a GCM module (with its length prefix).</summary>
    public static byte[] DecryptGcm(byte[] key, ReadOnlySpan<byte> module, byte[] aad) {
        int length = CheckedLength(module, NonceLength + TagLength);
        ReadOnlySpan<byte> body = module.Slice(LengthPrefix, length);
        byte[] plaintext = new byte[length - NonceLength - TagLength];
        using var gcm = new AesGcm(key, TagLength);
        try {
            gcm.Decrypt(body.Slice(0, NonceLength), body.Slice(NonceLength, plaintext.Length),
                body.Slice(NonceLength + plaintext.Length, TagLength), plaintext, aad);
        } catch(AuthenticationTagMismatchException e) {
            throw new ParquetException("Parquet decryption failed: wrong key, wrong AAD prefix, or a tampered file", e);
        }
        return plaintext;
    }

    /// <summary>The GCM tag of <paramref name="plaintext"/> under the given nonce (plaintext footer signatures).</summary>
    public static byte[] GcmTag(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, byte[] aad) {
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagLength];
        using var gcm = new AesGcm(key, TagLength);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        return tag;
    }

    public static byte[] EncryptCtr(byte[] key, ReadOnlySpan<byte> plaintext) {
        byte[] module = new byte[CtrOverhead + plaintext.Length];
        BinaryPrimitives.WriteInt32LittleEndian(module, module.Length - LengthPrefix);
        Span<byte> nonce = module.AsSpan(LengthPrefix, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        Ctr(key, nonce, plaintext, module.AsSpan(CtrOverhead));
        return module;
    }

    public static byte[] DecryptCtr(byte[] key, ReadOnlySpan<byte> module) {
        int length = CheckedLength(module, NonceLength);
        byte[] plaintext = new byte[length - NonceLength];
        Ctr(key, module.Slice(LengthPrefix, NonceLength), module.Slice(CtrOverhead, plaintext.Length), plaintext);
        return plaintext;
    }

    /// <summary>The module's length (after the prefix), checked against the bytes there are.</summary>
    public static int CheckedLength(ReadOnlySpan<byte> module, int minimum) {
        if(module.Length < LengthPrefix)
            throw new ParquetException("encrypted module is truncated");
        int length = BinaryPrimitives.ReadInt32LittleEndian(module);
        if(length < minimum || length > module.Length - LengthPrefix)
            throw new ParquetException($"encrypted module length {length} is invalid");
        return length;
    }

    /// <summary>AES-CTR with a 16-byte counter block: the 12-byte nonce, then a 4-byte big-endian counter from 1.</summary>
    private static void Ctr(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output) {
        const int block = 16;
        int blocks = (input.Length + block - 1) / block;
        byte[] counters = new byte[blocks * block];
        for(int b = 0; b < blocks; b++) {
            Span<byte> c = counters.AsSpan(b * block, block);
            nonce.CopyTo(c);
            BinaryPrimitives.WriteUInt32BigEndian(c.Slice(NonceLength), (uint)(b + 1));
        }
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] stream = aes.EncryptEcb(counters, PaddingMode.None);
        for(int i = 0; i < input.Length; i++)
            output[i] = (byte)(input[i] ^ stream[i]);
    }
}
