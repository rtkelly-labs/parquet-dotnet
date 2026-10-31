using System.Runtime.InteropServices;
using System.Text;
using Parquet;
using Parquet.Data;
using Parquet.Encryption;
using Parquet.Schema;
using PS = ParquetSharp;
using PSchema = ParquetSharp.Schema;

namespace Lake.Parquet.Conformance;

/// <summary>
/// The other direction: parquet-cpp (ParquetSharp) writes a generated case with its own choices (encodings per
/// column, dictionaries, data page v1 or v2, page sizes, page checksums and indexes, statistics, encryption),
/// and the library must read back exactly the values, levels, field IDs and statistics. Files written by
/// other engines exercise decoding paths the library's own writer never produces.
/// </summary>
public static class Reverse {

    sealed record Column(LeafKind Kind, PS.PhysicalType Physical, PS.LogicalType Logical, int Length);

    static readonly PS.Compression[] Compressions =
        [PS.Compression.Uncompressed, PS.Compression.Snappy, PS.Compression.Gzip, PS.Compression.Brotli, PS.Compression.Zstd, PS.Compression.Lz4];

    public static async Task<IReadOnlyList<string>> RunAsync(Case c, string directory) {
        IReadOnlyList<IReadOnlyList<LeafChunk>> groups = Harness.Generate(c);
        var random = new Random(c.Seed * 7919 + 1);
        string path = Path.Combine(directory, $"cpp-{c.Seed}.parquet");
        var choices = new StringBuilder();
        try {
            Write(c, groups, path, random, choices);
        } catch(Exception e) {
            return [$"parquet-cpp could not write the case ({choices}): {e.Message}"];
        }
        var problems = new List<string>();
        try {
            await ReadAsync(c, groups, path, problems);
        } catch(Exception e) {
            problems.Add($"reading a parquet-cpp file failed: {e.GetType().Name}: {e.Message}");
        }
        return problems.Select(p => $"{p} [parquet-cpp wrote: {choices}]").ToList();
    }

    // ---- writing with parquet-cpp ----

    static Column Describe(LeafKind kind) {
        string name = kind.Name;
        if(name.StartsWith("decimal(", StringComparison.Ordinal)) {
            string[] ps = name[8..name.IndexOf(')')].Split(',');
            int precision = int.Parse(ps[0]), scale = int.Parse(ps[1]);
            bool bytes = name.EndsWith("fixed", StringComparison.Ordinal) || precision > 18;
            PS.LogicalType decimalType = PS.LogicalType.Decimal(precision, scale);
            return bytes
                ? new Column(kind, PS.PhysicalType.FixedLenByteArray, decimalType, (int)Math.Ceiling((precision * Math.Log2(10) + 1) / 8))
                : new Column(kind, precision <= 9 ? PS.PhysicalType.Int32 : PS.PhysicalType.Int64, decimalType, -1);
        }
        if(name.StartsWith("timestamp_", StringComparison.Ordinal)) {
            PS.TimeUnit unit = name.Contains("nanos") ? PS.TimeUnit.Nanos : name.Contains("micros") ? PS.TimeUnit.Micros : PS.TimeUnit.Millis;
            bool utc = name.Contains("utc") || name.Contains("legacy") || name.Contains("physical");
            return new Column(kind, PS.PhysicalType.Int64, PS.LogicalType.Timestamp(utc, unit), -1);
        }
        return name switch {
            "int8" => new(kind, PS.PhysicalType.Int32, PS.LogicalType.Int(8, true), -1),
            "uint8" => new(kind, PS.PhysicalType.Int32, PS.LogicalType.Int(8, false), -1),
            "int16" => new(kind, PS.PhysicalType.Int32, PS.LogicalType.Int(16, true), -1),
            "uint16" => new(kind, PS.PhysicalType.Int32, PS.LogicalType.Int(16, false), -1),
            "int32" => new(kind, PS.PhysicalType.Int32, PS.LogicalType.None(), -1),
            "uint32" => new(kind, PS.PhysicalType.Int32, PS.LogicalType.Int(32, false), -1),
            "int64" => new(kind, PS.PhysicalType.Int64, PS.LogicalType.None(), -1),
            "uint64" => new(kind, PS.PhysicalType.Int64, PS.LogicalType.Int(64, false), -1),
            "bool" => new(kind, PS.PhysicalType.Boolean, PS.LogicalType.None(), -1),
            "float" => new(kind, PS.PhysicalType.Float, PS.LogicalType.None(), -1),
            "double" => new(kind, PS.PhysicalType.Double, PS.LogicalType.None(), -1),
            "string" => new(kind, PS.PhysicalType.ByteArray, PS.LogicalType.String(), -1),
            "binary" => new(kind, PS.PhysicalType.ByteArray, PS.LogicalType.None(), -1),
            "fixed[6]" => new(kind, PS.PhysicalType.FixedLenByteArray, PS.LogicalType.None(), 6),
            "uuid" => new(kind, PS.PhysicalType.FixedLenByteArray, PS.LogicalType.Uuid(), 16),
            "date" or "dateonly" => new(kind, PS.PhysicalType.Int32, PS.LogicalType.Date(), -1),
            _ => throw new NotSupportedException($"no parquet-cpp column for {name}"),
        };
    }

