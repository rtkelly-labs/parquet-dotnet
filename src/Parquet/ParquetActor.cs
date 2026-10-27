using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Parquet.Extensions;

namespace Parquet;

/// <summary>
/// Base class for reader and writer
/// </summary>
public class ParquetActor {
    internal static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("PAR1");
    internal static readonly byte[] EncryptedMagicBytes = Encoding.ASCII.GetBytes("PARE");
    private readonly Stream _fileStream;

    internal ParquetActor(Stream? fileStream) =>
        _fileStream = fileStream ?? throw new ArgumentNullException(nameof(fileStream));

    /// <summary>
    /// Original stream to write or read
    /// </summary>
    protected Stream Stream => _fileStream;

    /// <summary>
    /// Validates that this file is a valid parquet file by reading head and tail of it
    /// </summary>
    /// <returns></returns>
    /// <exception cref="IOException"></exception>
    protected async Task ValidateFileAsync() {
        _fileStream.Seek(0, SeekOrigin.Begin);
        byte[] head = await _fileStream.ReadBytesExactlyAsync(4);

        _fileStream.Seek(-4, SeekOrigin.End);
        byte[] tail = await _fileStream.ReadBytesExactlyAsync(4);

        bool plain = MagicBytes.SequenceEqual(head) && MagicBytes.SequenceEqual(tail);
        bool encrypted = EncryptedMagicBytes.SequenceEqual(head) && EncryptedMagicBytes.SequenceEqual(tail);
        if(!plain && !encrypted)
            throw new IOException($"not a parquet file, head: {head.ToHexString()}, tail: {tail.ToHexString()}");
        HasEncryptedFooter = encrypted;
    }

    /// <summary>The file ends in "PARE": its footer is encrypted.</summary>
    internal bool HasEncryptedFooter { get; private set; }

    /// <summary>Decrypts the file's columns, when it has encrypted ones (set by <see cref="ReadMetadataAsync"/>).</summary>
    internal Encryption.FileDecryptor? Decryptor { get; private set; }

    internal async ValueTask<Parquet.Meta.FileMetaData> ReadMetadataAsync(CancellationToken cancellationToken = default,
        Encryption.ParquetDecryptionOptions? decryption = null) {
        int footerLength = await GoBeforeFooterAsync();
        byte[] footerData = await _fileStream.ReadBytesExactlyAsync(footerLength);
        if(HasEncryptedFooter) {
            (Meta.FileMetaData meta, Encryption.FileDecryptor decryptor) = Encryption.FileDecryptor.ReadEncryptedFooter(footerData, decryption);
            Decryptor = decryptor;
            return meta;
        }
        using var ms = new MemoryStream(footerData);
        Meta.FileMetaData result = Parquet.Meta.FileMetaData.Read(new Meta.Proto.ThriftCompactProtocolReader(ms));
        if(result.EncryptionAlgorithm != null)
            Decryptor = Encryption.FileDecryptor.ForPlaintextFooter(result, footerData, (int)ms.Position, decryption);
        return result;
    }

    internal async ValueTask<int> GoBeforeFooterAsync() {
        //go to -4 bytes (PAR1) -4 bytes (footer length number)
        _fileStream.Seek(-8, SeekOrigin.End);
        int footerLength = await _fileStream.ReadInt32Async();

        //set just before footer starts
        _fileStream.Seek(-8 - footerLength, SeekOrigin.End);

        return footerLength;
    }
}