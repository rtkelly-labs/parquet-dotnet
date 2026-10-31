namespace Lake.Parquet.Conformance;

/// <summary>
/// Runs generated cases. By default the seeds 1 to LAKE_CONFORMANCE_CASES (200); LAKE_CONFORMANCE_FROM moves
/// the window (CI's deep run explores fresh seeds), and LAKE_CONFORMANCE_SEED runs one case alone, to
/// reproduce a failure. LAKE_CONFORMANCE_KEEP=&lt;directory&gt; keeps the files of failing cases.
/// </summary>
public class ConformanceTests {

    public static IEnumerable<object[]> Seeds() {
        if(int.TryParse(Environment.GetEnvironmentVariable("LAKE_CONFORMANCE_SEED"), out int seed)) {
            return [[seed]];
        }
        int count = int.TryParse(Environment.GetEnvironmentVariable("LAKE_CONFORMANCE_CASES"), out int n) ? n : 200;
        int from = int.TryParse(Environment.GetEnvironmentVariable("LAKE_CONFORMANCE_FROM"), out int f) ? f : 1;
        return Enumerable.Range(from, count).Select(s => new object[] { s });
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Case_round_trips_and_agrees_with_parquet_cpp(int seed) {
        Case c = Case.Generate(seed);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("lake-conformance-");
        try {
            IReadOnlyList<string> problems = await Harness.RunAsync(c, directory.FullName);
            if(problems.Count > 0) {
                Keep(directory.FullName, seed);
                Assert.Fail($"{c.Describe()}\n{string.Join("\n", problems.Take(30).Select(p => "- " + p))}" +
                    (problems.Count > 30 ? $"\n- ... and {problems.Count - 30} more" : ""));
            }
        } finally {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Case_written_by_parquet_cpp_reads_back_exactly(int seed) {
        Case c = Case.Generate(seed);
        DirectoryInfo directory = Directory.CreateTempSubdirectory("lake-conformance-reverse-");
        try {
            IReadOnlyList<string> problems = await Reverse.RunAsync(c, directory.FullName);
            if(problems.Count > 0) {
                Keep(directory.FullName, seed);
                Assert.Fail($"{c.Describe()}\n{string.Join("\n", problems.Take(30).Select(p => "- " + p))}" +
                    (problems.Count > 30 ? $"\n- ... and {problems.Count - 30} more" : ""));
            }
        } finally {
            directory.Delete(recursive: true);
        }
    }

    static void Keep(string directory, int seed) {
        if(Environment.GetEnvironmentVariable("LAKE_CONFORMANCE_KEEP") is not { Length: > 0 } keep) {
            return;
        }
        Directory.CreateDirectory(keep);
        foreach(string file in Directory.EnumerateFiles(directory)) {
            File.Copy(file, Path.Combine(keep, Path.GetFileName(file)), overwrite: true);
        }
    }
}
