using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Parquet.Meta;
using Parquet.Meta.Proto;

namespace Parquet.Encryption;

/// <summary>Encrypts one column chunk's pages and headers.</summary>
sealed class ColumnEncryptor {
    private readonly byte[] _key;
    private readonly byte[] _fileAad;
    private readonly int _rowGroup;
    private readonly int _column;
    private readonly bool _ctr;

    public ColumnEncryptor(byte[] key, byte[] fileAad, int rowGroup, int column, bool ctr) =>
        (_key, _fileAad, _rowGroup, _column, _ctr) = (key, fileAad, rowGroup, column, ctr);

    public byte[] Header(ReadOnlySpan<byte> header, bool dictionary, int page) =>
        AesModules.EncryptGcm(_key, header, dictionary
            ? AesModules.Aad(_fileAad, ModuleType.DictionaryPageHeader, _rowGroup, _column)
            : AesModules.Aad(_fileAad, ModuleType.DataPageHeader, _rowGroup, _column, page));

    public byte[] Page(ReadOnlySpan<byte> data, bool dictionary, int page) =>
        _ctr
            ? AesModules.EncryptCtr(_key, data)
            : AesModules.EncryptGcm(_key, data, dictionary
                ? AesModules.Aad(_fileAad, ModuleType.DictionaryPage, _rowGroup, _column)
                : AesModules.Aad(_fileAad, ModuleType.DataPage, _rowGroup, _column, page));
}

/// <summary>Decrypts one column chunk's pages and headers.</summary>
sealed class ColumnDecryptor {
    private readonly byte[] _key;
    private readonly byte[] _fileAad;
    private readonly int _rowGroup;
    private readonly int _column;
    private readonly bool _ctr;

    public ColumnDecryptor(byte[] key, byte[] fileAad, int rowGroup, int column, bool ctr) =>
        (_key, _fileAad, _rowGroup, _column, _ctr) = (key, fileAad, rowGroup, column, ctr);

    /// <summary>Reads the encrypted page header at the stream's position, leaving the stream after it.</summary>
    public PageHeader ReadHeader(Stream s, bool dictionary, int page) {
        byte[] module = ReadModule(s);
        byte[] plain = AesModules.DecryptGcm(_key, module, dictionary
            ? AesModules.Aad(_fileAad, ModuleType.DictionaryPageHeader, _rowGroup, _column)
            : AesModules.Aad(_fileAad, ModuleType.DataPageHeader, _rowGroup, _column, page));
        using var ms = new MemoryStream(plain);
        return PageHeader.Read(new ThriftCompactProtocolReader(ms));
    }

    public byte[] Page(ReadOnlySpan<byte> module, bool dictionary, int page) =>
        _ctr
            ? AesModules.DecryptCtr(_key, module)
            : AesModules.DecryptGcm(_key, module, dictionary
                ? AesModules.Aad(_fileAad, ModuleType.DictionaryPage, _rowGroup, _column)
                : AesModules.Aad(_fileAad, ModuleType.DataPage, _rowGroup, _column, page));

    private static byte[] ReadModule(Stream s) {
        byte[] prefix = new byte[AesModules.LengthPrefix];
        s.ReadExactly(prefix);
        int length = BitConverter.ToInt32(prefix, 0);
        if(length < AesModules.NonceLength || length > s.Length - s.Position)
            throw new ParquetException($"encrypted module length {length} is invalid");
        byte[] module = new byte[AesModules.LengthPrefix + length];
        prefix.CopyTo(module, 0);
        s.ReadExactly(module, AesModules.LengthPrefix, length);
        return module;
    }
}

/// <summary>A file being written with encryption: its file AAD, and the keys of its columns.</summary>
sealed class FileEncryptor {
    private readonly ParquetEncryptionOptions _options;
    private readonly byte[] _fileAad;
    private readonly EncryptionAlgorithm _algorithm;

