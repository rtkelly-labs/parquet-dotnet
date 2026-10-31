using System.Security.Cryptography;
using System.Text;
using Lake.Parquet.Conformance;
using Parquet;
using Parquet.Schema;

// Lake.Parquet under native AOT. Three modes, each printing one line per item and exiting non-zero on any failure:
//
//   cases  <from> <count>         generated conformance cases: write, read back, compare values, levels, field IDs, statistics
//   write  <from> <count> <dir>   write those cases' files, for the other build (JIT or native) to read
//   read   <from> <count> <dir>   check files the other build wrote against what each case's seed gives
//   hashes <from> <count>         SHA-256 of each file (CI compares them with the JIT build's: the same bytes), except
//                                 encrypted cases (random nonces) and gzip (native AOT links its own zlib, whose output
//                                 differs from the shared runtime's while decoding the same)
//   corpus <directory>            every .parquet file decoded, hashed by its schema, values and levels (compared likewise)
//   api                           the calls generated readers and writers make, under every codec, hint and page layout
//
// The same program runs on the JIT (`dotnet run`) to produce what the native build must match.

if(args.Length == 1 && args[0] == "api") {
    return await Api.RunAsync();
}
if(args.Length < 2) {
    Console.Error.WriteLine("usage: cases <from> <count> | hashes <from> <count> | corpus <directory>");
    return 2;
}

string work = Path.Combine(Path.GetTempPath(), "lake-aot-" + Environment.ProcessId);
Directory.CreateDirectory(work);
try {
    return args[0] switch {
        "cases" => await CasesAsync(int.Parse(args[1]), int.Parse(args[2])),
        "write" => await WriteAsync(int.Parse(args[1]), int.Parse(args[2]), args[3]),
        "read" => await ReadFilesAsync(int.Parse(args[1]), int.Parse(args[2]), args[3]),
        "hashes" => await HashesAsync(int.Parse(args[1]), int.Parse(args[2])),
        "corpus" => await CorpusAsync(args[1]),
        _ => 2,
    };
} finally {
    Directory.Delete(work, recursive: true);
}

async Task<int> CasesAsync(int from, int count) {
    int failed = 0;
    for(int seed = from; seed < from + count; seed++) {
        Case c = Case.Generate(seed);
        IReadOnlyList<string> problems = await Harness.RunLibraryAsync(c, work);
        if(problems.Count > 0) {
            failed++;
            Console.WriteLine(c.Describe());
            foreach(string problem in problems) {
                Console.WriteLine("- " + problem);
            }
        }
    }
    Console.WriteLine($"cases {from}..{from + count - 1}: {count - failed} passed, {failed} failed");
    return failed == 0 ? 0 : 1;
}

async Task<int> WriteAsync(int from, int count, string directory) {
    Directory.CreateDirectory(directory);
    for(int seed = from; seed < from + count; seed++) {
        await Harness.WriteFileAsync(Case.Generate(seed), Path.Combine(directory, $"case-{seed}.parquet"));
    }
    Console.WriteLine($"wrote cases {from}..{from + count - 1} to {directory}");
    return 0;
}

async Task<int> ReadFilesAsync(int from, int count, string directory) {
    int failed = 0;
    for(int seed = from; seed < from + count; seed++) {
        Case c = Case.Generate(seed);
        IReadOnlyList<string> problems = await Harness.CheckFileAsync(c, Path.Combine(directory, $"case-{seed}.parquet"));
        if(problems.Count > 0) {
            failed++;
            Console.WriteLine(c.Describe());
            foreach(string problem in problems) {
                Console.WriteLine("- " + problem);
            }
        }
    }
    Console.WriteLine($"read cases {from}..{from + count - 1} from {directory}: {count - failed} passed, {failed} failed");
    return failed == 0 ? 0 : 1;
}

async Task<int> HashesAsync(int from, int count) {
    for(int seed = from; seed < from + count; seed++) {
        Case c = Case.Generate(seed);
        if(c.Encryption != null || c.Compression == CompressionMethod.Gzip) {
            continue;   // random nonces; and native AOT's own zlib, whose bytes differ from the shared runtime's
        }
        await Harness.RunLibraryAsync(c, work);
        string path = Path.Combine(work, $"case-{seed}.parquet");
        Console.WriteLine($"{seed} {c.Compression} {Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)))}");
    }
    return 0;
}

async Task<int> CorpusAsync(string directory) {
    int failed = 0;
    foreach(string path in Directory.EnumerateFiles(directory, "*.parquet", SearchOption.AllDirectories).Order(StringComparer.Ordinal)) {
        string name = Path.GetRelativePath(directory, path).Replace('\\', '/');
        try {
            Console.WriteLine($"{name} {await DecodeAsync(path)}");
        } catch(Exception e) {
            // a file the library cannot read must fail the same way on both builds
            Console.WriteLine($"{name} error {e.GetType().Name}");
            if(Environment.GetEnvironmentVariable("LAKE_AOT_VERBOSE") == "1") Console.Error.WriteLine($"{name}: {e.Message}");
            if(e is TypeInitializationException or NotSupportedException or MissingMethodException or TypeLoadException) {
                failed += e.Message.Contains("dynamic", StringComparison.OrdinalIgnoreCase) || e.Message.Contains("AOT", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }
        }
    }
    return failed == 0 ? 0 : 1;
}

// The file's schema, then every leaf column of every row group, physical values and levels, into one hash.
async Task<string> DecodeAsync(string path) {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    await using FileStream stream = File.OpenRead(path);
    await using ParquetReader reader = await ParquetReader.CreateAsync(stream);
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
