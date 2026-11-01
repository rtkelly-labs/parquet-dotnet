using System;
using System.Collections.Generic;
using Parquet.Schema;

namespace Parquet.Data;

/// <summary>
/// A nested leaf column's definition and repetition levels as Arrow lays nested data out (Lake.NET decision 11):
/// one offsets array per repeated ancestor and one validity mask per optional ancestor, so code that builds
/// <c>List&lt;List&lt;T&gt;&gt;</c>, dictionaries or records walks offset ranges instead of levels.
/// </summary>
/// <remarks>
/// <para>Layer 0 is the rows; layer <c>i</c> (1 ≤ i ≤ <see cref="RepeatedCount"/>) is the elements of the <c>i</c>-th
/// repeated ancestor, outermost first: a list's elements, or a map's key-value pairs. <see cref="Offsets"/>[<c>i</c>]
/// gives, for each slot of layer <c>i</c>, the range of its elements in layer <c>i + 1</c>; a null or empty list has an
/// empty range.</para>
/// <para>Each optional ancestor, and the leaf when it is optional, has a validity mask over the slots of the layer it
/// is in (<see cref="NestedValidity"/>): a null list, a null struct, a null map, a null element. Masks of one layer are
/// nested: where an outer one is false, the inner ones are false too.</para>
/// <para>The leaf's non-null values, read packed (<see cref="ParquetRowGroupReader.ReadRawAsync{T}"/>), belong to the
/// slots of the last layer whose masks are all true, in order: <see cref="ValueCount"/> of them.</para>
/// </remarks>
public sealed class NestedLayout {

    internal NestedLayout(int[] slotCounts, int[][] offsets, NestedValidity[] validity, int valueCount) {
        SlotCounts = slotCounts;
        Offsets = offsets;
        Validity = validity;
        ValueCount = valueCount;
    }

    /// <summary>The number of repeated ancestors: lists, maps and legacy repeated fields.</summary>
    public int RepeatedCount => Offsets.Count;

    /// <summary>Slots in each layer: <c>[0]</c> is the row count.</summary>
    public IReadOnlyList<int> SlotCounts { get; }

    /// <summary>
    /// For each layer but the last, <c>SlotCounts[i] + 1</c> offsets into layer <c>i + 1</c>: slot <c>s</c>'s elements
    /// are <c>Offsets[i][s]</c> up to <c>Offsets[i][s + 1]</c>.
    /// </summary>
    public IReadOnlyList<int[]> Offsets { get; }

    /// <summary>The validity masks, outermost first.</summary>
    public IReadOnlyList<NestedValidity> Validity { get; }

    /// <summary>The number of non-null leaf values.</summary>
    public int ValueCount { get; }

    /// <summary>
    /// The layout of a leaf column's levels, as read with <see cref="ParquetRowGroupReader.ReadRawAsync{T}"/> (one
    /// definition and repetition level per entry; <paramref name="repetitionLevels"/> may be empty for a leaf with no
    /// repeated ancestor, <paramref name="definitionLevels"/> for one with no optional or repeated ancestor).
    /// </summary>
    /// <param name="schema">The schema the leaf belongs to.</param>
    /// <param name="leaf">The leaf column.</param>
    /// <param name="definitionLevels">One definition level per entry.</param>
    /// <param name="repetitionLevels">One repetition level per entry.</param>
    /// <param name="entries">The number of entries: the column chunk's value count.</param>
    public static NestedLayout FromLevels(ParquetSchema schema, DataField leaf,
        ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels, int entries) {
        Chain chain = Chain.Of(schema, leaf);
        int layers = chain.RepeatedDefinition.Length;   // index 0 unused: the rows
        if(definitionLevels.Length < entries && chain.MaxDefinition > 0)
            throw new ArgumentException($"{entries} entries but {definitionLevels.Length} definition levels", nameof(definitionLevels));
        if(repetitionLevels.Length < entries && layers > 1)
            throw new ArgumentException($"{entries} entries but {repetitionLevels.Length} repetition levels", nameof(repetitionLevels));

        var counts = new int[layers];
        var offsets = new List<int>[layers - 1];
        for(int i = 0; i < offsets.Length; i++)
            offsets[i] = new List<int>();
        var masks = new List<bool>[chain.Optional.Length];
        for(int i = 0; i < masks.Length; i++)
            masks[i] = new List<bool>();
        int values = 0;

        for(int e = 0; e < entries; e++) {
            int d = chain.MaxDefinition > 0 ? definitionLevels[e] : 0;
            int r = layers > 1 ? repetitionLevels[e] : 0;
            if(d < 0 || d > chain.MaxDefinition || r < 0 || r >= layers || (e == 0 && r != 0))
                throw new ArgumentException($"entry {e}: definition level {d} and repetition level {r} are not levels of '{leaf.Path}'");
            for(int layer = r; layer < layers; layer++) {
                // a new slot in this layer: a new row, or a new element of the layer's repeated ancestor
                if(layer > 0 && d < chain.RepeatedDefinition[layer]) {
                    if(layer == r)
                        throw new ArgumentException($"entry {e}: repetition level {r} continues a list, but definition level {d} says it is empty or null");
                    break;
                }
                counts[layer]++;
                for(int o = 0; o < chain.Optional.Length; o++) {
                    if(chain.OptionalLayer[o] == layer)
                        masks[o].Add(d >= chain.Optional[o]);
                }
                if(layer < layers - 1)
                    offsets[layer].Add(counts[layer + 1]);
            }
            if(d == chain.MaxDefinition)
                values++;
        }

        var offsetArrays = new int[layers - 1][];
        for(int i = 0; i < offsetArrays.Length; i++) {
            offsets[i].Add(counts[i + 1]);
            offsetArrays[i] = offsets[i].ToArray();
        }
        var validity = new NestedValidity[masks.Length];
        for(int o = 0; o < masks.Length; o++)
            validity[o] = new NestedValidity(chain.OptionalField[o], chain.OptionalLayer[o], chain.Optional[o], masks[o].ToArray());
        return new NestedLayout(counts, offsetArrays, validity, values);
    }

