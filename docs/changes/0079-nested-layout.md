---
title: "NestedLayout: a nested leaf's levels as Arrow's offsets and validity, and back"
kind: code
status: fork-only
depends_on: [0062]
evidence: failing-test
---

# 0079 NestedLayout: a nested leaf's levels as Arrow's offsets and validity, and back

## What and why

Lake.NET decision 11: the library owns levels and offsets, and mapping objects belongs to Parquet.SourceGenerator.

- **Problem:** to build `List<List<T>>`, `Dictionary<K, V>` or nested records from a leaf column, a caller had to
  turn definition and repetition levels into structure itself. The generator does that in generated code, and stops
  at one level of lists and at maps (its PARQ013) because the level arithmetic does not generalise in emitted code.
  Lake.NET's read path and the generator's Arrow bridge want Arrow's layout, offsets and validity, which is the same
  conversion again.
- **Change:** a new public type, `Parquet.Data.NestedLayout`.
  - `NestedLayout.FromLevels(schema, leaf, definitionLevels, repetitionLevels, entries)` returns:
    - for each repeated ancestor (a list's repeated group, a map's key-value group, a legacy repeated field), an
      offsets array over the slots of the layer above;
    - for each optional ancestor (a nullable list, map or struct, and the leaf when it is optional), a validity mask
      over the slots of its layer, false under a null outer ancestor;
    - the count of non-null leaf values, which belong in order to the last layer's slots whose masks are all true.
  - `layout.ToLevels(schema, leaf)` is the inverse, for `WriteAllPartsAsync`.
  - The ancestors come from the schema tree's own levels (`MaxDefinitionLevel` and `MaxRepetitionLevel` at each
    node), so any depth works.
  - Levels that cannot belong to the leaf are refused: a first entry continuing a list, a level beyond the maximum,
    an entry continuing a list its definition level says is empty.
  - No reflection and no runtime generics, so native AOT is not affected.

## Evaluation

- **Oracle: Arrow itself.** `data/nested/nested_layout.parquet` is written by pyarrow 26 (`data/nested/generate.py`)
  with:
  - a list of lists of nullable ints;
  - a map from strings to lists of longs;
  - a list of structs holding an int and a list of strings;
  - nested optional structs;
  - a required list.

  Each has nulls and empties at every level. `data/nested/nested_layout.json` holds, for each of the seven leaves,
  the offsets and validity pyarrow builds when it reads the file. `Types/NestedLayoutTest` (16):
  - `FromLevels` on the levels the library reads gives exactly Arrow's offsets, masks and value counts, leaf by leaf;
  - `ToLevels` gives back the file's levels;
  - a `List<List<int?>>` assembled from the layout and the packed values alone is the data pyarrow wrote;
  - foreign levels are refused.
- **Without the change:** the tests do not compile.
- **Suite:** all pass on net8.0 and net10.0.

## Upstream

A new API, fork-only by decision. A person may propose it from `lake/change/0079-nested-layout`.

## Compatibility

Additive.
