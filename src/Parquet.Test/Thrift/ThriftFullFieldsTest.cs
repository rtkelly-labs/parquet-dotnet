using System.Collections.Generic;
using System.IO;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Xunit;

namespace Parquet.Test.Thrift;

/// <summary>
/// Footer and page structures with every optional field set, including those the library never sets itself (v2 and
/// index page headers, histograms, Z/M bounds, AAD prefixes, footer signing keys), round-trip: each field is written
/// under its own id and read back into the same field.
/// </summary>
public class ThriftFullFieldsTest {

    delegate void Write<T>(T value, ThriftCompactProtocolWriter writer);

    static T RoundTrip<T>(T value, Write<T> write, System.Func<ThriftCompactProtocolReader, T> read) {
        using var ms = new MemoryStream();
        write(value, new ThriftCompactProtocolWriter(ms));
        long written = ms.Position;
        ms.Position = 0;
        T back = read(new ThriftCompactProtocolReader(ms));
        Assert.Equal(written, ms.Position);
        return back;
    }

    static Statistics FullStatistics() => new() {
        Max = [9], Min = [1], NullCount = 2, DistinctCount = 5, MaxValue = [8], MinValue = [2],
        IsMaxValueExact = true, IsMinValueExact = false, NanCount = 3,
    };

    [Fact]
    public void Page_header_with_v2_data_page_crc_and_index_page() {
        var header = new PageHeader {
            Type = PageType.DATA_PAGE_V2, UncompressedPageSize = 100, CompressedPageSize = 60, Crc = -12345,
            IndexPageHeader = new IndexPageHeader(),
            DataPageHeaderV2 = new DataPageHeaderV2 {
                NumValues = 10, NumNulls = 2, NumRows = 4, Encoding = Encoding.RLE_DICTIONARY,
                DefinitionLevelsByteLength = 7, RepetitionLevelsByteLength = 3, IsCompressed = false, Statistics = FullStatistics(),
            },
        };
        PageHeader back = RoundTrip(header, (v, w) => v.Write(w), PageHeader.Read);
        Assert.Equal((PageType.DATA_PAGE_V2, 100, 60, -12345), (back.Type, back.UncompressedPageSize, back.CompressedPageSize, back.Crc));
        Assert.NotNull(back.IndexPageHeader);
        DataPageHeaderV2 v2 = back.DataPageHeaderV2!;
        Assert.Equal((10, 2, 4, Encoding.RLE_DICTIONARY, 7, 3, (bool?)false),
            (v2.NumValues, v2.NumNulls, v2.NumRows, v2.Encoding, v2.DefinitionLevelsByteLength, v2.RepetitionLevelsByteLength, v2.IsCompressed));
        Statistics s = v2.Statistics!;
        Assert.Equal(((long?)2, (long?)5, (bool?)true, (bool?)false, (long?)3), (s.NullCount, s.DistinctCount, s.IsMaxValueExact, s.IsMinValueExact, s.NanCount));
        Assert.Equal(new byte[] { 9 }, s.Max);
        Assert.Equal(new byte[] { 1 }, s.Min);
        Assert.Equal(new byte[] { 8 }, s.MaxValue);
        Assert.Equal(new byte[] { 2 }, s.MinValue);
    }

    [Fact]
    public void Dictionary_page_header_sorted() {
        var header = new PageHeader {
            Type = PageType.DICTIONARY_PAGE, UncompressedPageSize = 8, CompressedPageSize = 8,
            DictionaryPageHeader = new DictionaryPageHeader { NumValues = 2, Encoding = Encoding.PLAIN, IsSorted = true },
        };
        Assert.True(RoundTrip(header, (v, w) => v.Write(w), PageHeader.Read).DictionaryPageHeader!.IsSorted);
    }

    [Fact]
    public void Column_index_with_histograms_and_nan_counts() {
        var index = new ColumnIndex {
            NullPages = [false, true], MinValues = [[1], []], MaxValues = [[9], []], BoundaryOrder = BoundaryOrder.ASCENDING,
            NullCounts = [0, 5], RepetitionLevelHistograms = [5, 0, 0, 5], DefinitionLevelHistograms = [0, 5, 5, 0], NanCounts = [1, 0],
        };
        ColumnIndex back = RoundTrip(index, (v, w) => v.Write(w), ColumnIndex.Read);
        Assert.Equal(index.NullPages, back.NullPages);
        Assert.Equal(index.MinValues, back.MinValues);
        Assert.Equal(index.MaxValues, back.MaxValues);
        Assert.Equal(BoundaryOrder.ASCENDING, back.BoundaryOrder);
        Assert.Equal(index.NullCounts, back.NullCounts);
        Assert.Equal(index.RepetitionLevelHistograms, back.RepetitionLevelHistograms);
        Assert.Equal(index.DefinitionLevelHistograms, back.DefinitionLevelHistograms);
        Assert.Equal(index.NanCounts, back.NanCounts);
    }