    static PSchema.Node Leaf(string name, Leaf leaf, bool nullable) {
        Column column = Describe(leaf.Kind);
        return new PSchema.PrimitiveNode(name, nullable ? PS.Repetition.Optional : PS.Repetition.Required,
            column.Logical, column.Physical, column.Length, leaf.Field.FieldId);
    }

    static PSchema.Node Node(Shape shape) {
        PS.Repetition Repetition(bool nullable) => nullable ? PS.Repetition.Optional : PS.Repetition.Required;
        return shape switch {
            Flat f => Leaf(f.Field.Name, f.Leaf, f.Nullable),
            ListShape l => new PSchema.GroupNode(l.Field.Name, Repetition(l.ListNullable),
                [new PSchema.GroupNode("list", PS.Repetition.Repeated, [Leaf("element", l.Element, l.ElementNullable)])],
                PS.LogicalType.List(), l.Field.FieldId),
            StructShape s => new PSchema.GroupNode(s.Field.Name, Repetition(s.Nullable),
                s.Children.Select(ch => Leaf(ch.Leaf.Field.Name, ch.Leaf, ch.Nullable)).ToArray(), null, s.Field.FieldId),
            MapShape m => new PSchema.GroupNode(m.Field.Name, Repetition(m.Nullable),
                [new PSchema.GroupNode("key_value", PS.Repetition.Repeated, [Leaf("key", m.Key, false), Leaf("value", m.Value, m.ValueNullable)])],
                PS.LogicalType.Map(), m.Field.FieldId),
            _ => throw new NotSupportedException(shape.GetType().Name),
        };
    }

    static PS.Encoding[] EncodingsFor(PS.PhysicalType physical) => physical switch {
        PS.PhysicalType.Int32 or PS.PhysicalType.Int64 => [PS.Encoding.Plain, PS.Encoding.DeltaBinaryPacked, PS.Encoding.ByteStreamSplit],
        PS.PhysicalType.Float or PS.PhysicalType.Double => [PS.Encoding.Plain, PS.Encoding.ByteStreamSplit],
        PS.PhysicalType.ByteArray => [PS.Encoding.Plain, PS.Encoding.DeltaLengthByteArray, PS.Encoding.DeltaByteArray],
        PS.PhysicalType.FixedLenByteArray => [PS.Encoding.Plain, PS.Encoding.ByteStreamSplit, PS.Encoding.DeltaByteArray],
        _ => [PS.Encoding.Plain],
    };

