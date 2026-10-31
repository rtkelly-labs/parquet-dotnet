using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IO;
using Parquet.Encodings;
using Parquet.Extensions;
using Parquet.Meta;
using Parquet.Schema;

namespace Parquet.File;

class DataColumnWriter {
    private readonly Stream _stream;
    private readonly ThriftFooter _footer;
    private readonly SchemaElement _schemaElement;
    private readonly Dictionary<string, string>? _keyValueMetadata;
    private readonly ParquetOptions _options;
    private readonly Encryption.ColumnEncryptor? _encryptor;
    private int _dataPageOrdinal;
    private static readonly RecyclableMemoryStreamManager _rmsMgr = new RecyclableMemoryStreamManager();

    public DataColumnWriter(
       Stream stream,
       ThriftFooter footer,
       SchemaElement schemaElement,
       ParquetOptions options,
       Dictionary<string, string>? keyValueMetadata,
       Encryption.ColumnEncryptor? encryptor = null) {
        _encryptor = encryptor;
        _stream = stream;
        _footer = footer;
        _schemaElement = schemaElement;
        _keyValueMetadata = keyValueMetadata;
        _options = options;
        _rmsMgr.Settings.MaximumSmallPoolFreeBytes = options.MaximumSmallPoolFreeBytes;
        _rmsMgr.Settings.MaximumLargePoolFreeBytes = options.MaximumLargePoolFreeBytes;
    }

    public async Task<ColumnChunk> WriteAsync<T>(
        FieldPath fullPath,
        WritingColumn<T> wc,
        CancellationToken cancellationToken) where T : struct {
        // Num_values in the chunk does include null values - I have validated this by dumping spark-generated file.
        ColumnChunk chunk = _footer.CreateColumnChunk(
            _options.CompressionMethod, _stream, _schemaElement.Type!.Value, fullPath, wc.NumValues,
            _keyValueMetadata);
        if(chunk.MetaData == null)
            throw new InvalidDataException($"{nameof(chunk.MetaData)} can not be null");

        ColumnMetrics metrics = await WriteAsync(
            chunk, wc, _schemaElement,
            cancellationToken);
        chunk.MetaData.Encodings = metrics.GetUsedEncodings();

        //generate stats for column chunk
        chunk.MetaData.Statistics = _options.WriteStatistics ? wc.Statistics.ToThriftStatistics(_schemaElement) : null;

        //the following counters must include both data size and header size
        chunk.MetaData.TotalCompressedSize = metrics.CompressedSize;
        chunk.MetaData.TotalUncompressedSize = metrics.UncompressedSize;

        return chunk;
    }

    class ColumnMetrics {
        public int CompressedSize;
        public int UncompressedSize;
        public readonly List<PageHeader> Pages = new();

        public List<Encoding> GetUsedEncodings() {
            var r = new HashSet<Encoding>();
            foreach(PageHeader page in Pages) {
                if(page.DictionaryPageHeader != null) {
                    r.Add(page.DictionaryPageHeader.Encoding);
                }
                if(page.DataPageHeader != null) {
                    r.Add(page.DataPageHeader.Encoding);
                    r.Add(page.DataPageHeader.DefinitionLevelEncoding);
                    r.Add(page.DataPageHeader.RepetitionLevelEncoding);
                }
                if(page.DataPageHeaderV2 != null) {
                    r.Add(page.DataPageHeaderV2.Encoding);
                }
            }
            return r.ToList();
        }
    }

