---
title: "Type discovery that trimming and native AOT keep working"
kind: code
status: fork-only
depends_on: [0022, 0024]
---

# 0047 Type discovery that trimming and native AOT keep working

## What and why

- **Problem:** with the serializer left out (`lake(0042)`, `lake(0045)`), three findings stood between Lake.Parquet and
  `IsAotCompatible`, all in how a `DataField` learns about its CLR type:
  - `TryExtractIEnumerableType` and `IsGenericIDictionary` read a type's interfaces, which the trimmer may remove from a
    type nothing else asks about, so a trimmed or native AOT app could stop recognising `List<T>` or a dictionary;
  - `GetNullable` built `Nullable<T>` with `MakeGenericType`, which native AOT cannot do for a type it has not compiled,
    and every field set nullable calls it.
- **Change:**
  - the `Type` handed to data fields is annotated `[DynamicallyAccessedMembers(Interfaces)]` wherever it flows: the two
    helpers, `DataField(name, clrType)` and its type discovery, `DataField<T>`'s `T`, `DecimalDataField`'s `clrType`,
    `ListField`'s `itemDataType`, and what `RawDataField`, `TimeDataField`, `SchemaEncoder` and `ParquetOptions` compute.
    Annotations are metadata only: callers passing concrete types are unaffected; a caller generic over `T` that passes
    `typeof(T)` annotates its `T` too (the analyzer says so);
  - `GetNullable` takes the nullable form of every value type a data field can have from a table; for other value
    types (enums) it constructs the `Nullable<T>` type, never an instance of it, which native AOT also supports (the
    native suite checks it), with a justified suppression; should a runtime refuse, the type itself.

## Evaluation

- **Tests:** `AotSafeTypesTest`: the nullable form of every supported value type, of nullable, reference and enum
  types, and of nullable fields; fields of array, `List<T>` and `IEnumerable<T>` types and the dictionary refusal; and
  that the public entry points carry the annotation (fails without this change).
- **Analyzers:** the stripped Lake.Parquet build (`lake(0045)`) has no trimming or AOT finding on net8.0 and net10.0,
  from 3; `lake(0048)` makes that a build error and proves it under native AOT.

## Upstream

Not yet proposed: a person proposes it from `lake/change/0047-aot-safe-type-discovery`. Upstream would also need the
serializer annotated or excluded to be AOT-compatible as a whole.

## Compatibility

None: the same types on the JIT and under native AOT.
