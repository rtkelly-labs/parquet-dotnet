using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.HighPerformance.Buffers;
using Microsoft.IO;
using Parquet.Data;
using Parquet.Encodings;
using Parquet.Extensions;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;

namespace Parquet.File;

/// <summary>
/// Reader for Parquet data column
/// </summary>
class DataColumnReader {
    private readonly DataField _dataField;
    private readonly Stream _inputStream;
    private readonly ColumnChunk _thriftColumnChunk;
    private readonly SchemaElement? _schemaElement;
    private readonly ThriftFooter _footer;
    private readonly ParquetOptions _options;
    private readonly DataColumnStatistics? _stats;
    private readonly CompressionMethod _compressionMethod;
    private readonly Encryption.ColumnDecryptor? _decryptor;
    private bool _pageIsDictionary;
    private int _dataPageOrdinal;
    private static readonly RecyclableMemoryStreamManager _rmsMgr = new RecyclableMemoryStreamManager();

    internal DataColumnReader(
       DataField dataField,
       Stream inputStream,
       ColumnChunk thriftColumnChunk,
       DataColumnStatistics? stats,
       ThriftFooter footer,
       ParquetOptions? parquetOptions,
       Encryption.ColumnDecryptor? decryptor = null) {
        _decryptor = decryptor;
        _dataField = dataField ?? throw new ArgumentNullException(nameof(dataField));
        _inputStream = inputStream ?? throw new ArgumentNullException(nameof(inputStream));
        _thriftColumnChunk = thriftColumnChunk ?? throw new ArgumentNullException(nameof(thriftColumnChunk));
        _stats = stats;
        _compressionMethod = (CompressionMethod)(int)(thriftColumnChunk.MetaData?.Codec ?? CompressionCodec.UNCOMPRESSED);
        _footer = footer ?? throw new ArgumentNullException(nameof(footer));
        _options = parquetOptions ?? throw new ArgumentNullException(nameof(parquetOptions));

        dataField.EnsureAttachedToSchema(nameof(dataField));

        _schemaElement = _footer.GetSchemaElement(_thriftColumnChunk);

        // parquetOptions is guaranteed non-null due to earlier null check.
        _rmsMgr.Settings.MaximumSmallPoolFreeBytes = parquetOptions.MaximumSmallPoolFreeBytes;
        _rmsMgr.Settings.MaximumLargePoolFreeBytes = parquetOptions.MaximumLargePoolFreeBytes;
    }

    /// <summary>
    /// Return data column statistics
    /// </summary>
    /// <returns>Data column statistics or null</returns>
    public DataColumnStatistics? GetColumnStatistics() => _stats;

    public async ValueTask ReadAsync<T>(ReadingColumn<T> rc, CancellationToken cancellationToken) where T : struct {
        // how many values are in column chunk, as there may be multiple data pages
        int totalValuesInChunk = (int)_thriftColumnChunk.MetaData!.NumValues;
        int definedValuesCount = totalValuesInChunk;
        if(_stats?.NullCount != null)
            definedValuesCount -= (int)_stats.NullCount.Value;

        // an empty chunk (a row group of no rows) may have no pages and no page offsets at all (parquet-cpp)
        if(totalValuesInChunk == 0)
            return;

        long fileOffset = GetFileOffset();
        if(fileOffset < 0 || fileOffset >= _inputStream.Length)
            throw new ParquetException($"column '{_dataField.Path}' starts at {fileOffset}, outside the file of {_inputStream.Length} bytes, file is corrupt");
        long pageOffset = fileOffset;
        // encrypted: a page header's AAD depends on whether it is the dictionary page's, which comes first when there is one
        bool dictionaryFirst = _thriftColumnChunk.MetaData.DictionaryPageOffset is > 0;

        while(rc.ValuesRead < totalValuesInChunk) {
            // use absolute positioning on every page read, because in some edge cases page reader may not exhaust or over-read page data
            _inputStream.Seek(pageOffset, SeekOrigin.Begin);
            _pageIsDictionary = dictionaryFirst && pageOffset == fileOffset;
            PageHeader ph = _decryptor == null
                ? PageHeader.Read(new ThriftCompactProtocolReader(_inputStream))
                : _decryptor.ReadHeader(_inputStream, _pageIsDictionary, _dataPageOrdinal);
            // page buffers are sized by these: the page must fit in what is left of the file
            if(ph.CompressedPageSize < 0 || ph.CompressedPageSize > _inputStream.Length - _inputStream.Position || ph.UncompressedPageSize < 0)
                throw new ParquetException($"column '{_dataField.Path}': page sizes {ph.CompressedPageSize} (compressed) and {ph.UncompressedPageSize} (uncompressed) do not fit in the file, file is corrupt");
            pageOffset = _inputStream.Position + ph.CompressedPageSize;

            switch(ph.Type) {
                case PageType.DICTIONARY_PAGE:
                    await ReadDictionaryPageAsync(ph, rc, cancellationToken);
                    break;
                case PageType.DATA_PAGE:
                    await ReadDataPageV1Async(ph, rc, cancellationToken);
                    break;
                case PageType.DATA_PAGE_V2:
                    await ReadDataPageV2Async(ph, rc, totalValuesInChunk, cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"can't read page type {ph.Type}");
            }
            if(ph.Type != PageType.DICTIONARY_PAGE)
                _dataPageOrdinal++;
        }
    }

