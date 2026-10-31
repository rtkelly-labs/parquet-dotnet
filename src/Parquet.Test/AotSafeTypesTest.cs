using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Numerics;
using Parquet.Data;
using Parquet.Extensions;
using Parquet.File.Values.Primitives;
using Parquet.Schema;
using Xunit;

namespace Parquet.Test;

/// <summary>
/// Type discovery that native AOT can do: the nullable form of every value type a data field can have comes from a
/// table, not from constructing <c>Nullable&lt;T&gt;</c> at run time, and fields built from collection types still find
/// their element type. Behaviour is the same as before on the JIT.
/// </summary>
public class AotSafeTypesTest {

    public static TheoryData<Type, Type> ValueTypes => new() {
        { typeof(bool), typeof(bool?) }, { typeof(byte), typeof(byte?) }, { typeof(sbyte), typeof(sbyte?) },
        { typeof(short), typeof(short?) }, { typeof(ushort), typeof(ushort?) }, { typeof(int), typeof(int?) },
        { typeof(uint), typeof(uint?) }, { typeof(long), typeof(long?) }, { typeof(ulong), typeof(ulong?) },
        { typeof(float), typeof(float?) }, { typeof(double), typeof(double?) }, { typeof(decimal), typeof(decimal?) },
        { typeof(BigInteger), typeof(BigInteger?) }, { typeof(BigDecimal), typeof(BigDecimal?) },
        { typeof(DateTime), typeof(DateTime?) }, { typeof(DateOnly), typeof(DateOnly?) }, { typeof(TimeOnly), typeof(TimeOnly?) },
        { typeof(TimeSpan), typeof(TimeSpan?) }, { typeof(Guid), typeof(Guid?) }, { typeof(Interval), typeof(Interval?) },
        { typeof(ReadOnlyMemory<byte>), typeof(ReadOnlyMemory<byte>?) }, { typeof(ReadOnlyMemory<char>), typeof(ReadOnlyMemory<char>?) },
    };

    [Theory]
    [MemberData(nameof(ValueTypes))]
    public void Nullable_form_of_each_value_type(Type type, Type nullable) {
        Assert.Equal(nullable, type.GetNullable());
        Assert.Equal(nullable, nullable.GetNullable());   // already nullable
    }

    enum Colour { Red, Green }

    [Fact]
    public void Reference_types_and_enums() {
        Assert.Equal(typeof(string), typeof(string).GetNullable());
        Assert.Equal(typeof(byte[]), typeof(byte[]).GetNullable());
        Assert.Equal(typeof(Colour?), typeof(Colour).GetNullable());   // constructed, where the runtime can
    }

    [Theory]
    [MemberData(nameof(ValueTypes))]
    public void Nullable_fields_report_their_nullable_type(Type type, Type _) {
        if(!DataField.CanCreateFrom(type)) {
            return;
        }
        // the field's own type, which can be another (TimeOnly is stored as long), made nullable
        var field = new DataField("f", type) { IsNullable = true };
        Assert.Equal(field.ClrType.GetNullable(), field.ClrNullableIfHasNullsType);
        Assert.True(Nullable.GetUnderlyingType(field.ClrNullableIfHasNullsType) == field.ClrType);
        field.IsNullable = false;
        Assert.Equal(field.ClrType, field.ClrNullableIfHasNullsType);
    }

    [Fact]
    public void Fields_of_collection_types_find_their_element_type() {
        Assert.True(new DataField("a", typeof(int[])).IsArray);
        Assert.Equal(typeof(int), new DataField("a", typeof(int[])).ClrType);
        Assert.Equal(typeof(long), new DataField("l", typeof(List<long>)).ClrType);
        Assert.True(new DataField("e", typeof(IEnumerable<string>)).IsArray);
        Assert.Equal(typeof(ReadOnlyMemory<char>), new DataField("e", typeof(IEnumerable<string>)).ClrType);   // strings are stored as chars
        Assert.Throws<NotSupportedException>(() => new DataField("d", typeof(Dictionary<string, int>)));
    }

    static bool KeepsInterfaces(ICustomAttributeProvider target) =>
        target.GetCustomAttributes(typeof(DynamicallyAccessedMembersAttribute), false)
            .Cast<DynamicallyAccessedMembersAttribute>()
            .Any(a => a.MemberTypes.HasFlag(DynamicallyAccessedMemberTypes.Interfaces));

    /// <summary>
    /// The types handed to data fields are annotated so the trimmer keeps the interfaces type discovery inspects;
    /// without them, a trimmed or native AOT app could no longer tell a collection type from its element.
    /// </summary>
    [Fact]
    public void Types_given_to_data_fields_keep_their_interfaces_when_trimmed() {
        ParameterInfo Parameter(Type type, string name) =>
            type.GetConstructors().SelectMany(c => c.GetParameters()).First(p => p.Name == name);

        Assert.True(KeepsInterfaces(Parameter(typeof(DataField), "clrType")), "DataField(name, clrType)");
        Assert.True(KeepsInterfaces(typeof(DataField<>).GetGenericArguments()[0]), "DataField<T>");
        Assert.True(KeepsInterfaces(Parameter(typeof(DecimalDataField), "clrType")), "DecimalDataField(..., clrType)");
        Assert.True(KeepsInterfaces(Parameter(typeof(ListField), "itemDataType")), "ListField(name, itemDataType)");
    }
}
