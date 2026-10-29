using System;
using System.Collections.Generic;

namespace Parquet.Encryption;

/// <summary>
/// Writes a file with Parquet modular encryption (parquet-format Encryption.md): AES-GCM, with a footer key and
/// optionally separate keys for some columns. Keys are 16, 24 or 32 bytes (AES-128, AES-192, AES-256).
/// </summary>
public sealed class ParquetEncryptionOptions {
    /// <summary>The key the footer is encrypted (or, with <see cref="PlaintextFooter"/>, signed) with.</summary>
    public required byte[] FooterKey { get; set; }

    /// <summary>Stored in the file for readers to find the footer key with (a key ID, for instance).</summary>
    public byte[]? FooterKeyMetadata { get; set; }

    /// <summary>
    /// Keys for individual columns, by dotted path (<c>a.b.list.element</c>). When set, only these columns are
    /// encrypted and the others are written in plaintext; when null, every column is encrypted with the footer key.
    /// </summary>
    public IDictionary<string, ParquetColumnKey>? ColumnKeys { get; set; }

    /// <summary>
    /// A prefix bound into every module's additional authenticated data (a file name, for instance), so that a file
    /// cannot be passed off as another.
    /// </summary>
    public byte[]? AadPrefix { get; set; }

    /// <summary>Store <see cref="AadPrefix"/> in the file (default); when false, readers have to supply it.</summary>
    public bool StoreAadPrefix { get; set; } = true;

    /// <summary>
    /// Leave the footer readable (signed with the footer key rather than encrypted), so that readers without keys
    /// still see the schema and the plaintext columns.
    /// </summary>
    public bool PlaintextFooter { get; set; }

    /// <summary>Encrypt page data with AES-CTR (AES_GCM_CTR_V1: faster, page data unauthenticated) instead of AES-GCM.</summary>
    public bool UseCtrForPages { get; set; }
}

/// <summary>A column's own key, and what is stored for readers to find it with.</summary>
public sealed record ParquetColumnKey(byte[] Key, byte[]? KeyMetadata = null);

/// <summary>Keys for reading files with Parquet modular encryption.</summary>
public sealed class ParquetDecryptionOptions {
    /// <summary>The footer key; when null, <see cref="KeyRetriever"/> is asked with the footer's key metadata.</summary>
    public byte[]? FooterKey { get; set; }

    /// <summary>Column keys by dotted path; when a column has none here, <see cref="KeyRetriever"/> is asked.</summary>
    public IDictionary<string, byte[]>? ColumnKeys { get; set; }

    /// <summary>Finds a key from the key metadata stored in the file (null when the writer stored none). Returns null when it has no key.</summary>
    public Func<byte[]?, byte[]?>? KeyRetriever { get; set; }

    /// <summary>The AAD prefix, for files written without storing it.</summary>
    public byte[]? AadPrefix { get; set; }

    /// <summary>Check the signature of a plaintext footer when the footer key is available (default).</summary>
    public bool VerifyPlaintextFooter { get; set; } = true;
}