    static void Write(Case c, IReadOnlyList<IReadOnlyList<LeafChunk>> groups, string path, Random random, StringBuilder choices) {
        using var root = new PSchema.GroupNode("schema", PS.Repetition.Required, c.Columns.Select(Node).ToArray());
        using var builder = new PS.WriterPropertiesBuilder();
        PS.Compression compression = Compressions[random.Next(Compressions.Length)];
        bool v2 = random.Next(2) == 0;
        bool statistics = random.Next(6) != 0;
        long pageSize = random.Next(3) == 0 ? random.Next(64, 2048) : 1024 * 1024;
        builder.Compression(compression).DataPageVersion(v2 ? PS.ParquetDataPageVersion.V2 : PS.ParquetDataPageVersion.V1).DataPagesize(pageSize);
        builder.WriteBatchSize(random.Next(1, 200));
        if(!statistics) {
            builder.DisableStatistics();
        }
        if(random.Next(3) == 0) {
            builder.EnablePageChecksum();
        }
        if(random.Next(3) == 0) {
            builder.EnableWritePageIndex();
        }
        choices.Append($"{compression}, data page {(v2 ? "v2" : "v1")}, page size {pageSize}, statistics {(statistics ? "on" : "off")}");

        Leaf[] leaves = c.Leaves.ToArray();
        for(int i = 0; i < leaves.Length; i++) {
            Column column = Describe(leaves[i].Kind);
            string columnPath = ColumnPath(root, i);
            PS.Encoding[] encodings = EncodingsFor(column.Physical);
            PS.Encoding encoding = encodings[random.Next(encodings.Length)];
            if(encoding != PS.Encoding.Plain || random.Next(3) == 0) {
                builder.DisableDictionary(columnPath);
                builder.Encoding(columnPath, encoding);
                choices.Append($", {columnPath}: {encoding}");
            } else {
                choices.Append($", {columnPath}: dictionary");
            }
        }

        if(c.Encryption is { } e) {
            using var encryption = new PS.FileEncryptionPropertiesBuilder(e.FooterKey);
            if(e.PlaintextFooter) {
                encryption.SetPlaintextFooter();
            }
            // parquet-cpp 25 records its footer-signing algorithm (AES_GCM_V1) in a plaintext footer even when
            // pages use AES_GCM_CTR_V1, so nobody, parquet-cpp included, can read such a file: not generated.
            bool ctr = e.Ctr && !e.PlaintextFooter;
            encryption.Algorithm(ctr ? PS.ParquetCipher.AesGcmCtrV1 : PS.ParquetCipher.AesGcmV1);
            var columnProperties = e.ColumnKeys.Select(k => {
                using var columnBuilder = new PS.ColumnEncryptionPropertiesBuilder(k.Key);
                return columnBuilder.Key(k.Value).KeyMetadata(k.Key).Build();
            }).ToArray();
            if(columnProperties.Length > 0) {
                encryption.EncryptedColumns(columnProperties);
            }
            using PS.FileEncryptionProperties properties = encryption.Build();
            builder.Encryption(properties);
            choices.Append($", encrypted ({(e.PlaintextFooter ? "plaintext footer" : "encrypted footer")}, {(ctr ? "CTR" : "GCM")})");
        }

        using PS.WriterProperties writerProperties = builder.Build();
        using var file = new PS.ParquetFileWriter(path, root, writerProperties);
        foreach(IReadOnlyList<LeafChunk> group in groups) {
            using PS.RowGroupWriter rowGroup = file.AppendRowGroup();
            foreach(LeafChunk chunk in group) {
                Column column = Describe(chunk.Leaf.Kind);
                using PS.ColumnWriter writer = rowGroup.NextColumn();
                WriteColumn(writer, chunk, column);
            }
        }
        file.Close();
    }

