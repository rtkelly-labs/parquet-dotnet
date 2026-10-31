using System.Text;
using Parquet;
using Parquet.Encryption;
using Parquet.Schema;

namespace Lake.Parquet.Conformance;

/// <summary>Footer and column keys for an encrypted case, and how parquet-cpp gets them.</summary>
public sealed record CaseEncryption(byte[] FooterKey, bool PlaintextFooter, bool Ctr, IReadOnlyDictionary<string, byte[]> ColumnKeys);

/// <summary>
/// One generated case: a schema of 1 to 6 columns of random shapes and kinds, 1 to 3 row groups of 0 to 200
/// rows, and random writer options (compression, statistics, encoding hints, encryption, field IDs). Everything
/// comes from the seed, so a failure is reproduced with LAKE_CONFORMANCE_SEED=&lt;seed&gt;.
/// </summary>
public sealed class Case {

    static readonly CompressionMethod[] Compressions =
        [CompressionMethod.None, CompressionMethod.Snappy, CompressionMethod.Gzip, CompressionMethod.Brotli, CompressionMethod.Zstd];

    public required int Seed { get; init; }
    public required IReadOnlyList<Shape> Columns { get; init; }
    public required IReadOnlyList<int> RowGroups { get; init; }
    public required CompressionMethod Compression { get; init; }
    public required bool WriteStatistics { get; init; }
    /// <summary>Data pages of at most about this many values (lake(0043)), or one page per chunk.</summary>
    public required int? MaxDataPageValues { get; init; }
    public required IReadOnlyDictionary<string, EncodingHint> Hints { get; init; }
    public required CaseEncryption? Encryption { get; init; }
    public required Random Random { get; init; }
    public required ParquetSchema Schema { get; init; }

    public IEnumerable<Leaf> Leaves => Columns.SelectMany(c => c.Leaves);

    public static Case Generate(int seed) {
        var random = new Random(seed);
        IReadOnlyList<LeafKind> kinds = Kinds.All;
        LeafKind AnyKind() => kinds[random.Next(kinds.Count)];
        bool Coin() => random.Next(2) == 0;

        var columns = new List<Shape>();
        for(int i = random.Next(1, 7); i > 0; i--) {
            string name = $"c{columns.Count}";
            columns.Add(random.Next(10) switch {
                < 5 => new Flat(name, AnyKind(), Coin()),
                < 7 => new ListShape(name, AnyKind(), Coin(), Coin()),
                < 9 => new StructShape(name, Enumerable.Range(0, random.Next(1, 4)).Select(_ => (AnyKind(), Coin())).ToList(), Coin()),
                _ => new MapShape(name, AnyKind(), AnyKind(), Coin(), Coin()),
            });
        }

        // Field IDs on most fields, groups included, all distinct.
        int nextId = random.Next(1, 1000);
        foreach(Field field in columns.SelectMany(c => Walk(c.Field))) {
            if(random.Next(10) < 7) {
                field.FieldId = nextId += random.Next(1, 5);
            }
        }

        // Built before anything is keyed by path: a nested field's path is final once it is in a schema.
        var schema = new ParquetSchema(columns.Select(c => c.Field).ToArray());

        var hints = new Dictionary<string, EncodingHint>();
        foreach(Leaf leaf in columns.SelectMany(c => c.Leaves)) {
            if(random.Next(3) == 0) {
                hints[leaf.Field.Path.ToString()] = (EncodingHint)random.Next(4);
            }
        }

        CaseEncryption? encryption = null;
        if(random.Next(4) == 0) {
            byte[] Key() {
                byte[] key = new byte[new[] { 16, 24, 32 }[random.Next(3)]];
                random.NextBytes(key);
                return key;
            }
            var columnKeys = columns.SelectMany(c => c.Leaves)
                .Where(_ => random.Next(3) == 0)
                .ToDictionary(l => l.Field.Path.ToString(".", null), _ => Key());
            encryption = new CaseEncryption(Key(), Coin(), random.Next(3) == 0, columnKeys);
        }

        return new Case {
            Seed = seed,
            Columns = columns,
            RowGroups = Enumerable.Range(0, random.Next(1, 4)).Select(_ => random.Next(8) == 0 ? 0 : random.Next(1, 201)).ToList(),
            Compression = Compressions[random.Next(Compressions.Length)],
            WriteStatistics = random.Next(6) != 0,
            // from its own stream, so every seed's other choices and values stay what they were before the option
            MaxDataPageValues = new Random(seed ^ 0x5eed_9a9) is var pageRandom && pageRandom.Next(2) == 0 ? null : pageRandom.Next(1, 61),
            Hints = hints,
            Encryption = encryption,
            Random = random,
            Schema = schema,
        };
    }

    public ParquetOptions WriteOptions() {
        var options = new ParquetOptions {
            CompressionMethod = Compression,
            WriteStatistics = WriteStatistics,
            MaxDataPageValues = MaxDataPageValues,
        };
        foreach((string path, EncodingHint hint) in Hints) {
            options.ColumnEncodingHints[path] = hint;
        }
        if(Encryption is { } e) {
            options.Encryption = new ParquetEncryptionOptions {
                FooterKey = e.FooterKey,
                PlaintextFooter = e.PlaintextFooter,
                UseCtrForPages = e.Ctr,
                ColumnKeys = e.ColumnKeys.ToDictionary(k => k.Key, k => new ParquetColumnKey(k.Value, Encoding.UTF8.GetBytes(k.Key))),
            };
        }
        return options;
    }

    public ParquetOptions ReadOptions() => new() {
        Decryption = Encryption is { } e
            ? new ParquetDecryptionOptions { FooterKey = e.FooterKey, ColumnKeys = e.ColumnKeys.ToDictionary(k => k.Key, k => k.Value) }
            : null,
    };

    public static IEnumerable<Field> Walk(Field field) {
        yield return field;
        IEnumerable<Field> children = field switch {
            StructField s => s.Fields,
            ListField l => [l.Item],
            MapField m => [m.Key, m.Value],
            _ => [],
        };
        foreach(Field child in children.SelectMany(Walk)) {
            yield return child;
        }
    }

    public string Describe() {
        var text = new StringBuilder();
        text.AppendLine($"seed {Seed}: {RowGroups.Count} row group(s) of [{string.Join(", ", RowGroups)}] rows, {Compression}, statistics {(WriteStatistics ? "on" : "off")}, {(MaxDataPageValues is int pages ? $"pages of {pages} values" : "one page per chunk")}");
        if(Encryption is { } e) {
            text.AppendLine($"  encrypted: {(e.PlaintextFooter ? "plaintext" : "encrypted")} footer, {(e.Ctr ? "AES_GCM_CTR_V1" : "AES_GCM_V1")}, column keys for [{string.Join(", ", e.ColumnKeys.Keys)}]");
        }
        foreach(Shape column in Columns) {
            text.AppendLine("  " + column.Describe());
        }
        foreach((string path, EncodingHint hint) in Hints) {
            text.AppendLine($"  hint {path}: {hint}");
        }
        text.Append($"  reproduce: LAKE_CONFORMANCE_SEED={Seed} dotnet test eng/lake/Lake.Parquet.Conformance");
        return text.ToString();
    }
}