    private async ValueTask<IMemoryOwner<byte>> ReadPageDataAsync(PageHeader ph) {
        if(_decryptor != null) {
            using var ms = new MemoryStream(await ReadEncryptedPageAsync(ph), false);
            return await Compressor.Instance.Decompress(_compressionMethod, ms, ph.UncompressedPageSize);
        }
        Stream src = _inputStream.Sub(_inputStream.Position, ph.CompressedPageSize);
        return await Compressor.Instance.Decompress(_compressionMethod, src, ph.UncompressedPageSize);
    }

    /// <summary>The page's (compressed) bytes, decrypted.</summary>
    private async ValueTask<byte[]> ReadEncryptedPageAsync(PageHeader ph) {
        byte[] module = await _inputStream.ReadBytesExactlyAsync(ph.CompressedPageSize);
        return _decryptor!.Page(module, _pageIsDictionary, _dataPageOrdinal);
    }

    private async ValueTask ReadDictionaryPageAsync<T>(PageHeader ph, ReadingColumn<T> rc, CancellationToken cancellationToken) where T : struct {

        if(rc.HasDictionary)
            throw new InvalidOperationException("dictionary already read");

        //Dictionary page format: the entries in the dictionary - in dictionary order - using the plain encoding.
        using IMemoryOwner<byte> bytes = await ReadPageDataAsync(ph);

        // the dictionary is sized by its count: each plain-encoded value takes at least its physical width
        if(ph.DictionaryPageHeader!.NumValues < 0 || ph.DictionaryPageHeader.NumValues * MinPlainBits(_schemaElement!) > bytes.Memory.Length * 8L)
            throw new ParquetException($"column '{_dataField.Path}': dictionary page of {bytes.Memory.Length} bytes claims {ph.DictionaryPageHeader.NumValues} values, file is corrupt");

        // Dictionary should not contain null values
        Span<T> dictionary = rc.AllocateDictionary(ph.DictionaryPageHeader!.NumValues);

        ParquetPlainEncoder.Decode(dictionary, ph.DictionaryPageHeader.NumValues,
               _schemaElement!, bytes.Memory.Span, out int dictionaryOffset);
        // entries not decoded would be whatever the pooled buffer held
        if(dictionaryOffset != ph.DictionaryPageHeader.NumValues)
            throw new ParquetException($"column '{_dataField.Path}': dictionary page holds {dictionaryOffset} of the {ph.DictionaryPageHeader.NumValues} values it claims, file is corrupt");
    }