    private async Task CompressAndWriteAsync(
        PageHeader ph, MemoryStream uncompressedData,
        ColumnMetrics cs,
        CancellationToken cancellationToken) {

        int uncompressedLength = (int)uncompressedData.Length;
        using IMemoryOwner<byte> compressed = await Compressor.Instance.CompressAsync(
            _options.CompressionMethod, _options.CompressionLevel, uncompressedData);

        // encrypted: the page and its header are each a module, and the page size is the module's
        bool dictionary = ph.Type == PageType.DICTIONARY_PAGE;
        int pageOrdinal = dictionary ? -1 : _dataPageOrdinal++;
        ReadOnlyMemory<byte> pageData = _encryptor == null
            ? compressed.Memory
            : _encryptor.Page(compressed.Memory.Span, dictionary, pageOrdinal);

        ph.UncompressedPageSize = uncompressedLength;
        ph.CompressedPageSize = pageData.Length;

        //write the header in
        using(MemoryStream headerMs = _rmsMgr.GetStream()) {
            ph.Write(new Meta.Proto.ThriftCompactProtocolWriter(headerMs));
            _stream.Flush();

            // write header
            int headerSize;
            if(_encryptor == null) {
                headerSize = (int)headerMs.Length;
                headerMs.Position = 0;
                await headerMs.CopyToAsync(_stream);
            } else {
                byte[] header = _encryptor.Header(headerMs.GetBuffer().AsSpan(0, (int)headerMs.Length), dictionary, pageOrdinal);
                headerSize = header.Length;
                await _stream.WriteAsync(header, 0, header.Length, cancellationToken);
            }

            cs.CompressedSize += headerSize;
            cs.UncompressedSize += headerSize;
        }

        // write data
        await _stream.WriteAsync(pageData, cancellationToken);

        cs.CompressedSize += ph.CompressedPageSize;
        cs.UncompressedSize += ph.UncompressedPageSize;
    }

    /// <summary>
    /// The stream a data page is encoded into. For fixed-width values the page's size is known within a little, so it
    /// is one contiguous buffer from the start: the compressor then reads it in place, where a stream of blocks was
    /// copied into one buffer first (GetBuffer). Byte arrays and strings keep the block stream.
    /// </summary>
    private MemoryStream GetPageStream<T>(WritingColumn<T> wc, int valueCount) where T : struct {
        if(typeof(T) == typeof(ReadOnlyMemory<char>) || typeof(T) == typeof(ReadOnlyMemory<byte>) || valueCount == 0)
            return _rmsMgr.GetStream();
        // values at most their CLR size (decimals and dates encode smaller), and levels in RLE well under a byte each
        long estimate = (long)valueCount * System.Runtime.CompilerServices.Unsafe.SizeOf<T>() + valueCount / 4 + 1024;
        return _rmsMgr.GetStream(null, estimate, asContiguousBuffer: true);
    }