    public FileEncryptor(ParquetEncryptionOptions options) {
        AesModules.CheckKey(options.FooterKey, "the footer key");
        foreach(KeyValuePair<string, ParquetColumnKey> c in options.ColumnKeys ?? new Dictionary<string, ParquetColumnKey>())
            AesModules.CheckKey(c.Value.Key, $"the key of column {c.Key}");
        _options = options;

        byte[] unique = RandomNumberGenerator.GetBytes(8);
        byte[] prefix = options.AadPrefix ?? [];
        _fileAad = [.. prefix, .. unique];
        byte[]? stored = options.AadPrefix != null && options.StoreAadPrefix ? options.AadPrefix : null;
        bool? supply = options.AadPrefix != null && !options.StoreAadPrefix ? true : null;
        _algorithm = options.UseCtrForPages
            ? new EncryptionAlgorithm { AESGCMCTRV1 = new AesGcmCtrV1 { AadPrefix = stored, AadFileUnique = unique, SupplyAadPrefix = supply } }
            : new EncryptionAlgorithm { AESGCMV1 = new AesGcmV1 { AadPrefix = stored, AadFileUnique = unique, SupplyAadPrefix = supply } };
    }

    public bool EncryptedFooter => !_options.PlaintextFooter;

    private (byte[] key, ParquetColumnKey? own)? KeyOf(IEnumerable<string> path) {
        if(_options.ColumnKeys == null)
            return (_options.FooterKey, null);
        return _options.ColumnKeys.TryGetValue(string.Join(".", path), out ParquetColumnKey? ck) ? (ck.Key, ck) : null;
    }

    /// <summary>The encryptor for a column chunk, or null when the column is written in plaintext.</summary>
    public ColumnEncryptor? ForColumn(IEnumerable<string> path, int rowGroup, int column) =>
        KeyOf(path) is (byte[] key, _) ? new ColumnEncryptor(key, _fileAad, rowGroup, column, _options.UseCtrForPages) : null;

    /// <summary>
    /// Sets the row group ordinals and each column's crypto metadata: columns with their own key (and every encrypted
    /// column when the footer is plaintext) get their metadata encrypted, plaintext footers keep a copy without statistics.
    /// </summary>
    public void Seal(FileMetaData meta) {
        for(int r = 0; r < meta.RowGroups.Count; r++) {
            RowGroup rg = meta.RowGroups[r];
            rg.Ordinal = (short)r;
            for(int c = 0; c < rg.Columns.Count; c++) {
                ColumnChunk cc = rg.Columns[c];
                ColumnMetaData md = cc.MetaData!;
                if(KeyOf(md.PathInSchema) is not (byte[] key, var own))
                    continue;
                cc.CryptoMetadata = own == null
                    ? new ColumnCryptoMetaData { ENCRYPTIONWITHFOOTERKEY = new EncryptionWithFooterKey() }
                    : new ColumnCryptoMetaData {
                        ENCRYPTIONWITHCOLUMNKEY = new EncryptionWithColumnKey { PathInSchema = md.PathInSchema.ToList(), KeyMetadata = own.KeyMetadata }
                    };
                if(own == null && EncryptedFooter)
                    continue;   // protected by the footer's encryption
                using(var ms = new MemoryStream()) {
                    md.Write(new ThriftCompactProtocolWriter(ms));
                    cc.EncryptedColumnMetadata = AesModules.EncryptGcm(key, ms.ToArray(),
                        AesModules.Aad(_fileAad, ModuleType.ColumnMetaData, r, c));
                }
                cc.MetaData = EncryptedFooter ? null : Stripped(md);
            }
        }
        if(!EncryptedFooter) {
            meta.EncryptionAlgorithm = _algorithm;
            meta.FooterSigningKeyMetadata = _options.FooterKeyMetadata;
        }
    }