    /// <summary>The fewest bits a PLAIN value of this physical type takes: BOOLEAN one, BYTE_ARRAY its length prefix.</summary>
    private static long MinPlainBits(SchemaElement se) => se.Type switch {
        Meta.Type.INT32 or Meta.Type.FLOAT or Meta.Type.BYTE_ARRAY => 32,
        Meta.Type.INT64 or Meta.Type.DOUBLE => 64,
        Meta.Type.INT96 => 96,
        Meta.Type.FIXED_LEN_BYTE_ARRAY => Math.Max(1, (se.TypeLength ?? 0) * 8L),
        _ => 1
    };

    private long GetFileOffset() =>
        // get the minimum offset, we'll just read pages in sequence as DictionaryPageOffset/Data_page_offset are not reliable
        new[]
            {
                _thriftColumnChunk.MetaData?.DictionaryPageOffset ?? 0,
                _thriftColumnChunk.MetaData!.DataPageOffset,
                _thriftColumnChunk.MetaData?.IndexPageOffset ?? 0
            }
            .Where(e => e != 0)
            .DefaultIfEmpty(-1)
            .Min();

    private async ValueTask ReadDataPageV1Async<T>(PageHeader ph, ReadingColumn<T> rc, CancellationToken cancellationToken) where T : struct {
        using IMemoryOwner<byte> bytes = await ReadPageDataAsync(ph);

        if(ph.DataPageHeader == null) {
            throw new ParquetException($"column '{_dataField.Path}' is missing data page header, file is corrupt");
        }

        int dataUsed = 0;
        int allValueCount = (int)_thriftColumnChunk.MetaData!.NumValues;
        int pageValueCount = ph.DataPageHeader.NumValues;
        CheckPageValueCount(pageValueCount, allValueCount, rc);

        if(_dataField.MaxRepetitionLevel > 0) {
            int levelsRead = ReadLevels(
                bytes.Memory.Span, _dataField.MaxRepetitionLevel,
                rc.RepetitionLevelsToReadInto,
                pageValueCount, null, out int usedLength);
            rc.MarkRepetitionLevels(levelsRead);
            dataUsed += usedLength;
        }

        int defNulls = 0;
        if(_dataField.MaxDefinitionLevel > 0) {
            int levelsRead = ReadLevels(
                bytes.Memory.Span.Slice(dataUsed), _dataField.MaxDefinitionLevel,
                rc.DefinitionLevelsToReadInto,
                pageValueCount, null, out int usedLength);
            dataUsed += usedLength;
            defNulls = rc.MarkDefinitionLevels(levelsRead, _dataField.MaxDefinitionLevel);
        }

        // try to be clever to detect how many elements to read
        int dataElementCount = pageValueCount - defNulls;

        ReadColumn(
            bytes.Memory.Span.Slice(dataUsed),
            ph.DataPageHeader.Encoding,
            allValueCount, dataElementCount,
            rc);
    }

