using System.Collections.Generic;
using Parquet.Data;
using Parquet.Meta;

namespace Parquet.Schema;

/// <summary>
/// A leaf column as stored: its physical type, annotations and levels, straight from the file's schema (no CLR type
/// mapping), in file order.
/// </summary>
public sealed class ColumnDescriptor {
    internal ColumnDescriptor(int ordinal, FieldPath path, SchemaElement element, int maxDefinitionLevel, int maxRepetitionLevel) {
        Ordinal = ordinal;
        Path = path;
        SchemaElement = element;
        MaxDefinitionLevel = maxDefinitionLevel;
        MaxRepetitionLevel = maxRepetitionLevel;
    }

    /// <summary>Position among the leaf columns (the column ordinal of row group column chunks).</summary>
    public int Ordinal { get; }

    /// <summary>Path in the file's schema, group names included (<c>tags.list.element</c>).</summary>
    public FieldPath Path { get; }

    /// <summary>The thrift schema element.</summary>
    public SchemaElement SchemaElement { get; }

    /// <summary>Physical type.</summary>
    public Type PhysicalType => SchemaElement.Type!.Value;

    /// <summary>Byte length of FIXED_LEN_BYTE_ARRAY values; null otherwise.</summary>
    public int? TypeLength => PhysicalType == Type.FIXED_LEN_BYTE_ARRAY ? SchemaElement.TypeLength : null;

    /// <summary>Logical type annotation, if any.</summary>
    public LogicalType? LogicalType => SchemaElement.LogicalType;

    /// <summary>Legacy converted type annotation, if any.</summary>
    public ConvertedType? ConvertedType => SchemaElement.ConvertedType;

    /// <summary>Field ID, if the writer set one.</summary>
    public int? FieldId => SchemaElement.FieldId;

    /// <summary>Highest definition level (a value is present at this level).</summary>
    public int MaxDefinitionLevel { get; }

    /// <summary>Highest repetition level.</summary>
    public int MaxRepetitionLevel { get; }

    /// <summary>The order min/max statistics follow for this column's type.</summary>
    public ColumnSortOrder SortOrder => ColumnStatisticsRules.SortOrder(SchemaElement);

    /// <inheritdoc/>
    public override string ToString() => $"{Path} ({PhysicalType}, DL {MaxDefinitionLevel}, RL {MaxRepetitionLevel})";

    /// <summary>The leaf columns of a flattened thrift schema (the root first).</summary>
    internal static List<ColumnDescriptor> Of(List<SchemaElement> schema) {
        var result = new List<ColumnDescriptor>();
        int index = 1;
        void Walk(int children, List<string> path, int def, int rep) {
            for(int c = 0; c < children && index < schema.Count; c++) {
                SchemaElement se = schema[index++];
                int d = def + (se.RepetitionType is FieldRepetitionType.OPTIONAL or FieldRepetitionType.REPEATED ? 1 : 0);
                int r = rep + (se.RepetitionType == FieldRepetitionType.REPEATED ? 1 : 0);
                var p = new List<string>(path) { se.Name };
                if(se.NumChildren is > 0)
                    Walk(se.NumChildren.Value, p, d, r);
                else
                    result.Add(new ColumnDescriptor(result.Count, new FieldPath(p), se, d, r));
            }
        }
        if(schema.Count > 0)
            Walk(schema[0].NumChildren ?? 0, new List<string>(), 0, 0);
        return result;
    }
}
