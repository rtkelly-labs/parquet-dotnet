using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Lake.Parquet.Conformance;

/// <summary>What was written for one leaf in one row group: its defined values and its levels.</summary>
public sealed record LeafChunk(Leaf Leaf, object[] Values, int[] Definitions, int[] Repetitions);

/// <summary>
/// Writes a case with the library and checks the file against three oracles:
/// <list type="bullet">
/// <item><b>Round trip</b>: the library reads back exactly the values and levels it was given, the schema
/// (levels, field IDs) is the one written, and every row group has its row count.</item>
/// <item><b>parquet-cpp</b> (ParquetSharp): reads the same physical values and levels, the same field IDs, and,
/// for encrypted files, decrypts them with the same keys.</item>
/// <item><b>Statistics</b>: null counts and min/max are exactly what the values give under the column's sort
/// order, the library and parquet-cpp agree on them, and parquet-cpp trusts them (column orders); with
/// statistics off, there are none.</item>
/// </list>
/// Every disagreement is collected, then reported with the case's description and seed.
/// </summary>
public static partial class Harness {

    /// <summary>Generates every row group's entries for a case.</summary>
    public static IReadOnlyList<IReadOnlyList<LeafChunk>> Generate(Case c) {
        var groups = new List<IReadOnlyList<LeafChunk>>();
        foreach(int rows in c.RowGroups) {
            foreach(Leaf leaf in c.Leaves) {
                leaf.Clear();
            }
            for(int row = 0; row < rows; row++) {
                foreach(Shape column in c.Columns) {
                    column.AddRow(c.Random);
                }
            }
            groups.Add(c.Leaves.Select(l => new LeafChunk(l, l.Values.ToArray(), l.Definitions.ToArray(), l.Repetitions.ToArray())).ToList());
        }
        return groups;
    }

    static async Task WriteAsync(Case c, IReadOnlyList<IReadOnlyList<LeafChunk>> groups, string path, CompressionMethod? compression = null) {
        ParquetOptions options = c.WriteOptions();
        if(compression is CompressionMethod method) {
            options.CompressionMethod = method;
        }
        await using FileStream stream = System.IO.File.Create(path);
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(c.Schema, stream, options);
        foreach(IReadOnlyList<LeafChunk> group in groups) {
            using ParquetRowGroupWriter rowGroup = writer.CreateRowGroup();
            foreach(LeafChunk chunk in group) {
                Leaf leaf = chunk.Leaf;
                await leaf.Kind.WriteAsync(rowGroup, leaf.Field, chunk.Values,
                    leaf.MaxDefinition > 0 ? chunk.Definitions : null,
                    leaf.MaxRepetition > 0 ? chunk.Repetitions : null);
            }
            rowGroup.CompleteValidate();
        }
    }

    // ---- round trip and the library's statistics ----

    static async Task CheckRoundTripAsync(Case c, IReadOnlyList<IReadOnlyList<LeafChunk>> groups, string path,
        Dictionary<string, int> typeLengths, List<string> problems) {
        await using FileStream stream = System.IO.File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, c.ReadOptions());

        if(reader.RowGroupCount != groups.Count) {
            problems.Add($"round trip: {reader.RowGroupCount} row groups, expected {groups.Count}");
            return;
        }
        Field[] written = c.Columns.SelectMany(col => Case.Walk(col.Field)).ToArray();
        Field[] read = reader.Schema.Fields.SelectMany(Case.Walk).ToArray();
        if(written.Length != read.Length) {
            problems.Add($"round trip: the schema reads back with {read.Length} fields, expected {written.Length}");
            return;
        }
        for(int i = 0; i < written.Length; i++) {
            if(written[i].FieldId != read[i].FieldId) {
                problems.Add($"round trip: {written[i].Path} has field ID {read[i].FieldId}, expected {written[i].FieldId}");
            }
        }

        DataField[] fields = reader.Schema.GetDataFields();
        Leaf[] leaves = c.Leaves.ToArray();
        for(int i = 0; i < leaves.Length; i++) {
            if(fields[i].MaxDefinitionLevel != leaves[i].MaxDefinition || fields[i].MaxRepetitionLevel != leaves[i].MaxRepetition) {
                problems.Add($"schema: {fields[i].Path} has levels (def {fields[i].MaxDefinitionLevel}, rep {fields[i].MaxRepetitionLevel}), expected (def {leaves[i].MaxDefinition}, rep {leaves[i].MaxRepetition})");
            }
        }

