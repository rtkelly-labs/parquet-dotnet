// Compiled only into Lake.Parquet, in place of the reflection-based serializer that Lake.Parquet.targets leaves out
// (README, "What Lake.Parquet leaves out"). SchemaEncoder uses one extension method from the serializer's
// ParquetClrSchemaExtensions; this is that method, alone. The Lake build compiles a renamed copy of src/, so this
// file is written in the Lake.Parquet namespace already.

using Lake.Parquet.Schema;

namespace Lake.Parquet.Serialization;

static class LakeTimeUnitExtensions {

    /// <summary>A footer's timestamp unit as the schema's.</summary>
    public static DateTimeTimeUnit Convert(this Lake.Parquet.Meta.TimeUnit unit) {
        if(unit.MILLIS is not null)
            return DateTimeTimeUnit.Millis;
        if(unit.MICROS is not null)
            return DateTimeTimeUnit.Micros;
        if(unit.NANOS is not null)
            return DateTimeTimeUnit.Nanos;
        throw new ParquetException($"Unexpected TimeUnit: {unit}");
    }
}