    /// <summary>What readers without the column's key see in a plaintext footer: no statistics.</summary>
    private static ColumnMetaData Stripped(ColumnMetaData md) => new() {
        Type = md.Type, Encodings = md.Encodings, PathInSchema = md.PathInSchema, Codec = md.Codec,
        NumValues = md.NumValues, TotalUncompressedSize = md.TotalUncompressedSize,
        TotalCompressedSize = md.TotalCompressedSize, DataPageOffset = md.DataPageOffset,
        DictionaryPageOffset = md.DictionaryPageOffset, IndexPageOffset = md.IndexPageOffset,
    };

    /// <summary>The footer as stored between the data and the footer length (after <see cref="Seal"/>).</summary>
    public byte[] Footer(FileMetaData meta) {
        byte[] plain;
        using(var ms = new MemoryStream()) {
            meta.Write(new ThriftCompactProtocolWriter(ms));
            plain = ms.ToArray();
        }
        byte[] aad = AesModules.Aad(_fileAad, ModuleType.Footer);
        using var result = new MemoryStream();
        if(EncryptedFooter) {
            new FileCryptoMetaData { EncryptionAlgorithm = _algorithm, KeyMetadata = _options.FooterKeyMetadata }
                .Write(new ThriftCompactProtocolWriter(result));
            result.Write(AesModules.EncryptGcm(_options.FooterKey, plain, aad));
        } else {
            // signature: the nonce and the GCM tag of the footer
            byte[] nonce = RandomNumberGenerator.GetBytes(AesModules.NonceLength);
            result.Write(plain);
            result.Write(nonce);
            result.Write(AesModules.GcmTag(_options.FooterKey, nonce, plain, aad));
        }
        return result.ToArray();
    }
}

/// <summary>A file being read with encryption: its algorithm, file AAD and keys.</summary>
sealed class FileDecryptor {
    private readonly ParquetDecryptionOptions? _options;
    private readonly byte[] _fileAad;
    private readonly bool _ctr;
    private readonly byte[]? _footerKey;

    private FileDecryptor(EncryptionAlgorithm algorithm, byte[]? footerKeyMetadata, ParquetDecryptionOptions? options) {
        _options = options;
        (byte[]? prefix, byte[]? unique, bool? supply) = algorithm switch {
            { AESGCMV1: AesGcmV1 a } => (a.AadPrefix, a.AadFileUnique, a.SupplyAadPrefix),
            { AESGCMCTRV1: AesGcmCtrV1 a } => (a.AadPrefix, a.AadFileUnique, a.SupplyAadPrefix),
            _ => throw new NotSupportedException("unknown Parquet encryption algorithm")
        };
        _ctr = algorithm.AESGCMCTRV1 != null;
        if(supply == true && options?.AadPrefix == null)
            throw new ParquetException("this file's AAD prefix is not stored in it: set ParquetDecryptionOptions.AadPrefix");
        if(prefix != null && options?.AadPrefix != null && !prefix.AsSpan().SequenceEqual(options.AadPrefix))
            throw new ParquetException("the AAD prefix does not match the one stored in the file");
        _fileAad = [.. options?.AadPrefix ?? prefix ?? [], .. unique ?? []];
        _footerKey = options?.FooterKey ?? options?.KeyRetriever?.Invoke(footerKeyMetadata);
    }

    /// <summary>Decrypts an encrypted footer (the bytes between the data and the footer length of a PARE file).</summary>
    public static (FileMetaData meta, FileDecryptor decryptor) ReadEncryptedFooter(byte[] footer, ParquetDecryptionOptions? options) {
        using var ms = new MemoryStream(footer);
        FileCryptoMetaData crypto = FileCryptoMetaData.Read(new ThriftCompactProtocolReader(ms));
        var d = new FileDecryptor(crypto.EncryptionAlgorithm, crypto.KeyMetadata, options);
        if(d._footerKey == null)
            throw new ParquetException("the file's footer is encrypted: set ParquetOptions.Decryption with its footer key or a key retriever");
        byte[] plain = AesModules.DecryptGcm(d._footerKey, footer.AsSpan((int)ms.Position), AesModules.Aad(d._fileAad, ModuleType.Footer));
        using var pm = new MemoryStream(plain);
        FileMetaData meta = FileMetaData.Read(new ThriftCompactProtocolReader(pm));
        d.DecryptColumnMetadata(meta);
        return (meta, d);
    }