    private async ValueTask ReadDataPageV2Async<T>(PageHeader ph, ReadingColumn<T> rc, long maxValues, CancellationToken cancellationToken) where T : struct {
        if(ph.DataPageHeaderV2 == null) {
            throw new ParquetException($"column '{_dataField.Path}' is missing data page header, file is corrupt");
        }

        MemoryOwner<byte> pageMemory;
        if(_decryptor != null) {
            byte[] page = await ReadEncryptedPageAsync(ph);
            pageMemory = MemoryOwner<byte>.Allocate(page.Length);
            page.CopyTo(pageMemory.Span);
        } else {
            pageMemory = MemoryOwner<byte>.Allocate(ph.CompressedPageSize);
            await using Stream src = _inputStream.Sub(_inputStream.Position, ph.CompressedPageSize);
            int copied = await src.CopyToAsync(pageMemory.Memory, cancellationToken);
            if(copied < ph.CompressedPageSize) {
                pageMemory.Dispose();
                throw new EndOfStreamException($"column '{_dataField.Path}': page has {copied} bytes, but its header declares {ph.CompressedPageSize}. File is most likely truncated or corrupt.");
            }
        }
        using MemoryOwner<byte> pageMemoryOwner = pageMemory;
        // v2 pages: repetition then definition levels (RLE, uncompressed, without a length prefix: the lengths are
        // in the header), then the values, which alone are compressed
        int repLength = ph.DataPageHeaderV2.RepetitionLevelsByteLength;
        int defLength = ph.DataPageHeaderV2.DefinitionLevelsByteLength;
        if(repLength < 0 || defLength < 0 || (long)repLength + defLength > Math.Min(pageMemory.Length, ph.UncompressedPageSize))
            throw new ParquetException($"column '{_dataField.Path}': level lengths {repLength} and {defLength} do not fit in the page, file is corrupt");
        int dataUsed = repLength + defLength;
        CheckPageValueCount(ph.DataPageHeaderV2.NumValues, maxValues, rc);

        if(_dataField.MaxRepetitionLevel > 0) {
            int levelsRead = ReadLevels(pageMemory.Span.Slice(0, repLength),
                _dataField.MaxRepetitionLevel, rc.RepetitionLevelsToReadInto,
                ph.DataPageHeaderV2.NumValues, repLength, out _);
            rc.MarkRepetitionLevels(levelsRead);
        }

        int nulls = 0;
        if(_dataField.MaxDefinitionLevel > 0) {
            int levelsRead = ReadLevels(
                pageMemory.Span.Slice(repLength, defLength), _dataField.MaxDefinitionLevel,
                rc.DefinitionLevelsToReadInto,
                ph.DataPageHeaderV2.NumValues, defLength, out _);
            nulls = rc.MarkDefinitionLevels(levelsRead, _dataField.MaxDefinitionLevel);
        }

        // values present: NumNulls counts every level below the maximum (empty and null lists too)
        int maxReadCount = ph.DataPageHeaderV2.NumValues - (_dataField.MaxDefinitionLevel > 0 ? nulls : ph.DataPageHeaderV2.NumNulls);
        if(maxReadCount < 0 || maxReadCount > ph.DataPageHeaderV2.NumValues)
            throw new ParquetException($"column '{_dataField.Path}': page has {ph.DataPageHeaderV2.NumValues} values and {ph.DataPageHeaderV2.NumNulls} nulls, file is corrupt");

        if(ph.DataPageHeaderV2.IsCompressed == false || _thriftColumnChunk.MetaData!.Codec == CompressionCodec.UNCOMPRESSED) {
            ReadColumn(pageMemory.Span.Slice(dataUsed), ph.DataPageHeaderV2.Encoding, maxValues, maxReadCount, rc);
            return;
        }

        int decompressedSize = ph.UncompressedPageSize - repLength - defLength;

        // decompress into rented memory
        using IMemoryOwner<byte> decompressedDataMemory = await Compressor.Instance.Decompress(
            _compressionMethod,
            pageMemory.Memory.Sub(dataUsed, pageMemory.Length - dataUsed),
            decompressedSize);

        ReadColumn(decompressedDataMemory.Memory.Span,
            ph.DataPageHeaderV2.Encoding,
            maxValues, maxReadCount,
            rc);
    }

    /// <summary>A page's values are read into the column's buffers, which hold the column chunk's values.</summary>
    private void CheckPageValueCount<T>(int pageValueCount, long chunkValueCount, ReadingColumn<T> rc) where T : struct {
        if(pageValueCount < 0 || pageValueCount > chunkValueCount - rc.ValuesRead)
            throw new ParquetException($"column '{_dataField.Path}': page has {pageValueCount} values, but only {chunkValueCount - rc.ValuesRead} of the column chunk's {chunkValueCount} are left, file is corrupt");
    }

    private int ReadLevels(Span<byte> s, int maxLevel,
        Span<int> dest,
        int pageSize,
        int? length, out int usedLength) {

        int bitWidth = maxLevel.GetBitWidth();

        return RleBitpackedHybridEncoder.Decode(s, bitWidth, length, out usedLength, dest, pageSize);
    }