        for(int g = 0; g < groups.Count; g++) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
            if(rowGroup.RowCount != c.RowGroups[g]) {
                problems.Add($"round trip: row group {g} has {rowGroup.RowCount} rows, expected {c.RowGroups[g]}");
            }
            for(int i = 0; i < leaves.Length; i++) {
                LeafChunk chunk = groups[g][i];
                string where = $"row group {g}, {fields[i].Path} ({leaves[i].Kind})";
                try {
                    (List<object> values, int[]? definitions, int[]? repetitions) =
                        await leaves[i].Kind.ReadAsync(rowGroup, fields[i], chunk.Definitions.Length);
                    CompareValues($"round trip: {where}", leaves[i].Kind, chunk.Values, values, problems);
                    if(definitions != null && !definitions.SequenceEqual(chunk.Definitions)) {
                        problems.Add($"round trip: {where}: definition levels differ ({Diff(chunk.Definitions, definitions)})");
                    }
                    if(repetitions != null && !repetitions.SequenceEqual(chunk.Repetitions)) {
                        problems.Add($"round trip: {where}: repetition levels differ ({Diff(chunk.Repetitions, repetitions)})");
                    }
                } catch(Exception e) {
                    problems.Add($"round trip: {where}: reading failed: {e.GetType().Name}: {e.Message}");
                    continue;
                }
                CheckLibraryStatistics(c, chunk, rowGroup.GetStatistics(fields[i]), typeLengths[Key(leaves[i])], where, problems);
            }
        }
    }

    static void CheckLibraryStatistics(Case c, LeafChunk chunk, DataColumnStatistics? stats, int typeLength, string where, List<string> problems) {
        Leaf leaf = chunk.Leaf;
        if(!c.WriteStatistics) {
            if(stats is { HasMinMax: true }) {
                problems.Add($"statistics: {where}: min/max written although statistics are off");
            }
            return;
        }
        if(leaf.MaxRepetition == 0) {
            long nulls = chunk.Definitions.Length == 0 ? 0 : chunk.Definitions.Count(d => d < leaf.MaxDefinition);
            if(leaf.MaxDefinition > 0 && stats?.NullCount is long n && n != nulls) {
                problems.Add($"statistics: {where}: null count {n}, expected {nulls}");
            }
        }
        (object Min, object Max)? expected = Bounds(leaf.Kind, chunk.Values, typeLength);
        if(expected == null || !leaf.Kind.WritesBounds) {
            return;
        }
        if(stats is not { HasMinMax: true }) {
            problems.Add($"statistics: {where}: no min/max, expected {Show(expected.Value.Min)} .. {Show(expected.Value.Max)}");
            return;
        }
        if(!SamePhysical(stats.PhysicalMin, expected.Value.Min) || !SamePhysical(stats.PhysicalMax, expected.Value.Max)) {
            problems.Add($"statistics: {where}: min/max {Show(stats.PhysicalMin)} .. {Show(stats.PhysicalMax)}, expected {Show(expected.Value.Min)} .. {Show(expected.Value.Max)}");
        }
    }

    static string Key(Leaf leaf) => leaf.Field.Path.ToString(".", null);

    /// <summary>
    /// Writes a case and checks it with the library alone: the round trip and the library's statistics, the
    /// fixed-length widths read from the file's own footer. The native AOT regression suite (Lake.Parquet.AotTests) runs
    /// this, since parquet-cpp is not there.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunLibraryAsync(Case c, string directory) {
        var problems = new List<string>();
        IReadOnlyList<IReadOnlyList<LeafChunk>> groups = Generate(c);
        string path = Path.Combine(directory, $"case-{c.Seed}.parquet");
        try {
            await WriteAsync(c, groups, path);
        } catch(Exception e) {
            return [$"writing failed: {e}"];
        }
        Dictionary<string, int> typeLengths;
        try {
            typeLengths = await LibraryTypeLengthsAsync(c, path);
        } catch(Exception e) {
            return [$"the library could not open the file: {e}"];
        }
        await CheckRoundTripAsync(c, groups, path, typeLengths, problems);
        return problems;
    }

    /// <summary>Writes a case's file without checking it (for another build of the library to read: see CheckFileAsync).</summary>
    public static async Task WriteFileAsync(Case c, string path, CompressionMethod? compression = null) =>
        await WriteAsync(c, Generate(c), path, compression);

    /// <summary>
    /// Checks a file written for a case, perhaps by another build of the library (JIT or native AOT): the values,
    /// levels, field IDs, row counts and statistics the case's seed gives.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckFileAsync(Case c, string path) {
        var problems = new List<string>();
        IReadOnlyList<IReadOnlyList<LeafChunk>> groups = Generate(c);
        Dictionary<string, int> typeLengths;
        try {
            typeLengths = await LibraryTypeLengthsAsync(c, path);
        } catch(Exception e) {
            return [$"the library could not open the file: {e}"];
        }
        await CheckRoundTripAsync(c, groups, path, typeLengths, problems);
        return problems;
    }

    /// <summary>Each leaf's FIXED_LEN_BYTE_ARRAY width (0 for other types), from the footer's schema, in leaf order.</summary>
    static async Task<Dictionary<string, int>> LibraryTypeLengthsAsync(Case c, string path) {
        await using FileStream stream = System.IO.File.OpenRead(path);
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, c.ReadOptions());
        int[] widths = reader.Metadata!.Schema.Skip(1).Where(e => e.NumChildren is null or 0).Select(e => e.TypeLength ?? 0).ToArray();
        Leaf[] leaves = c.Leaves.ToArray();
        return Enumerable.Range(0, leaves.Length).ToDictionary(i => Key(leaves[i]), i => widths[i]);
    }

    // ---- orders and comparisons ----

    /// <summary>The physical min and max of the values under the column's sort order (NaN left out), or null.</summary>
    public static (object Min, object Max)? Bounds(LeafKind kind, IReadOnlyList<object> values, int typeLength) {
        if(kind.Order == Order.None) {
            return null;
        }
        List<object> physical = values.Select(v => kind.Physical(v, typeLength))
            .Where(p => p is not float f || !float.IsNaN(f))
            .Where(p => p is not double d || !double.IsNaN(d))
            .ToList();
        if(physical.Count == 0) {
            return null;
        }
        Comparison<object> compare = Comparer(kind.Order);
        object min = physical[0], max = physical[0];
        foreach(object p in physical) {
            if(compare(p, min) < 0) {
                min = p;
            }
            if(compare(p, max) > 0) {
                max = p;
            }
        }
        return (min, max);
    }

    static Comparison<object> Comparer(Order order) => (a, b) => (a, b) switch {
        (int x, int y) => order == Order.Unsigned ? ((uint)x).CompareTo((uint)y) : x.CompareTo(y),
        (long x, long y) => order == Order.Unsigned ? ((ulong)x).CompareTo((ulong)y) : x.CompareTo(y),
        (float x, float y) => x.CompareTo(y),
        (double x, double y) => x.CompareTo(y),
        (bool x, bool y) => x.CompareTo(y),
        (byte[] x, byte[] y) => order == Order.Unsigned
            ? x.AsSpan().SequenceCompareTo(y)
            : new System.Numerics.BigInteger(x, isUnsigned: false, isBigEndian: true).CompareTo(new System.Numerics.BigInteger(y, isUnsigned: false, isBigEndian: true)),
        _ => throw new InvalidOperationException($"cannot compare {a.GetType().Name} with {b.GetType().Name}"),
    };

    /// <summary>Physical values are equal; zeros of either sign are equal, as min/max may carry either.</summary>
    public static bool SamePhysical(object? a, object? b) => (a, b) switch {
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        (float x, float y) => x == y || (float.IsNaN(x) && float.IsNaN(y)),
        (double x, double y) => x == y || (double.IsNaN(x) && double.IsNaN(y)),
        _ => Equals(a, b),
    };

    internal static void CompareValues(string where, LeafKind kind, object[] expected, List<object> actual, List<string> problems) {
        if(expected.Length != actual.Count) {
            problems.Add($"{where}: {actual.Count} values, expected {expected.Length}");
            return;
        }
        for(int i = 0; i < expected.Length; i++) {
            if(!kind.Same(expected[i], actual[i])) {
                problems.Add($"{where}: value {i} is {Show(actual[i])}, expected {Show(expected[i])}");
                return;
            }
        }
    }

    internal static string Diff(int[] expected, int[] actual) {
        int i = 0;
        while(i < Math.Min(expected.Length, actual.Length) && expected[i] == actual[i]) {
            i++;
        }
        return $"first difference at {i} of {expected.Length}/{actual.Length}";
    }

    static string DiffValues(object[] expected, object[] actual) {
        int i = 0;
        while(i < Math.Min(expected.Length, actual.Length) && SamePhysical(expected[i], actual[i])) {
            i++;
        }
        return i < Math.Min(expected.Length, actual.Length)
            ? $"value {i}: {Show(actual[i])}, expected {Show(expected[i])}"
            : $"{actual.Length} values, expected {expected.Length}";
    }

    public static string Show(object? value) => value switch {
        null => "null",
        byte[] b => "0x" + Convert.ToHexString(b),
        ReadOnlyMemory<byte> b => "0x" + Convert.ToHexString(b.Span),
        ReadOnlyMemory<char> s => $"\"{s}\"",
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        DateTime t => t.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "?",
    };
}