    /// <summary>A plaintext footer of an encrypted file: checks its signature when the footer key is known.</summary>
    public static FileDecryptor ForPlaintextFooter(FileMetaData meta, byte[] footer, int metaLength, ParquetDecryptionOptions? options) {
        var d = new FileDecryptor(meta.EncryptionAlgorithm!, meta.FooterSigningKeyMetadata, options);
        if(d._footerKey != null && (options?.VerifyPlaintextFooter ?? true)) {
            if(footer.Length != metaLength + AesModules.NonceLength + AesModules.TagLength)
                throw new ParquetException("the plaintext footer has no signature");
            ReadOnlySpan<byte> nonce = footer.AsSpan(metaLength, AesModules.NonceLength);
            byte[] tag = AesModules.GcmTag(d._footerKey, nonce, footer.AsSpan(0, metaLength), AesModules.Aad(d._fileAad, ModuleType.Footer));
            if(!CryptographicOperations.FixedTimeEquals(tag, footer.AsSpan(metaLength + AesModules.NonceLength, AesModules.TagLength)))
                throw new ParquetException("the plaintext footer's signature does not match: wrong footer key, or a tampered file");
        }
        d.DecryptColumnMetadata(meta);
        return d;
    }

    private byte[]? KeyOf(ColumnCryptoMetaData crypto) {
        if(crypto.ENCRYPTIONWITHFOOTERKEY != null)
            return _footerKey;
        EncryptionWithColumnKey ck = crypto.ENCRYPTIONWITHCOLUMNKEY!;
        if(_options?.ColumnKeys != null && _options.ColumnKeys.TryGetValue(string.Join(".", ck.PathInSchema), out byte[]? key))
            return key;
        return _options?.KeyRetriever?.Invoke(ck.KeyMetadata);
    }

    /// <summary>Replaces encrypted column metadata with its plaintext where the key is known.</summary>
    private void DecryptColumnMetadata(FileMetaData meta) {
        for(int r = 0; r < meta.RowGroups.Count; r++) {
            RowGroup rg = meta.RowGroups[r];
            int ordinal = rg.Ordinal ?? r;
            for(int c = 0; c < rg.Columns.Count; c++) {
                ColumnChunk cc = rg.Columns[c];
                if(cc.CryptoMetadata == null || cc.EncryptedColumnMetadata == null)
                    continue;
                byte[]? key = KeyOf(cc.CryptoMetadata);
                if(key == null)
                    continue;   // a column this reader has no key for: it cannot be read
                byte[] plain = AesModules.DecryptGcm(key, cc.EncryptedColumnMetadata, AesModules.Aad(_fileAad, ModuleType.ColumnMetaData, ordinal, c));
                using var ms = new MemoryStream(plain);
                cc.MetaData = ColumnMetaData.Read(new ThriftCompactProtocolReader(ms));
            }
        }
    }

    /// <summary>The decryptor for a column chunk, or null when the column is in plaintext.</summary>
    public ColumnDecryptor? ForColumn(ColumnChunk cc, int rowGroup, int column) {
        if(cc.CryptoMetadata == null)
            return null;
        byte[] key = KeyOf(cc.CryptoMetadata) ?? throw new ParquetException(
            $"column '{string.Join(".", cc.CryptoMetadata.ENCRYPTIONWITHCOLUMNKEY?.PathInSchema ?? cc.MetaData?.PathInSchema ?? [])}' is encrypted and no key was given for it");
        return new ColumnDecryptor(key, _fileAad, rowGroup, column, _ctr);
    }
}
