using System.Runtime.InteropServices;
using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using PS = ParquetSharp;

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
public static class Harness {

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

    public static async Task<IReadOnlyList<string>> RunAsync(Case c, string directory) {
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
            typeLengths = TypeLengths(c, path);
        } catch(Exception e) {
            return [$"parquet-cpp could not open the file: {e.Message}"];
        }

        await CheckRoundTripAsync(c, groups, path, typeLengths, problems);
        CheckWithParquetCpp(c, groups, path, typeLengths, problems);
        return problems;
    }

    static async Task WriteAsync(Case c, IReadOnlyList<IReadOnlyList<LeafChunk>> groups, string path) {
        await using FileStream stream = File.Create(path);
        await using ParquetWriter writer = await ParquetWriter.CreateAsync(c.Schema, stream, c.WriteOptions());
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
        await using FileStream stream = File.OpenRead(path);
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

    // ---- parquet-cpp ----

    static PS.ParquetFileReader OpenWithParquetCpp(Case c, string path) {
        if(c.Encryption is not { } e) {
            return new PS.ParquetFileReader(path);
        }
        using var builder = new PS.FileDecryptionPropertiesBuilder();
        using PS.FileDecryptionProperties decryption = builder
            .FooterKey(e.FooterKey)
            .KeyRetriever(new KeyRetriever(e.ColumnKeys))
            .Build();
        using PS.ReaderProperties properties = PS.ReaderProperties.GetDefaultReaderProperties();
        properties.FileDecryptionProperties = decryption;
        return new PS.ParquetFileReader(path, properties);
    }

    sealed class KeyRetriever(IReadOnlyDictionary<string, byte[]> keys) : PS.DecryptionKeyRetriever {
        public override byte[] GetKey(string keyMetadata) => keys[keyMetadata];
    }

    static string Key(Leaf leaf) => leaf.Field.Path.ToString(".", null);

    static Dictionary<string, int> TypeLengths(Case c, string path) {
        using PS.ParquetFileReader file = OpenWithParquetCpp(c, path);
        PS.SchemaDescriptor schema = file.FileMetaData.Schema;
        Leaf[] leaves = c.Leaves.ToArray();
        return Enumerable.Range(0, schema.NumColumns).ToDictionary(i => Key(leaves[i]), i => schema.Column(i).TypeLength);
    }

    static void CheckWithParquetCpp(Case c, IReadOnlyList<IReadOnlyList<LeafChunk>> groups, string path,
        Dictionary<string, int> typeLengths, List<string> problems) {
        using PS.ParquetFileReader file = OpenWithParquetCpp(c, path);
        PS.SchemaDescriptor schema = file.FileMetaData.Schema;
        Leaf[] leaves = c.Leaves.ToArray();

        if(schema.NumColumns != leaves.Length) {
            problems.Add($"parquet-cpp: {schema.NumColumns} columns, expected {leaves.Length}");
            return;
        }
        if(file.FileMetaData.NumRowGroups != groups.Count) {
            problems.Add($"parquet-cpp: {file.FileMetaData.NumRowGroups} row groups, expected {groups.Count}");
            return;
        }
        for(int k = 0; k < c.Columns.Count; k++) {
            int id = schema.GroupNode.Field(k).FieldId;
            if(id != c.Columns[k].Field.FieldId) {
                problems.Add($"parquet-cpp: {c.Columns[k].Field.Name} has field ID {id}, expected {c.Columns[k].Field.FieldId}");
            }
        }
        for(int i = 0; i < leaves.Length; i++) {
            PS.ColumnDescriptor column = schema.Column(i);
            if(column.MaxDefinitionLevel != leaves[i].MaxDefinition || column.MaxRepetitionLevel != leaves[i].MaxRepetition) {
                problems.Add($"parquet-cpp: {Key(leaves[i])} has levels (def {column.MaxDefinitionLevel}, rep {column.MaxRepetitionLevel}), expected (def {leaves[i].MaxDefinition}, rep {leaves[i].MaxRepetition})");
            }
            if(column.SchemaNode.FieldId != leaves[i].Field.FieldId) {
                problems.Add($"parquet-cpp: {Key(leaves[i])} has field ID {column.SchemaNode.FieldId}, expected {leaves[i].Field.FieldId}");
            }
        }

        for(int g = 0; g < groups.Count; g++) {
            using PS.RowGroupReader rowGroup = file.RowGroup(g);
            if(rowGroup.MetaData.NumRows != c.RowGroups[g]) {
                problems.Add($"parquet-cpp: row group {g} has {rowGroup.MetaData.NumRows} rows, expected {c.RowGroups[g]}");
            }
            for(int i = 0; i < leaves.Length; i++) {
                LeafChunk chunk = groups[g][i];
                Leaf leaf = leaves[i];
                string where = $"row group {g}, {Key(leaf)} ({leaf.Kind})";
                int typeLength = typeLengths[Key(leaf)];
                object[] expected = chunk.Values.Select(v => leaf.Kind.Physical(v, typeLength)).ToArray();
                try {
                    using PS.ColumnReader reader = rowGroup.Column(i);
                    (object[] values, short[] definitions, short[] repetitions) = ReadPhysical(reader, chunk.Definitions.Length, typeLength);
                    if(values.Length != expected.Length || !values.Zip(expected).All(p => SamePhysical(p.First, p.Second))) {
                        problems.Add($"parquet-cpp: {where}: values differ ({DiffValues(expected, values)})");
                    }
                    if(leaf.MaxDefinition > 0 && !definitions.Select(d => (int)d).SequenceEqual(chunk.Definitions)) {
                        problems.Add($"parquet-cpp: {where}: definition levels differ");
                    }
                    if(leaf.MaxRepetition > 0 && !repetitions.Select(r => (int)r).SequenceEqual(chunk.Repetitions)) {
                        problems.Add($"parquet-cpp: {where}: repetition levels differ");
                    }
                } catch(Exception e) {
                    problems.Add($"parquet-cpp: {where}: reading failed: {e.GetType().Name}: {e.Message}");
                    continue;
                }
                CheckParquetCppStatistics(c, chunk, rowGroup.MetaData.GetColumnChunkMetaData(i), typeLength, where, problems);
            }
        }
    }

    static void CheckParquetCppStatistics(Case c, LeafChunk chunk, PS.ColumnChunkMetaData metadata, int typeLength, string where, List<string> problems) {
        using PS.Statistics? stats = metadata.Statistics;
        Leaf leaf = chunk.Leaf;
        if(!c.WriteStatistics) {
            if(stats is { HasMinMax: true }) {
                problems.Add($"parquet-cpp statistics: {where}: min/max present although statistics are off");
            }
            return;
        }
        (object Min, object Max)? expected = Bounds(leaf.Kind, chunk.Values, typeLength);
        if(expected == null || !leaf.Kind.WritesBounds) {
            return;
        }
        if(stats is not { HasMinMax: true }) {
            // parquet-cpp drops min/max it does not trust: without column orders, every unsigned-order column.
            problems.Add($"parquet-cpp statistics: {where}: parquet-cpp does not use the min/max");
            return;
        }
        (object? min, object? max) = stats switch {
            PS.Statistics<int> s => ((object?)s.Min, (object?)s.Max),
            PS.Statistics<long> s => (s.Min, s.Max),
            PS.Statistics<float> s => (s.Min, s.Max),
            PS.Statistics<double> s => (s.Min, s.Max),
            PS.Statistics<bool> s => (s.Min, s.Max),
            _ => (null, null),
        };
        if(min != null && (!SamePhysical(min, expected.Value.Min) || !SamePhysical(max, expected.Value.Max))) {
            problems.Add($"parquet-cpp statistics: {where}: min/max {Show(min)} .. {Show(max)}, expected {Show(expected.Value.Min)} .. {Show(expected.Value.Max)}");
        }
    }

    internal static (object[] Values, short[] Definitions, short[] Repetitions) ReadPhysical(PS.ColumnReader reader, int entries, int typeLength) =>
        reader switch {
            PS.ColumnReader<bool> r => Read(r, entries, v => v),
            PS.ColumnReader<int> r => Read(r, entries, v => v),
            PS.ColumnReader<long> r => Read(r, entries, v => v),
            PS.ColumnReader<float> r => Read(r, entries, v => v),
            PS.ColumnReader<double> r => Read(r, entries, v => v),
            PS.ColumnReader<PS.ByteArray> r => Read(r, entries, v => Copy(v.Pointer, v.Length)),
            PS.ColumnReader<PS.FixedLenByteArray> r => Read(r, entries, v => Copy(v.Pointer, typeLength)),
            _ => throw new NotSupportedException($"physical type of {reader.GetType().Name}"),
        };

    /// <summary>Reads every entry of a column chunk; byte arrays are copied before the next batch frees them.</summary>
    static (object[], short[], short[]) Read<T>(PS.ColumnReader<T> reader, int entries, Func<T, object> convert) where T : unmanaged {
        var values = new List<object>();
        var definitions = new List<short>();
        var repetitions = new List<short>();
        var defBuffer = new short[Math.Max(entries, 1)];
        var repBuffer = new short[Math.Max(entries, 1)];
        var valueBuffer = new T[Math.Max(entries, 1)];
        while(reader.HasNext) {
            long levels = reader.ReadBatch(valueBuffer.Length, defBuffer, repBuffer, valueBuffer, out long read);
            definitions.AddRange(defBuffer.Take((int)levels));
            repetitions.AddRange(repBuffer.Take((int)levels));
            for(int i = 0; i < read; i++) {
                values.Add(convert(valueBuffer[i]));
            }
        }
        return (values.ToArray(), definitions.ToArray(), repetitions.ToArray());
    }

    static byte[] Copy(IntPtr pointer, int length) {
        byte[] bytes = new byte[length];
        if(length > 0) {
            Marshal.Copy(pointer, bytes, 0, length);
        }
        return bytes;
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
