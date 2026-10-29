using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Parquet.Data;
using Parquet.Encryption;
using Parquet.Schema;
using Parquet.Serialization;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// Parquet modular encryption. The files in data/encryption were written by parquet-cpp (ParquetSharp 25.0.1.1): two
/// row groups of 100 rows, several data pages per column chunk; id int64 0..199, name string "name{id % 10}" or null
/// when id % 7 == 0. Footer key "0123456789012345" (metadata "kf"); the name column's key "1234567890123450"
/// (metadata "kc1"); aad_prefix_aes256 uses a 32-byte key and the AAD prefix "file1", not stored.
/// </summary>
public class EncryptionTest : TestBase {

    static readonly byte[] FooterKey = Encoding.ASCII.GetBytes("0123456789012345");
    static readonly byte[] NameKey = Encoding.ASCII.GetBytes("1234567890123450");
    static readonly byte[] Key256 = Encoding.ASCII.GetBytes("0123456789012345678901234567890a");

    static readonly long[] Ids = Enumerable.Range(0, 200).Select(i => (long)i).ToArray();
    static readonly string?[] Names = Ids.Select(i => i % 7 == 0 ? null : "name" + (i % 10)).ToArray();

    static ParquetOptions Decrypt(byte[]? footerKey, Dictionary<string, byte[]>? columns = null, byte[]? aadPrefix = null) =>
        new() { Decryption = new ParquetDecryptionOptions { FooterKey = footerKey, ColumnKeys = columns, AadPrefix = aadPrefix } };

    static async Task<(List<long> ids, List<string?> names)> ReadAllAsync(Stream s, ParquetOptions options, bool names = true) {
        await using ParquetReader reader = await ParquetReader.CreateAsync(s, options);
        DataField[] fields = reader.Schema.GetDataFields();
        var ids = new List<long>();
        var result = new List<string?>();
        foreach(ParquetRowGroupReader rg in reader.RowGroups) {
            using RawColumnData<long> id = await rg.ReadRawColumnDataAsync<long>(fields[0]);
            ids.AddRange(id.Values.ToArray());
            if(!names)
                continue;
            using RawColumnData<ReadOnlyMemory<char>> name = await rg.ReadRawColumnDataAsync<ReadOnlyMemory<char>>(fields[1]);
            int v = 0;
            foreach(int d in name.DefinitionLevels)
                result.Add(d == 0 ? null : name.Values[v++].ToString());
        }
        return (ids, result);
    }

    [Theory]
    [InlineData("uniform")]
    [InlineData("ctr")]
    public async Task Reads_files_encrypted_with_the_footer_key(string file) {
        using Stream s = OpenTestFile($"encryption/{file}.parquet");
        (List<long> ids, List<string?> names) = await ReadAllAsync(s, Decrypt(FooterKey));
        Assert.Equal(Ids, ids);
        Assert.Equal(Names, names);
    }

    [Fact]
    public async Task Reads_a_column_key_file() {
        using Stream s = OpenTestFile("encryption/column_key.parquet");
        (List<long> ids, List<string?> names) = await ReadAllAsync(s, Decrypt(FooterKey, new() { ["name"] = NameKey }));
        Assert.Equal(Ids, ids);
        Assert.Equal(Names, names);
    }

    [Fact]
    public async Task Finds_keys_through_their_metadata() {
        using Stream s = OpenTestFile("encryption/column_key.parquet");
        var options = new ParquetOptions {
            Decryption = new ParquetDecryptionOptions {
                KeyRetriever = metadata => Encoding.ASCII.GetString(metadata ?? []) switch {
                    "kf" => FooterKey,
                    "kc1" => NameKey,
                    _ => null
                }
            }
        };
        (List<long> ids, List<string?> names) = await ReadAllAsync(s, options);
        Assert.Equal(Ids, ids);
        Assert.Equal(Names, names);
    }

    [Fact]
    public async Task A_column_without_its_key_cannot_be_read_but_the_others_can() {
        using Stream s = OpenTestFile("encryption/column_key.parquet");
        (List<long> ids, _) = await ReadAllAsync(s, Decrypt(FooterKey), names: false);
        Assert.Equal(Ids, ids);

        s.Position = 0;
        ParquetException e = await Assert.ThrowsAsync<ParquetException>(() => ReadAllAsync(s, Decrypt(FooterKey)));
        Assert.Contains("'name' is encrypted and no key was given for it", e.Message);
    }

    [Fact]
    public async Task A_plaintext_footer_is_readable_without_keys() {
        using Stream s = OpenTestFile("encryption/plaintext_footer.parquet");
        await using(ParquetReader reader = await ParquetReader.CreateAsync(s)) {
            Assert.Equal(new[] { "id", "name" }, reader.Schema.GetDataFields().Select(f => f.Name));
            Assert.Equal(100, reader.RowGroups[0].RowCount);
        }
        s.Position = 0;
        (List<long> ids, _) = await ReadAllAsync(s, new ParquetOptions(), names: false);
        Assert.Equal(Ids, ids);

        s.Position = 0;
        (_, List<string?> names) = await ReadAllAsync(s, Decrypt(FooterKey, new() { ["name"] = NameKey }));
        Assert.Equal(Names, names);

        s.Position = 0;
        ParquetException wrong = await Assert.ThrowsAsync<ParquetException>(() => ReadAllAsync(s, Decrypt(NameKey), names: false));
        Assert.Contains("signature does not match", wrong.Message);
    }