    private void ReadColumn<T>(Span<byte> src,
        Encoding encoding, long totalValuesInChunk, int totalValuesInPage,
        ReadingColumn<T> rc)
        where T : struct {
        //dictionary encoding uses RLE to encode data

        switch(encoding) {
            case Encoding.PLAIN: { // 0
                    ParquetPlainEncoder.Decode(rc.ValuesToReadInto,
                        totalValuesInPage,
                        _schemaElement!, src, out int read);
                    rc.MarkValuesRead(read);
                }
                break;

            case Encoding.PLAIN_DICTIONARY: // 2  // values are still encoded in RLE
            case Encoding.RLE_DICTIONARY: { // 8
                    Span<int> span = rc.AllocateOrGetDictionaryIndexes(totalValuesInPage);
                    int indexCount = ReadRleDictionary(src, totalValuesInPage, span);
                    rc.MarkDictionaryIndexesRead(indexCount);
                    rc.Checkpoint();
                }
                break;

            case Encoding.RLE: { // 3
                    if(_dataField.ClrType == typeof(bool)) {
                        // for boolean values, we need to read into temporary int buffer and convert to booleans.
                        // todo: we can optimise this by implementing boolean RLE decoder
                        Span<bool> dest = rc.ValuesToReadInto.AsSpan<T, bool>();

                        // RLE booleans are one bit wide and, in data pages, prefixed with their length
                        int[] tmp = new int[dest.Length];
                        int read = RleBitpackedHybridEncoder.Decode(src,
                            1,
                            null, out int usedLength, tmp.AsSpan(), totalValuesInPage);

                        // copy back to bool array
                        for(int i = 0; i < read; i++) {
                            dest[i] = tmp[i] == 1;
                        }

                        rc.MarkValuesRead(read);
                    } else {
                        Span<int> dest = rc.ValuesToReadInto.AsSpan<T, int>();
                        int read = RleBitpackedHybridEncoder.Decode(src,
                            _schemaElement!.TypeLength ?? 0,
                            src.Length, out int usedLength, dest, totalValuesInPage);
                        rc.MarkValuesRead(read);
                    }
                }
                break;

            case Encoding.DELTA_BINARY_PACKED when !DeltaBinaryPackedEncoder.IsSupported(typeof(T)):
            case Encoding.BYTE_STREAM_SPLIT when !ByteStreamSplitEncoder.IsSupported(typeof(T)):
            case Encoding.DELTA_LENGTH_BYTE_ARRAY or Encoding.DELTA_BYTE_ARRAY
                when typeof(T) != typeof(ReadOnlyMemory<byte>) && typeof(T) != typeof(ReadOnlyMemory<char>):
                ReadThroughPlain(src, encoding, totalValuesInPage, rc);
                break;

            case Encoding.DELTA_BINARY_PACKED: {// 5
                    int read = DeltaBinaryPackedEncoder.Decode(src, rc.ValuesToReadInto, totalValuesInPage, out _);
                    rc.MarkValuesRead(read);
                }
                break;

            case Encoding.DELTA_LENGTH_BYTE_ARRAY: {  // 6
                    int read = DeltaLengthByteArrayEncoder.Decode(src, rc.ValuesToReadInto, totalValuesInPage);
                    rc.MarkValuesRead(read);
                }
                break;

            case Encoding.DELTA_BYTE_ARRAY: {         // 7
                    int read = DeltaByteArrayEncoder.Decode(src, rc.ValuesToReadInto, totalValuesInPage);
                    rc.MarkValuesRead(read);
                }
                break;

            case Encoding.BYTE_STREAM_SPLIT: {       // 9
                    ByteStreamSplitEncoder.Decode(src, rc.ValuesToReadInto.Slice(0, totalValuesInPage));
                    rc.MarkValuesRead(totalValuesInPage);
                }
                break;
            case Encoding.BIT_PACKED:                // 4 (deprecated)
            default:
                throw new ParquetException($"encoding {encoding} is not supported.");
        }
    }

