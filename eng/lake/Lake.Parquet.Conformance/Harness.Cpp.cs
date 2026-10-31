using System.Runtime.InteropServices;
using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using PS = ParquetSharp;

namespace Lake.Parquet.Conformance;

/// <summary>The parquet-cpp half of the harness: the oracle the library's files are read back with.</summary>
public static partial class Harness {

    /// <summary>
    /// Checks a file another build of the library wrote for a case (native AOT, say) with every oracle: the library's
    /// round trip and statistics, and parquet-cpp reading and, for encrypted cases, decrypting it.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckWrittenFileAsync(Case c, string path) {
        var problems = new List<string>();
        IReadOnlyList<IReadOnlyList<LeafChunk>> groups = Generate(c);
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
}