    [Fact]
    public async Task An_aad_prefix_that_is_not_stored_has_to_be_supplied() {
        using Stream s = OpenTestFile("encryption/aad_prefix_aes256.parquet");
        ParquetException missing = await Assert.ThrowsAsync<ParquetException>(() => ReadAllAsync(s, Decrypt(Key256)));
        Assert.Contains("AAD prefix", missing.Message);

        s.Position = 0;
        await Assert.ThrowsAsync<ParquetException>(() => ReadAllAsync(s, Decrypt(Key256, aadPrefix: "file2"u8.ToArray())));

        s.Position = 0;
        (List<long> ids, List<string?> names) = await ReadAllAsync(s, Decrypt(Key256, aadPrefix: "file1"u8.ToArray()));
        Assert.Equal(Ids, ids);
        Assert.Equal(Names, names);
    }

    [Fact]
    public async Task Wrong_or_missing_footer_key() {
        using Stream s = OpenTestFile("encryption/uniform.parquet");
        ParquetException missing = await Assert.ThrowsAsync<ParquetException>(() => ParquetReader.CreateAsync(s));
        Assert.Contains("footer is encrypted", missing.Message);
        s.Position = 0;
        ParquetException wrong = await Assert.ThrowsAsync<ParquetException>(() => ParquetReader.CreateAsync(s, Decrypt(NameKey)));
        Assert.Contains("decryption failed", wrong.Message);
    }

    class Record {
        public long Id { get; set; }
        public string? Name { get; set; }
        public List<int>? Tags { get; set; }
    }

    static Record[] Records(int n) => Enumerable.Range(0, n).Select(i => new Record {
        Id = i, Name = i % 7 == 0 ? null : "name" + (i % 10), Tags = i % 5 == 0 ? null : Enumerable.Range(0, i % 4).ToList()
    }).ToArray();

    public static IEnumerable<object[]> Modes() {
        yield return new object[] { "uniform", new ParquetEncryptionOptions { FooterKey = FooterKey } };
        yield return new object[] { "ctr", new ParquetEncryptionOptions { FooterKey = FooterKey, UseCtrForPages = true } };
        yield return new object[] { "column keys", new ParquetEncryptionOptions {
            FooterKey = FooterKey,
            ColumnKeys = new Dictionary<string, ParquetColumnKey> { ["Name"] = new(NameKey, "kc1"u8.ToArray()), ["Tags.list.element"] = new(Key256) } } };
        yield return new object[] { "plaintext footer", new ParquetEncryptionOptions {
            FooterKey = FooterKey, PlaintextFooter = true,
            ColumnKeys = new Dictionary<string, ParquetColumnKey> { ["Name"] = new(NameKey) } } };
        yield return new object[] { "aad prefix", new ParquetEncryptionOptions { FooterKey = Key256, AadPrefix = "f"u8.ToArray(), StoreAadPrefix = false } };
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Round_trip(string mode, ParquetEncryptionOptions encryption) {
        Record[] data = Records(1000);
        using var ms = new MemoryStream();
        await ParquetSerializer.SerializeAsync(data, ms, new ParquetOptions { RowGroupSize = 300, Encryption = encryption });

        byte[] bytes = ms.ToArray();
        string magic = mode == "plaintext footer" ? "PAR1" : "PARE";
        Assert.Equal(magic, Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(magic, Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4));
        // nothing readable in the encrypted parts
        Assert.DoesNotContain("name3", Encoding.ASCII.GetString(bytes));

        var keys = new Dictionary<string, byte[]> { ["Name"] = NameKey, ["Tags.list.element"] = Key256 };
        ms.Position = 0;
        IList<Record> back = (await ParquetSerializer.DeserializeAsync<Record>(ms, new ParquetOptions {
            Decryption = new ParquetDecryptionOptions {
                FooterKey = encryption.FooterKey, ColumnKeys = keys, AadPrefix = encryption.StoreAadPrefix ? null : encryption.AadPrefix
            }
        })).Data;
        Assert.Equal(data.Length, back.Count);
        for(int i = 0; i < data.Length; i++) {
            Assert.Equal(data[i].Id, back[i].Id);
            Assert.Equal(data[i].Name, back[i].Name);
            Assert.Equal(data[i].Tags, back[i].Tags);
        }
    }

    [Fact]
    public async Task Tampering_is_detected() {
        using var ms = new MemoryStream();
        await ParquetSerializer.SerializeAsync(Records(100), ms, new ParquetOptions {
            Encryption = new ParquetEncryptionOptions { FooterKey = FooterKey }
        });
        byte[] bytes = ms.ToArray();
        bytes[40] ^= 1;     // inside the first column chunk
        await using ParquetReader reader = await ParquetReader.CreateAsync(new MemoryStream(bytes), Decrypt(FooterKey));
        await Assert.ThrowsAsync<ParquetException>(async () => {
            using RawColumnData<long> _ = await reader.RowGroups[0].ReadRawColumnDataAsync<long>(reader.Schema.GetDataFields()[0]);
        });
    }

    [Fact]
    public async Task Keys_must_be_aes_sized() =>
        await Assert.ThrowsAsync<ArgumentException>(() => ParquetWriter.CreateAsync(new ParquetSchema(new DataField<int>("a")),
            new MemoryStream(), new ParquetOptions { Encryption = new ParquetEncryptionOptions { FooterKey = new byte[10] } }));
}
