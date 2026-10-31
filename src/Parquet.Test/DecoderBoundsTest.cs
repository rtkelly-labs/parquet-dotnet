using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Parquet.Encodings;
using Parquet.Meta;
using Parquet.Meta.Proto;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// Counts and lengths in pages checked against the data they index (docs/changes/0104-decoder-bounds-checks.md), for
/// corrupt pages the malformed-file corpus does not generate.
/// </summary>
public class DecoderBoundsTest {

    static readonly DataField<string> S = new("s");

    /// <summary>A dictionary-encoded string column of two entries, 8 bytes and 1 byte long, with nulls.</summary>
    static async Task<byte[]> WriteDictionaryColumnAsync() {
        var ms = new MemoryStream();
        var options = new ParquetOptions { ColumnEncodingHints = { ["s"] = EncodingHint.Dictionary } };
        await using(ParquetWriter w = await ParquetWriter.CreateAsync(new ParquetSchema(S), ms, options)) {
            using ParquetRowGroupWriter rg = w.CreateRowGroup();
            await rg.WriteAsync(S, Enumerable.Range(0, 100).Select(i => i % 5 == 0 ? null : i % 2 == 0 ? "aaaaaaaa" : "b").ToArray());
        }
        return ms.ToArray();
    }

    static async Task<ColumnMetaData> ColumnAsync(byte[] file) {
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        return reader.Metadata!.RowGroups[0].Columns[0].MetaData!;
    }

    static async Task<string?[]> ReadAsync(byte[] file) {
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(file));
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(0);
        string?[] values = new string?[rg.RowCount];
        await rg.ReadAsync(S, values);
        return values;
    }

    [Fact]
    public async Task The_unedited_file_reads() {
        string?[] values = await ReadAsync(await WriteDictionaryColumnAsync());
        Assert.Equal(Enumerable.Range(0, 100).Select(i => i % 5 == 0 ? null : i % 2 == 0 ? "aaaaaaaa" : "b"), values);
    }

    [Fact]
    public async Task Dictionary_encoded_page_without_a_dictionary_page_fails() {
        byte[] file = await WriteDictionaryColumnAsync();

        // the footer no longer points at the dictionary page, so reading starts at the data page
        int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(file.Length - 8));
        int start = file.Length - 8 - length;
        FileMetaData meta = FileMetaData.Read(new ThriftCompactProtocolReader(new MemoryStream(file, start, length)));
        Assert.NotNull(meta.RowGroups[0].Columns[0].MetaData!.DictionaryPageOffset);
        meta.RowGroups[0].Columns[0].MetaData!.DictionaryPageOffset = null;
        var footer = new MemoryStream();
        meta.Write(new ThriftCompactProtocolWriter(footer));
        byte[] edited = [.. file.AsSpan(0, start), .. footer.ToArray(), 0, 0, 0, 0, .. "PAR1"u8];
        BinaryPrimitives.WriteInt32LittleEndian(edited.AsSpan(edited.Length - 8), (int)footer.Length);

        await Assert.ThrowsAsync<ParquetException>(() => ReadAsync(edited));
    }

    [Theory]
    [InlineData(3)] // 3 values of at least 4 bytes fit in the page's 17 bytes, but only 2 are there
    [InlineData(5)] // 5 BYTE_ARRAY values need at least 20 bytes
    public async Task Dictionary_page_claiming_more_values_than_it_holds_fails(int claimed) {
        byte[] file = await WriteDictionaryColumnAsync();
        long offset = (await ColumnAsync(file)).DictionaryPageOffset!.Value;

        var ms = new MemoryStream(file) { Position = offset };
        PageHeader ph = PageHeader.Read(new ThriftCompactProtocolReader(ms));
        int headerLength = (int)(ms.Position - offset);
        Assert.Equal(2, ph.DictionaryPageHeader!.NumValues);
        Assert.Equal(17, ph.UncompressedPageSize);
        ph.DictionaryPageHeader.NumValues = claimed;
        var header = new MemoryStream();
        ph.Write(new ThriftCompactProtocolWriter(header));
        Assert.Equal(headerLength, header.Length);
        header.ToArray().CopyTo(file, offset);

        await Assert.ThrowsAsync<ParquetException>(() => ReadAsync(file));
    }

    public static TheoryData<byte[]> ShortBitWidthLists => new() {
        // block of 128 values in 4 miniblocks, 2 values, first value 0; min delta 0 and one of the four bit widths
        new byte[] { 0x80, 0x01, 0x04, 0x02, 0x00, 0x00, 0x00 },
        // 200 values: a whole first block (min delta, four bit widths of 0), then a second block cut off in its bit widths
        new byte[] { 0x80, 0x01, 0x04, 0xC8, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
    };

    [Theory]
    [MemberData(nameof(ShortBitWidthLists))]
    public void Delta_lengths_cut_off_in_their_bit_widths_fail(byte[] page) {
        Assert.Throws<InvalidDataException>(() =>
            DeltaLengthByteArrayEncoder.Decode<ReadOnlyMemory<byte>>(page, new ReadOnlyMemory<byte>[200], 200));
        Assert.Throws<InvalidDataException>(() =>
            DeltaByteArrayEncoder.Decode<ReadOnlyMemory<byte>>(page, new ReadOnlyMemory<byte>[200], 200));
    }

    // A bit-packed run (header 3: one group of 8) whose bytes are missing used to decode nothing and return 0, so an
    // optional column's read could complete from its definition levels with indexes never written.
    [Fact]
    public void Bit_packed_run_missing_its_bytes_fails() =>
        Assert.Throws<InvalidDataException>(() => RleBitpackedHybridEncoder.Decode(new byte[] { 3 }, 1, new int[8], 8));

    [Fact]
    public void Bit_packed_run_with_its_bytes_decodes() {
        int[] dest = new int[8];
        Assert.Equal(8, RleBitpackedHybridEncoder.Decode(new byte[] { 3, 0b1010_1010 }, 1, dest, 8));
        Assert.Equal(new[] { 0, 1, 0, 1, 0, 1, 0, 1 }, dest);
    }

    // Bit width 0 has no value bytes at all: a dictionary of one entry, every index 0.
    [Fact]
    public void Bit_packed_run_of_width_zero_decodes_zeros() {
        int[] dest = Enumerable.Repeat(-1, 8).ToArray();
        Assert.Equal(8, RleBitpackedHybridEncoder.Decode(new byte[] { 3 }, 0, dest, 8));
        Assert.All(dest, v => Assert.Equal(0, v));
    }
}
