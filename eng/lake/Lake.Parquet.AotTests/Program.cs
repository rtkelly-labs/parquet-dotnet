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
//   hashes <from> <count>         SHA-256 of each file (CI compares them with the JIT build's: the same bytes); gzip
//                                 cases are hashed written uncompressed (native AOT links its own zlib, whose compressed
//                                 bytes differ while decoding the same); encrypted cases are left to the cross-build reads
//                                 (random nonces)
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
        "corpus" => await Corpus.RunAsync(args[1]),
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
        if(c.Encryption != null) {
            continue;   // random nonces; the cross-build reads cover encrypted files
        }
        string path = Path.Combine(work, $"case-{seed}.parquet");
        if(c.Compression == CompressionMethod.Gzip) {
            // native AOT links its own zlib, whose compressed bytes differ from the shared runtime's: hash everything the
            // library produced, the same case written uncompressed (the cross-build reads cover the gzip file itself)
            await Harness.WriteFileAsync(c, path, CompressionMethod.None);
        } else {
            await Harness.WriteFileAsync(c, path);
        }
        Console.WriteLine($"{seed} {c.Compression} {Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)))}");
    }
    return 0;
}