    [Fact]
    public void Offset_index_with_unencoded_byte_array_sizes() {
        var index = new OffsetIndex {
            PageLocations = [new PageLocation { Offset = 4, CompressedPageSize = 100, FirstRowIndex = 0 }, new PageLocation { Offset = 104, CompressedPageSize = 50, FirstRowIndex = 10 }],
            UnencodedByteArrayDataBytes = [1000, 500],
        };
        OffsetIndex back = RoundTrip(index, (v, w) => v.Write(w), OffsetIndex.Read);
        Assert.Equal(2, back.PageLocations.Count);
        Assert.Equal((104L, 50, 10L), (back.PageLocations[1].Offset, back.PageLocations[1].CompressedPageSize, back.PageLocations[1].FirstRowIndex));
        Assert.Equal(index.UnencodedByteArrayDataBytes, back.UnencodedByteArrayDataBytes);
    }

    [Fact]
    public void Bounding_box_with_z_and_m() {
        var box = new BoundingBox { Xmin = -1, Xmax = 1, Ymin = -2, Ymax = 2, Zmin = -3, Zmax = 3, Mmin = -4.5, Mmax = 4.5 };
        BoundingBox back = RoundTrip(box, (v, w) => v.Write(w), BoundingBox.Read);
        Assert.Equal((-1d, 1d, -2d, 2d, (double?)-3, (double?)3, (double?)-4.5, (double?)4.5),
            (back.Xmin, back.Xmax, back.Ymin, back.Ymax, back.Zmin, back.Zmax, back.Mmin, back.Mmax));
    }

    [Fact]
    public void Encryption_algorithms_with_every_aad_field() {
        foreach(bool ctr in new[] { false, true }) {
            var algorithm = ctr
                ? new EncryptionAlgorithm { AESGCMCTRV1 = new AesGcmCtrV1 { AadPrefix = [1, 2], AadFileUnique = [3, 4], SupplyAadPrefix = true } }
                : new EncryptionAlgorithm { AESGCMV1 = new AesGcmV1 { AadPrefix = [1, 2], AadFileUnique = [3, 4], SupplyAadPrefix = true } };
            var crypto = new FileCryptoMetaData { EncryptionAlgorithm = algorithm, KeyMetadata = [7, 7] };
            FileCryptoMetaData back = RoundTrip(crypto, (v, w) => v.Write(w), FileCryptoMetaData.Read);
            Assert.Equal(new byte[] { 7, 7 }, back.KeyMetadata);
            (byte[]? prefix, byte[]? unique, bool? supply) = ctr
                ? (back.EncryptionAlgorithm.AESGCMCTRV1!.AadPrefix, back.EncryptionAlgorithm.AESGCMCTRV1.AadFileUnique, back.EncryptionAlgorithm.AESGCMCTRV1.SupplyAadPrefix)
                : (back.EncryptionAlgorithm.AESGCMV1!.AadPrefix, back.EncryptionAlgorithm.AESGCMV1.AadFileUnique, back.EncryptionAlgorithm.AESGCMV1.SupplyAadPrefix);
            Assert.Equal(new byte[] { 1, 2 }, prefix);
            Assert.Equal(new byte[] { 3, 4 }, unique);
            Assert.True(supply);
        }
    }

    [Fact]
    public void Footer_with_file_path_index_page_offset_and_signing_key() {
        var meta = new FileMetaData {
            Version = 2, NumRows = 1, Schema = [new SchemaElement { Name = "schema", NumChildren = 1 }, new SchemaElement { Name = "x", Type = Type.INT32 }],
            RowGroups = [new RowGroup {
                NumRows = 1, TotalByteSize = 10,
                Columns = [new ColumnChunk {
                    FilePath = "other.parquet", FileOffset = 4,
                    MetaData = new ColumnMetaData {
                        Type = Type.INT32, Encodings = [Encoding.PLAIN], PathInSchema = ["x"], Codec = CompressionCodec.UNCOMPRESSED,
                        NumValues = 1, TotalUncompressedSize = 10, TotalCompressedSize = 10, DataPageOffset = 4, IndexPageOffset = 2,
                        Statistics = FullStatistics(),
                    },
                }],
            }],
            EncryptionAlgorithm = new EncryptionAlgorithm { AESGCMV1 = new AesGcmV1 { AadFileUnique = [5] } },
            FooterSigningKeyMetadata = [9, 9, 9],
        };
        FileMetaData back = RoundTrip(meta, (v, w) => v.Write(w), FileMetaData.Read);
        ColumnChunk chunk = back.RowGroups[0].Columns[0];
        Assert.Equal(("other.parquet", (long?)2, (long?)5), (chunk.FilePath, chunk.MetaData!.IndexPageOffset, chunk.MetaData.Statistics!.DistinctCount));
        Assert.Equal(new byte[] { 9, 9, 9 }, back.FooterSigningKeyMetadata);
        Assert.Equal(new byte[] { 5 }, back.EncryptionAlgorithm!.AESGCMV1!.AadFileUnique);
    }
}