    private async Task<ColumnMetrics> WriteAsync<T>(ColumnChunk chunk,
        WritingColumn<T> wc,
        SchemaElement tse,
        CancellationToken cancellationToken) where T : struct {

        wc.Field.EnsureAttachedToSchema(nameof(wc.Field));
        wc.Pack(_options);

        var r = new ColumnMetrics();

        /*
         * The page header must precede actual data (compressed or not) however, it contains both
         * the uncompressed and compressed data size which we don't know! This somehow limits
         * the writing efficiency.
         */

        // dictionary page
        if(wc.HasDictionary) {
            chunk.MetaData!.DictionaryPageOffset = _stream.Position;
            PageHeader ph = _footer.CreateDictionaryPage(wc.Dictionary.Length, out _);
            r.Pages.Add(ph);
            using MemoryStream ms = _rmsMgr.GetStream();
            ParquetPlainEncoder.Encode(wc.Dictionary, ms, tse, wc.Statistics);
            await CompressAndWriteAsync(ph, ms, r, cancellationToken);
        }

        // data pages: one per chunk unless MaxDataPageValues splits it, at row boundaries
        bool deltaEncode = _options.GetEncodingHint(wc.Field) == EncodingHint.DeltaBinaryPacked && DeltaBinaryPackedEncoder.CanEncode(wc.Values);
        bool byteSplitStreamEncode = _options.GetEncodingHint(wc.Field) == EncodingHint.ByteSplitStream && ByteStreamSplitEncoder.IsSupported(typeof(T));
        List<(int Start, int End)> pages = SplitPages(wc, _options.MaxDataPageValues);
        bool paged = pages.Count > 1;
        if(paged && !wc.HasDictionary) {
            // the chunk's statistics, once over every value; each page is then encoded without them
            ParquetPlainEncoder.Encode(wc.Values, Stream.Null, tse, wc.Statistics);
        }

        chunk.MetaData!.DataPageOffset = _stream.Position;
        int valueStart = 0;
        foreach((int start, int end) in pages) {
            int entries = end - start;
            int valueCount = wc.HasDefinitionLevels
                ? wc.DefinitionLevels.Slice(start, entries).Count(wc.Field.MaxDefinitionLevel)
                : entries;
            using MemoryStream ms = GetPageStream(wc, valueCount);

            // data page Num_values also does include NULLs
            PageHeader ph = _footer.CreateDataPage(entries, wc.HasDictionary, deltaEncode, byteSplitStreamEncode, out DataPageHeader dph);
            r.Pages.Add(ph);

            if(wc.HasRepetitionLevels) {
                WriteLevels(ms, wc.RepetitionLevels.Slice(start, entries), wc.Field.MaxRepetitionLevel);
            }
            if(wc.HasDefinitionLevels) {
                WriteLevels(ms, wc.DefinitionLevels.Slice(start, entries), wc.Field.MaxDefinitionLevel);
            }

            Data.DataColumnStatistics? stats = paged ? null : wc.Statistics;
            if(wc.HasDictionary) {
                // dictionary indexes are always encoded with RLE
                int bitWidth = wc.Dictionary.Length.GetBitWidth();  // bit width is determined by the dictionary size
                ms.WriteByte((byte)bitWidth);   // bit width is stored as 1 byte before encoded data
                RleBitpackedHybridEncoder.Encode(ms, wc.DictionaryIndexes.Slice(valueStart, valueCount), bitWidth);
            } else {
                ReadOnlySpan<T> values = wc.Values.Slice(valueStart, valueCount);
                if(deltaEncode) {
                    DeltaBinaryPackedEncoder.Encode(values, ms, stats);
                } else if(byteSplitStreamEncode) {
                    ByteStreamSplitEncoder.Encode(values, ms);
                    // the statistics the plain encoder computes; writing to Stream.Null costs nothing
                    if(stats != null)
                        ParquetPlainEncoder.Encode(values, Stream.Null, tse, stats);
                } else {
                    ParquetPlainEncoder.Encode(values, ms, tse, stats);
                }
            }
            valueStart += valueCount;

            // a page's statistics are the chunk's only when it is the chunk's one page
            dph.Statistics = _options.WriteStatistics && !paged ? wc.Statistics.ToThriftStatistics(tse) : null;
            await CompressAndWriteAsync(ph, ms, r, cancellationToken);
        }

        return r;
    }

    /// <summary>
    /// The level ranges of a chunk's data pages: one page, unless <paramref name="maxValues"/> is set and the chunk
    /// has more levels, in which case each page ends at the first row start (repetition level 0) at or after it.
    /// </summary>
    internal static List<(int Start, int End)> SplitPages<T>(WritingColumn<T> wc, int? maxValues) where T : struct {
        int total = wc.NumValues;
        var pages = new List<(int, int)>();
        if(maxValues is not int limit || limit <= 0 || total <= limit) {
            pages.Add((0, total));
            return pages;
        }
        ReadOnlySpan<int> repetition = wc.HasRepetitionLevels ? wc.RepetitionLevels : default;
        int start = 0;
        while(start < total) {
            int end = Math.Min(start + limit, total);
            if(!repetition.IsEmpty)
                while(end < total && repetition[end] != 0)
                    end++;
            pages.Add((start, end));
            start = end;
        }
        return pages;
    }

    private static void WriteLevels(Stream s, ReadOnlySpan<int> levels, int maxValue) {
        int bitWidth = maxValue.GetBitWidth();
        RleBitpackedHybridEncoder.EncodeWithLength(s, bitWidth, levels);
    }
}