    /// <summary>
    /// Columns whose CLR type is not the physical type (timestamps, dates, decimals, int8/uint8, UUID, fixed
    /// bytes) under an encoding whose decoder only produces physical values: decodes the physical values,
    /// lays them out as PLAIN, and lets the PLAIN decoder convert them as it does for PLAIN pages. Other
    /// writers (parquet-mr's v2 writer, Arrow) use these encodings for such columns.
    /// </summary>
    private void ReadThroughPlain<T>(Span<byte> src, Encoding encoding, int count, ReadingColumn<T> rc) where T : struct {
        byte[] plain = TranscodeToPlain(src, encoding, count);
        ParquetPlainEncoder.Decode(rc.ValuesToReadInto, count, _schemaElement!, plain, out int read);
        rc.MarkValuesRead(read);
    }

    private byte[] TranscodeToPlain(Span<byte> src, Encoding encoding, int count) {
        switch(_schemaElement!.Type) {
            case Meta.Type.INT32: {
                    int[] values = new int[count];
                    if(encoding == Encoding.DELTA_BINARY_PACKED) {
                        DeltaBinaryPackedEncoder.Decode<int>(src, values, count, out _);
                    } else {
                        ByteStreamSplitEncoder.Decode<int>(src, values);
                    }
                    return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
                }
            case Meta.Type.INT64: {
                    long[] values = new long[count];
                    if(encoding == Encoding.DELTA_BINARY_PACKED) {
                        DeltaBinaryPackedEncoder.Decode<long>(src, values, count, out _);
                    } else {
                        ByteStreamSplitEncoder.Decode<long>(src, values);
                    }
                    return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
                }
            case Meta.Type.FIXED_LEN_BYTE_ARRAY when encoding == Encoding.BYTE_STREAM_SPLIT: {
                    // stream j holds byte j of every value
                    int k = _schemaElement.TypeLength ?? throw new ParquetException($"{_dataField.Path} has no type length");
                    byte[] plain = new byte[count * k];
                    for(int i = 0; i < count; i++) {
                        for(int j = 0; j < k; j++) {
                            plain[(i * k) + j] = src[(j * count) + i];
                        }
                    }
                    return plain;
                }
            case Meta.Type.FIXED_LEN_BYTE_ARRAY or Meta.Type.BYTE_ARRAY
                when encoding is Encoding.DELTA_BYTE_ARRAY or Encoding.DELTA_LENGTH_BYTE_ARRAY: {
                    var values = new ReadOnlyMemory<byte>[count];
                    int read = encoding == Encoding.DELTA_BYTE_ARRAY
                        ? DeltaByteArrayEncoder.Decode<ReadOnlyMemory<byte>>(src, values, count)
                        : DeltaLengthByteArrayEncoder.Decode<ReadOnlyMemory<byte>>(src, values, count);
                    // PLAIN: fixed-length values back to back; variable-length ones each after a 4-byte length
                    bool fixedLength = _schemaElement.Type == Meta.Type.FIXED_LEN_BYTE_ARRAY;
                    using var plain = new MemoryStream();
                    Span<byte> length = stackalloc byte[sizeof(int)];
                    for(int i = 0; i < read; i++) {
                        if(!fixedLength) {
                            BinaryPrimitives.WriteInt32LittleEndian(length, values[i].Length);
                            plain.Write(length);
                        }
                        plain.Write(values[i].Span);
                    }
                    return plain.ToArray();
                }
            default:
                throw new ParquetException($"encoding {encoding} is not supported for {_schemaElement.Type} column {_dataField.Path} ({_dataField.ClrType})");
        }
    }

    private static int ReadRleDictionary(Span<byte> s, int maxReadCount, Span<int> dest) {
        int offset = 0;
        int destOffset = 0;
        int start = destOffset;
        int bitWidth = s[offset++];

        int length = s.Length - 1;

        //when bit width is zero reader must stop and just repeat zero maxValue number of times
        if(bitWidth == 0 || length == 0) {
            for(int i = 0; i < maxReadCount; i++) {
                dest[destOffset++] = 0;
            }
        } else {
            if(length != 0) {
                destOffset += RleBitpackedHybridEncoder.Decode(s.Slice(1), bitWidth, length, out int usedLength, dest, maxReadCount);
            }
        }

        return destOffset - start;
    }
}