    static string ColumnPath(PSchema.GroupNode root, int leafIndex) {
        var paths = new List<string>();
        void Walk(PSchema.Node node, string prefix) {
            string here = prefix.Length == 0 ? node.Name : $"{prefix}.{node.Name}";
            if(node is PSchema.GroupNode g) {
                for(int i = 0; i < g.FieldCount; i++) {
                    Walk(g.Field(i), here);
                }
            } else {
                paths.Add(here);
            }
        }
        for(int i = 0; i < root.FieldCount; i++) {
            Walk(root.Field(i), "");
        }
        return paths[leafIndex];
    }

    static void WriteColumn(PS.ColumnWriter writer, LeafChunk chunk, Column column) {
        Leaf leaf = chunk.Leaf;
        short[]? definitions = leaf.MaxDefinition > 0 ? chunk.Definitions.Select(d => (short)d).ToArray() : null;
        short[]? repetitions = leaf.MaxRepetition > 0 ? chunk.Repetitions.Select(r => (short)r).ToArray() : null;
        object[] physical = chunk.Values.Select(v => leaf.Kind.Physical(v, column.Length)).ToArray();
        int entries = chunk.Definitions.Length;
        switch(writer) {
            case PS.ColumnWriter<bool> w: w.WriteBatch(entries, definitions, repetitions, physical.Cast<bool>().ToArray()); break;
            case PS.ColumnWriter<int> w: w.WriteBatch(entries, definitions, repetitions, physical.Cast<int>().ToArray()); break;
            case PS.ColumnWriter<long> w: w.WriteBatch(entries, definitions, repetitions, physical.Cast<long>().ToArray()); break;
            case PS.ColumnWriter<float> w: w.WriteBatch(entries, definitions, repetitions, physical.Cast<float>().ToArray()); break;
            case PS.ColumnWriter<double> w: w.WriteBatch(entries, definitions, repetitions, physical.Cast<double>().ToArray()); break;
            case PS.ColumnWriter<PS.ByteArray> w:
                WithUnmanaged(physical.Cast<byte[]>().ToArray(), pointers =>
                    w.WriteBatch(entries, definitions, repetitions, pointers.Select((p, i) => new PS.ByteArray(p, ((byte[])physical[i]).Length)).ToArray()));
                break;
            case PS.ColumnWriter<PS.FixedLenByteArray> w:
                WithUnmanaged(physical.Cast<byte[]>().ToArray(), pointers =>
                    w.WriteBatch(entries, definitions, repetitions, pointers.Select(p => new PS.FixedLenByteArray(p)).ToArray()));
                break;
            default:
                throw new NotSupportedException(writer.GetType().Name);
        }
    }

    /// <summary>Copies byte strings to unmanaged memory for the duration of a write.</summary>
    static void WithUnmanaged(byte[][] values, Action<IntPtr[]> write) {
        int total = values.Sum(v => v.Length);
        IntPtr block = Marshal.AllocHGlobal(Math.Max(total, 1));
        try {
            var pointers = new IntPtr[values.Length];
            int offset = 0;
            for(int i = 0; i < values.Length; i++) {
                pointers[i] = block + offset;
                Marshal.Copy(values[i], 0, pointers[i], values[i].Length);
                offset += values[i].Length;
            }
            write(pointers);
        } finally {
            Marshal.FreeHGlobal(block);
        }
    }

    // ---- reading with the library ----

