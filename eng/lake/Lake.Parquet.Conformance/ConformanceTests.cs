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

    /// <summary>
    /// Files another build of the library wrote (LAKE_CONFORMANCE_FILES, a directory of case-NNNN.parquet: CI's native
    /// AOT build), checked with every oracle, parquet-cpp decrypting the encrypted ones. Without the variable, nothing.
    /// </summary>
    [Fact]
    public async Task Files_written_by_another_build_agree_with_parquet_cpp() {
        if(Environment.GetEnvironmentVariable("LAKE_CONFORMANCE_FILES") is not { Length: > 0 } files) {
            return;
        }
        var failures = new List<string>();
        int checkedFiles = 0, encrypted = 0;
        foreach(object[] row in Seeds()) {
            int seed = (int)row[0];
            Case c = Case.Generate(seed);
            IReadOnlyList<string> problems = await Harness.CheckWrittenFileAsync(c, Path.Combine(files, $"case-{seed}.parquet"));
            checkedFiles++;
            encrypted += c.Encryption != null ? 1 : 0;
            if(problems.Count > 0) {
                failures.Add($"{c.Describe()}\n{string.Join("\n", problems.Take(10).Select(p => "- " + p))}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n\n", failures.Take(5)));
        Assert.True(checkedFiles > 0 && encrypted > 0, $"checked {checkedFiles} files, {encrypted} encrypted");
    }

    static void Keep(string directory, int seed) {
        if(Environment.GetEnvironmentVariable("LAKE_CONFORMANCE_KEEP") is not { Length: > 0 } keep) {
            return;
        }
        Directory.CreateDirectory(keep);
        foreach(string file in Directory.EnumerateFiles(directory)) {
            System.IO.File.Copy(file, Path.Combine(keep, Path.GetFileName(file)), overwrite: true);
        }
    }
}
