using Parquet.Schema;

namespace Lake.Parquet.Conformance;

/// <summary>
/// One leaf column of a case: its kind, its field, the levels the harness expects for it (worked out from the
/// shape, independently of the library), and the entries generated for the current row group.
/// </summary>
public sealed class Leaf {

    public required LeafKind Kind { get; init; }
    public required DataField Field { get; init; }
    public required int MaxDefinition { get; init; }
    public required int MaxRepetition { get; init; }

    /// <summary>The defined values, in order: one per entry whose definition level is the maximum.</summary>
    public List<object> Values { get; } = [];
    public List<int> Definitions { get; } = [];
    public List<int> Repetitions { get; } = [];

    public int Entries => Definitions.Count;

    public void Add(int definition, int repetition, Random? random = null) {
        Definitions.Add(definition);
        Repetitions.Add(repetition);
        if(definition == MaxDefinition) {
            Values.Add(Kind.Next(random ?? throw new ArgumentNullException(nameof(random))));
        }
    }

    public void Clear() {
        Values.Clear();
        Definitions.Clear();
        Repetitions.Clear();
    }
}

/// <summary>A top-level column: a field (possibly nested) and the leaves under it.</summary>
public abstract class Shape {

    public abstract Field Field { get; }
    public abstract IReadOnlyList<Leaf> Leaves { get; }

    /// <summary>Appends one row's entries to every leaf.</summary>
    public abstract void AddRow(Random random);

    public abstract string Describe();

    protected static bool Chance(Random random, int oneIn) => random.Next(oneIn) == 0;
}

/// <summary>A required or optional primitive.</summary>
public sealed class Flat : Shape {

    readonly Leaf _leaf;
    readonly bool _nullable;

    public bool Nullable => _nullable;

    public Flat(string name, LeafKind kind, bool nullable) {
        _nullable = nullable;
        DataField field = kind.Field(name, nullable);
        _leaf = new Leaf { Kind = kind, Field = field, MaxDefinition = nullable ? 1 : 0, MaxRepetition = 0 };
    }

    public override Field Field => _leaf.Field;
    public override IReadOnlyList<Leaf> Leaves => [_leaf];
    public Leaf Leaf => _leaf;

    public override void AddRow(Random random) =>
        _leaf.Add(_nullable && Chance(random, 5) ? 0 : _leaf.MaxDefinition, 0, random);

    public override string Describe() => $"{Field.Name}: {(_nullable ? "optional" : "required")} {_leaf.Kind}";
}

/// <summary>A three-level list (LogicalTypes.md): optional or required list, optional or required element.</summary>
public sealed class ListShape : Shape {

    readonly ListField _field;
    readonly Leaf _leaf;
    readonly bool _listNullable, _elementNullable;

    public bool ListNullable => _listNullable;
    public bool ElementNullable => _elementNullable;

    public ListShape(string name, LeafKind kind, bool listNullable, bool elementNullable) {
        (_listNullable, _elementNullable) = (listNullable, elementNullable);
        DataField element = kind.Field("element", elementNullable);
        _field = new ListField(name, element, isNullable: listNullable);
        _leaf = new Leaf {
            Kind = kind, Field = element,
            MaxDefinition = (listNullable ? 1 : 0) + 1 + (elementNullable ? 1 : 0),
            MaxRepetition = 1,
        };
    }

    public override Field Field => _field;
    public override IReadOnlyList<Leaf> Leaves => [_leaf];
    public Leaf Element => _leaf;

    public override void AddRow(Random random) {
        int present = _listNullable ? 1 : 0;
        if(_listNullable && Chance(random, 6)) {
            _leaf.Add(0, 0);                    // a null list
            return;
        }
        if(Chance(random, 6)) {
            _leaf.Add(present, 0);              // an empty list
            return;
        }
        for(int i = random.Next(1, 5), first = 1; i > 0; i--, first = 0) {
            bool nullElement = _elementNullable && Chance(random, 5);
            _leaf.Add(nullElement ? present + 1 : _leaf.MaxDefinition, first == 1 ? 0 : 1, random);
        }
    }