    static async Task ReadAsync(Case c, IReadOnlyList<IReadOnlyList<LeafChunk>> groups, string path, List<string> problems) {
        await using FileStream stream = System.IO.File.OpenRead(path);
        var options = new ParquetOptions {
            Decryption = c.Encryption is { } e
                ? new ParquetDecryptionOptions { FooterKey = e.FooterKey, KeyRetriever = metadata => metadata == null ? null : e.ColumnKeys[Encoding.UTF8.GetString(metadata)] }
                : null,
        };
        await using ParquetReader reader = await ParquetReader.CreateAsync(stream, options);
        if(reader.RowGroupCount != groups.Count) {
            problems.Add($"reverse: {reader.RowGroupCount} row groups, expected {groups.Count}");
            return;
        }
        Field[] written = c.Columns.SelectMany(col => Case.Walk(col.Field)).ToArray();
        Field[] read = reader.Schema.Fields.SelectMany(Case.Walk).ToArray();
        if(written.Length != read.Length) {
            problems.Add($"reverse: the schema reads as {read.Length} fields, expected {written.Length}");
            return;
        }
        for(int i = 0; i < written.Length; i++) {
            if(written[i].FieldId != read[i].FieldId) {
                problems.Add($"reverse: {read[i].Path} has field ID {read[i].FieldId}, expected {written[i].FieldId}");
            }
        }

        DataField[] fields = reader.Schema.GetDataFields();
        Leaf[] leaves = c.Leaves.ToArray();
        for(int g = 0; g < groups.Count; g++) {
            using ParquetRowGroupReader rowGroup = reader.OpenRowGroupReader(g);
            if(rowGroup.RowCount != c.RowGroups[g]) {
                problems.Add($"reverse: row group {g} has {rowGroup.RowCount} rows, expected {c.RowGroups[g]}");
            }
            for(int i = 0; i < leaves.Length; i++) {
                LeafChunk chunk = groups[g][i];
                string where = $"reverse: row group {g}, {fields[i].Path} ({leaves[i].Kind})";
                if(fields[i].MaxDefinitionLevel != leaves[i].MaxDefinition || fields[i].MaxRepetitionLevel != leaves[i].MaxRepetition) {
                    problems.Add($"{where}: levels (def {fields[i].MaxDefinitionLevel}, rep {fields[i].MaxRepetitionLevel}), expected (def {leaves[i].MaxDefinition}, rep {leaves[i].MaxRepetition})");
                    continue;
                }
                try {
                    (List<object> values, int[]? definitions, int[]? repetitions) =
                        await leaves[i].Kind.ReadAsync(rowGroup, fields[i], chunk.Definitions.Length);
                    Harness.CompareValues(where, leaves[i].Kind, chunk.Values, values, problems);
                    if(definitions != null && !definitions.SequenceEqual(chunk.Definitions)) {
                        problems.Add($"{where}: definition levels differ ({Harness.Diff(chunk.Definitions, definitions)})");
                    }
                    if(repetitions != null && !repetitions.SequenceEqual(chunk.Repetitions)) {
                        problems.Add($"{where}: repetition levels differ ({Harness.Diff(chunk.Repetitions, repetitions)})");
                    }
                } catch(Exception ex) {
                    // a ParquetException explains itself; anything else is a crash, whose stack says where
                    problems.Add($"{where}: reading failed: {ex.GetType().Name}: {ex.Message}" +
                        (ex is ParquetException or NotSupportedException ? "" : $"\n{ex.StackTrace}"));
                    continue;
                }
                CheckStatistics(chunk, rowGroup.GetStatistics(fields[i]), Describe(leaves[i].Kind).Length, where, problems);
            }
        }
    }

    /// <summary>When parquet-cpp wrote min/max, the library decodes them to exactly the values' bounds.</summary>
    static void CheckStatistics(LeafChunk chunk, DataColumnStatistics? stats, int typeLength, string where, List<string> problems) {
        if(stats is not { HasMinMax: true }) {
            return;   // statistics off, or a type parquet-cpp writes none for
        }
        (object Min, object Max)? expected = Harness.Bounds(chunk.Leaf.Kind, chunk.Values, typeLength);
        if(expected == null) {
            return;
        }
        if(!Harness.SamePhysical(stats.PhysicalMin, expected.Value.Min) || !Harness.SamePhysical(stats.PhysicalMax, expected.Value.Max)) {
            problems.Add($"{where}: statistics decode as {Harness.Show(stats.PhysicalMin)} .. {Harness.Show(stats.PhysicalMax)}, expected {Harness.Show(expected.Value.Min)} .. {Harness.Show(expected.Value.Max)}");
        }
    }
}