    /// <summary>
    /// The definition and repetition levels of this layout, for <see cref="ParquetRowGroupWriter.WriteAllPartsAsync{T}"/>:
    /// one entry per slot of the last layer, plus one for each null or empty list and each slot a null ancestor ends.
    /// </summary>
    public (int[] DefinitionLevels, int[] RepetitionLevels) ToLevels(ParquetSchema schema, DataField leaf) {
        Chain chain = Chain.Of(schema, leaf);
        int layers = chain.RepeatedDefinition.Length;
        if(layers != SlotCounts.Count || chain.Optional.Length != Validity.Count)
            throw new ArgumentException($"this layout is not of '{leaf.Path}'", nameof(leaf));
        var defs = new List<int>();
        var reps = new List<int>();
        for(int row = 0; row < SlotCounts[0]; row++)
            Emit(0, row, 0);
        return (defs.ToArray(), reps.ToArray());

        void Emit(int layer, int slot, int rep) {
            // the deepest definition level this slot reaches in its own layer
            int d = layer == 0 ? 0 : chain.RepeatedDefinition[layer];
            for(int o = 0; o < chain.Optional.Length; o++) {
                if(chain.OptionalLayer[o] != layer)
                    continue;
                if(!Validity[o].IsValid[slot]) {
                    defs.Add(chain.Optional[o] - 1);
                    reps.Add(rep);
                    return;
                }
                d = chain.Optional[o];
            }
            if(layer == layers - 1) {
                defs.Add(d);
                reps.Add(rep);
                return;
            }
            int start = Offsets[layer][slot], end = Offsets[layer][slot + 1];
            if(start == end) {
                // an empty list: present to the level below its repeated group
                defs.Add(chain.RepeatedDefinition[layer + 1] - 1);
                reps.Add(rep);
                return;
            }
            for(int child = start; child < end; child++)
                Emit(layer + 1, child, child == start ? rep : layer + 1);
        }
    }

    /// <summary>A leaf's ancestors as level boundaries: where each repeated and each optional one is defined.</summary>
    private sealed class Chain {
        public int MaxDefinition;
        public int[] RepeatedDefinition = [0];   // [layer]: the definition level at which that layer's repeated ancestor has an element
        public int[] Optional = [];              // the definition level at which each optional ancestor is present
        public int[] OptionalLayer = [];
        public Field[] OptionalField = [];

        public static Chain Of(ParquetSchema schema, DataField leaf) {
            List<Field>? path = Find(schema.Fields, leaf, new List<Field>())
                ?? throw new ArgumentException($"'{leaf.Path}' is not in this schema", nameof(leaf));
            var repeated = new List<int> { 0 };
            var optional = new List<(int Definition, int Layer, Field Field)>();
            int def = 0;
            foreach(Field f in path) {
                switch(f) {
                    case ListField or MapField:
                        // nullable: the list or map itself can be null; then its repeated group, one layer down
                        if(f.IsNullable)
                            optional.Add((def + 1, repeated.Count - 1, f));
                        repeated.Add(f.MaxDefinitionLevel);
                        break;
                    case StructField when f.IsNullable:
                        optional.Add((f.MaxDefinitionLevel, repeated.Count - 1, f));
                        break;
                    case DataField d when d.IsArray:
                        repeated.Add(d.MaxDefinitionLevel);
                        break;
                    case DataField d when d.IsNullable:
                        optional.Add((d.MaxDefinitionLevel, repeated.Count - 1, f));
                        break;
                }
                def = f.MaxDefinitionLevel;
            }
            return new Chain {
                MaxDefinition = leaf.MaxDefinitionLevel,
                RepeatedDefinition = repeated.ToArray(),
                Optional = optional.ConvertAll(o => o.Definition).ToArray(),
                OptionalLayer = optional.ConvertAll(o => o.Layer).ToArray(),
                OptionalField = optional.ConvertAll(o => o.Field).ToArray(),
            };
        }

        private static List<Field>? Find(IEnumerable<Field> fields, DataField leaf, List<Field> path) {
            foreach(Field f in fields) {
                path.Add(f);
                if(ReferenceEquals(f, leaf) || (f is DataField && f.Path.Equals(leaf.Path)))
                    return path;
                IEnumerable<Field> children = f switch {
                    StructField s => s.Fields,
                    ListField l => [l.Item],
                    MapField m => [m.Key, m.Value],
                    _ => []
                };
                if(Find(children, leaf, path) is { } found)
                    return found;
                path.RemoveAt(path.Count - 1);
            }
            return null;
        }
    }
}

/// <summary>One optional ancestor's (or an optional leaf's) validity over the slots of its layer.</summary>
public sealed class NestedValidity {

    internal NestedValidity(Field field, int layer, int definitionLevel, bool[] isValid) {
        Field = field;
        Layer = layer;
        DefinitionLevel = definitionLevel;
        IsValid = isValid;
    }

    /// <summary>The optional field: a list, map or struct, or the leaf.</summary>
    public Field Field { get; }

    /// <summary>The layer whose slots the mask covers.</summary>
    public int Layer { get; }

    /// <summary>The definition level at which the field is present.</summary>
    public int DefinitionLevel { get; }

    /// <summary>Whether the field is present in each slot of its layer.</summary>
    public IReadOnlyList<bool> IsValid { get; }
}