    public override string Describe() =>
        $"{Field.Name}: {(_listNullable ? "optional" : "required")} list<{(_elementNullable ? "optional" : "required")} {_leaf.Kind}>";
}

/// <summary>An optional or required struct of primitives.</summary>
public sealed class StructShape : Shape {

    readonly StructField _field;
    readonly List<(Leaf Leaf, bool Nullable)> _children = [];
    readonly bool _nullable;

    public bool Nullable => _nullable;
    public IReadOnlyList<(Leaf Leaf, bool Nullable)> Children => _children;

    public StructShape(string name, IReadOnlyList<(LeafKind Kind, bool Nullable)> children, bool nullable) {
        _nullable = nullable;
        int present = nullable ? 1 : 0;
        var fields = new List<Field>();
        for(int i = 0; i < children.Count; i++) {
            DataField child = children[i].Kind.Field($"f{i}", children[i].Nullable);
            fields.Add(child);
            _children.Add((new Leaf {
                Kind = children[i].Kind, Field = child,
                MaxDefinition = present + (children[i].Nullable ? 1 : 0),
                MaxRepetition = 0,
            }, children[i].Nullable));
        }
        _field = new StructField(name, fields, nullable);
    }

    public override Field Field => _field;
    public override IReadOnlyList<Leaf> Leaves => _children.Select(c => c.Leaf).ToList();

    public override void AddRow(Random random) {
        if(_nullable && Chance(random, 6)) {
            foreach((Leaf leaf, _) in _children) {
                leaf.Add(0, 0);
            }
            return;
        }
        int present = _nullable ? 1 : 0;
        foreach((Leaf leaf, bool nullable) in _children) {
            leaf.Add(nullable && Chance(random, 5) ? present : leaf.MaxDefinition, 0, random);
        }
    }

    public override string Describe() =>
        $"{Field.Name}: {(_nullable ? "optional" : "required")} struct<{string.Join(", ", _children.Select(c => $"{c.Leaf.Field.Name}: {(c.Nullable ? "optional" : "required")} {c.Leaf.Kind}"))}>";
}

/// <summary>An optional or required map with required keys and optional or required values.</summary>
public sealed class MapShape : Shape {

    readonly MapField _field;
    readonly Leaf _key, _value;
    readonly bool _nullable, _valueNullable;

    public bool Nullable => _nullable;
    public bool ValueNullable => _valueNullable;
    public Leaf Key => _key;
    public Leaf Value => _value;

    public MapShape(string name, LeafKind keyKind, LeafKind valueKind, bool nullable, bool valueNullable) {
        (_nullable, _valueNullable) = (nullable, valueNullable);
        int present = nullable ? 1 : 0;
        DataField key = keyKind.Field("key", false);
        DataField value = valueKind.Field("value", valueNullable);
        _field = new MapField(name, key, value, nullable);
        _key = new Leaf { Kind = keyKind, Field = key, MaxDefinition = present + 1, MaxRepetition = 1 };
        _value = new Leaf { Kind = valueKind, Field = value, MaxDefinition = present + 1 + (valueNullable ? 1 : 0), MaxRepetition = 1 };
    }

    public override Field Field => _field;
    public override IReadOnlyList<Leaf> Leaves => [_key, _value];

    public override void AddRow(Random random) {
        int present = _nullable ? 1 : 0;
        if(_nullable && Chance(random, 6)) {
            _key.Add(0, 0);
            _value.Add(0, 0);
            return;
        }
        if(Chance(random, 6)) {
            _key.Add(present, 0);
            _value.Add(present, 0);
            return;
        }
        for(int i = random.Next(1, 4), first = 1; i > 0; i--, first = 0) {
            int repetition = first == 1 ? 0 : 1;
            _key.Add(_key.MaxDefinition, repetition, random);
            _value.Add(_valueNullable && Chance(random, 5) ? present + 1 : _value.MaxDefinition, repetition, random);
        }
    }

    public override string Describe() =>
        $"{Field.Name}: {(_nullable ? "optional" : "required")} map<{_key.Kind}, {(_valueNullable ? "optional" : "required")} {_value.Kind}>";
}
