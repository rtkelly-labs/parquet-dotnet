// Compares two BenchmarkDotNet runs (their JSON exports) and writes a Markdown table: time and allocation of each
// benchmark in the baseline and the candidate, and the candidate's ratio to the baseline.
//
//   dotnet run eng/lake/CompareBenchmarks.cs -- <baseline results dir> <candidate results dir> [baseline name] [candidate name]
//
// The results directories are BenchmarkDotNet's (<artifacts>/results), from runs with `--exporters json`. A benchmark
// that failed or did not run on one side shows as n/a (the *_Utf8 benchmarks need the fork). Lake.NET
// docs/lake-parquet/testing.md, "Benchmarks", has how the two runs are made.

using System.Globalization;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: CompareBenchmarks.cs <baseline dir> <candidate dir> [baseline name] [candidate name]");
    return 2;
}

Dictionary<string, (double Mean, double? Allocated)> baseline = Read(args[0]);
Dictionary<string, (double Mean, double? Allocated)> candidate = Read(args[1]);
string baseName = args.Length > 2 ? args[2] : "baseline";
string candName = args.Length > 3 ? args[3] : "candidate";
if (baseline.Count == 0 && candidate.Count == 0)
{
    Console.Error.WriteLine("no benchmark results in either directory (run BenchmarkDotNet with --exporters json)");
    return 1;
}

Console.WriteLine($"| Benchmark | {baseName} | {candName} | Time | {baseName} allocated | {candName} allocated | Allocated |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");
foreach (string name in baseline.Keys.Union(candidate.Keys).Order(StringComparer.Ordinal))
{
    bool b = baseline.TryGetValue(name, out var bv);
    bool c = candidate.TryGetValue(name, out var cv);
    Console.WriteLine(string.Join(" | ", [
        "| " + name,
        b ? Time(bv.Mean) : "n/a",
        c ? Time(cv.Mean) : "n/a",
        b && c ? Ratio(cv.Mean, bv.Mean) : "",
        b ? Bytes(bv.Allocated) : "n/a",
        c ? Bytes(cv.Allocated) : "n/a",
        b && c && bv.Allocated is double ba && cv.Allocated is double ca ? Ratio(ca, ba) : "",
    ]) + " |");
}
return 0;

// Type.Method from every *-report-full*.json in a results directory; benchmarks that did not run have no statistics.
static Dictionary<string, (double, double?)> Read(string directory)
{
    var results = new Dictionary<string, (double, double?)>(StringComparer.Ordinal);
    if (!Directory.Exists(directory))
    {
        return results;
    }
    foreach (string file in Directory.EnumerateFiles(directory, "*-report-full*.json"))
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (JsonElement benchmark in doc.RootElement.GetProperty("Benchmarks").EnumerateArray())
        {
            if (!benchmark.TryGetProperty("Statistics", out JsonElement stats) || stats.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string type = benchmark.GetProperty("Type").GetString() ?? "";
            string method = benchmark.GetProperty("Method").GetString() ?? "";
            double? allocated = benchmark.TryGetProperty("Memory", out JsonElement memory) && memory.ValueKind == JsonValueKind.Object
                ? memory.GetProperty("BytesAllocatedPerOperation").GetDouble()
                : null;
            results[$"{type}.{method}"] = (stats.GetProperty("Mean").GetDouble(), allocated);
        }
    }
    return results;
}

static string Time(double ns) => ns >= 1e6
    ? (ns / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " ms"
    : (ns / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + " µs";

static string Bytes(double? b) => b switch
{
    null => "",
    >= 1e6 => (b.Value / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    _ => (b.Value / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + " KB",
};

static string Ratio(double candidate, double baseline) => baseline == 0
    ? ""
    : (candidate / baseline).ToString("0.00", CultureInfo.InvariantCulture) + "×";
