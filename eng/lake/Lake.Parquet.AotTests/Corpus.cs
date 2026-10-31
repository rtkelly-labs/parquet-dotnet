using System.Security.Cryptography;
using System.Text;
using Parquet;
using Parquet.Schema;

/// <summary>Upstream's test corpus, decoded: what both builds must agree on, and the files no reader can decode.</summary>
static class Corpus {

    // Files no reader can decode, with why: each must be refused, and refused the same way on both builds.
    static readonly Dictionary<string, string> Refused = new() {
        ["special/all_nulls_no_booleans.parquet"] = "FIXED_LEN_BYTE_ARRAY with no length; parquet-cpp refuses it too",
    };

    // The keys upstream's tests document for its encrypted files (src/Parquet.Test/EncryptionTest.cs).
    static ParquetOptions OptionsFor(string name) {
        byte[] footerKey = Encoding.ASCII.GetBytes("0123456789012345");
        byte[] nameKey = Encoding.ASCII.GetBytes("1234567890123450");
        byte[] key256 = Encoding.ASCII.GetBytes("0123456789012345678901234567890a");
        return name switch {
            "encryption/aad_prefix_aes256.parquet" => new() { Decryption = new() { FooterKey = key256, AadPrefix = "file1"u8.ToArray() } },
            _ when name.StartsWith("encryption/", StringComparison.Ordinal) =>
                new() { Decryption = new() { FooterKey = footerKey, ColumnKeys = new Dictionary<string, byte[]> { ["name"] = nameKey } } },
            _ => new(),
        };
    }

    public static async Task<int> RunAsync(string directory) {
        int failed = 0;
        foreach(string path in Directory.EnumerateFiles(directory, "*.parquet", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
            string name = Path.GetRelativePath(directory, path).Replace('\\', '/');
            try {
                string decoded = await DecodeAsync(path, OptionsFor(name));
                Console.WriteLine($"{name} {decoded}");
                if(Refused.ContainsKey(name)) {
                    failed++;
                    Console.Error.WriteLine($"{name}: decoded, but no reader should ({Refused[name]})");
                }
            } catch(Exception e) {
                Console.WriteLine($"{name} refused {e.GetType().Name}");
                if(!Refused.ContainsKey(name)) {
                    failed++;
                    Console.Error.WriteLine($"{name}: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        return failed == 0 ? 0 : 1;
    }

    // The file's schema, then every leaf column of every row group, physical values and levels, into one hash.
    static async Task<string> DecodeAsync(string path, ParquetOptions options) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream stream = File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, options);
        hash.AppendData(Encoding.UTF8.GetBytes(reader.Schema.ToString()));
        DataField[] fields = reader.Schema.GetDataFields();
        for(int g = 0; g < reader.RowGroupCount; g++) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
            foreach(DataField field in fields) {
                int entries = checked((int)rowGroup.GetMetadata(field)!.MetaData!.NumValues);
                int[]? definitions = field.MaxDefinitionLevel > 0 ? new int[entries] : null;
                int[]? repetitions = field.MaxRepetitionLevel > 0 ? new int[entries] : null;
                string values = field.PhysicalClrType switch {
                    Type t when t == typeof(bool) => await ReadAsync<bool>(rowGroup, field, entries, definitions, repetitions),
                    Type t when t == typeof(int) => await ReadAsync<int>(rowGroup, field, entries, definitions, repetitions),
                    Type t when t == typeof(long) => await ReadAsync<long>(rowGroup, field, entries, definitions, repetitions),
                    Type t when t == typeof(float) => await ReadAsync<float>(rowGroup, field, entries, definitions, repetitions),
                    Type t when t == typeof(double) => await ReadAsync<double>(rowGroup, field, entries, definitions, repetitions),
                    Type t when t == typeof(ReadOnlyMemory<byte>) => await ReadAsync<ReadOnlyMemory<byte>>(rowGroup, field, entries, definitions, repetitions),
                    _ => await ReadAsync<DateTime>(rowGroup, field, entries, definitions, repetitions),   // INT96
                };
                hash.AppendData(Encoding.UTF8.GetBytes(values));
                if(definitions != null) {
                    hash.AppendData(Encoding.UTF8.GetBytes(string.Join(',', definitions)));
                }
                if(repetitions != null) {
                    hash.AppendData(Encoding.UTF8.GetBytes(string.Join(',', repetitions)));
                }
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static async Task<string> ReadAsync<T>(ParquetRowGroupReader rowGroup, DataField field, int entries, int[]? definitions, int[]? repetitions) where T : struct {
        var values = new T[entries];
        // a null int[] would convert to an empty, non-null Memory<int>?: pass null as null
        await rowGroup.ReadRawAsync<T>(field, values,
            definitions is null ? null : (Memory<int>?)definitions,
            repetitions is null ? null : (Memory<int>?)repetitions);
        return string.Join('|', values.Select(v => v switch {
            ReadOnlyMemory<byte> bytes => Convert.ToHexString(bytes.Span),
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        }));
    }
}
