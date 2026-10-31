using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Parquet.Extensions;

static class ParquetClrSchemaExtensions {
    /// <summary>
    /// Creates a generic typed list of elements of this type.
    /// </summary>
    public static IList CreateGenericList(this Type t) {
        Type rt = typeof(List<>).MakeGenericType(t);

        return (IList)Activator.CreateInstance(rt)!;
    }

    /// <summary>
    /// Checks if this type implements generic IEnumerable or an array.
    /// </summary>
    /// <param name="t"></param>
    /// <param name="baseType"></param>
    /// <returns></returns>
    public static bool TryExtractIEnumerableType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type t, out Type? baseType) {
        if(typeof(byte[]) == t || typeof(string) == t || typeof(ReadOnlyMemory<char>) == t || typeof(ReadOnlyMemory<byte>) == t) {
            //it's a special case to avoid confusion between byte arrays and repeatable bytes
            baseType = null;
            return false;
        }

        TypeInfo ti = t.GetTypeInfo();
        Type[] args = ti.GenericTypeArguments;

        if(args.Length > 0) {
            //check derived interfaces
            foreach(Type interfaceType in ti.ImplementedInterfaces) {
                TypeInfo iti = interfaceType.GetTypeInfo();
                if(iti.IsGenericType && iti.GetGenericTypeDefinition() == typeof(IEnumerable<>)) {
                    baseType = iti.GenericTypeArguments[0];
                    return true;
                }
            }

            //check if this is an IEnumerable<>
            if(ti.IsGenericType && ti.GetGenericTypeDefinition() == typeof(IEnumerable<>)) {
                baseType = ti.GenericTypeArguments[0];
                return true;
            }
        }

        if(ti.IsArray) {
            baseType = ti.GetElementType();
            return true;
        }

        baseType = null;
        return false;
    }

    public static Type ExtractElementTypeFromEnumerableType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type t) {
        if(t.TryExtractIEnumerableType(out Type? iet)) {
            return iet!;
        }

        throw new ArgumentException($"type {t} is not single-element generic enumerable", nameof(t));
    }

    public static MethodInfo GetGenericListAddMethod(this Type listType) {
        Type elementType = listType.ExtractElementTypeFromEnumerableType();
        Type genericListType = typeof(List<>).MakeGenericType(elementType);
        MethodInfo? method = genericListType.GetMethod(nameof(IList.Add));
        return method ?? throw new InvalidOperationException("method not present");
    }

    public static bool IsGenericIDictionary([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type t) {
        return t.IsGenericType &&
            (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
            t.GetInterfaces().Any(x => x.IsGenericType && typeof(IDictionary<,>) == x.GetGenericTypeDefinition()));
    }

    public static bool IsGenericIList(this Type t) {
        return t.IsGenericType &&
            (t.GetGenericTypeDefinition() == typeof(IList<>) ||
            t.GetInterfaces().Any(x => x.IsGenericType && typeof(IList<>) == x.GetGenericTypeDefinition()));
    }

    public static bool TryExtractDictionaryType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] this Type t, out Type? keyType, out Type? valueType) {
        if(t.IsGenericIDictionary()) {
            TypeInfo ti = t.GetTypeInfo();
            keyType = ti.GenericTypeArguments[0];
            valueType = ti.GenericTypeArguments[1];
            return true;
        }

        keyType = valueType = null;
        return false;
    }

    public static bool TryExtractIListType(this Type t, out Type? elementType) {
        if(t.IsGenericIList()) {
            TypeInfo ti = t.GetTypeInfo();
            elementType = ti.GenericTypeArguments[0];
            return true;
        }
        elementType = null;
        return false;
    }

    /// <summary>
    /// Checks if this type is a generic <see cref="List{T}"/> (not just any <see cref="IEnumerable{T}"/> or <see cref="IList{T}"/>, but specifically <see cref="List{T}"/>).
    /// </summary>
    /// <param name="t"></param>
    /// <param name="elementType">Generic list's element type, if this is a generic list.</param>
    /// <returns></returns>
    public static bool IsGenericList(this Type t, out Type elementType) {
        bool isList = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);
        if(!isList) {
            elementType = typeof(void);
            return false;
        }

        TypeInfo ti = t.GetTypeInfo();
        elementType = ti.GenericTypeArguments[0];
        return true;
    }

    public static bool IsNullable(this IList list) {
        TypeInfo ti = list.GetType().GetTypeInfo();

        Type t = ti.GenericTypeArguments[0];
        Type? gt = t.GetTypeInfo().IsGenericType ? t.GetTypeInfo().GetGenericTypeDefinition() : null;

        return gt == typeof(Nullable<>) || t.GetTypeInfo().IsClass;
    }

    public static bool IsNullable(this PropertyInfo pi) {
        return pi.GetMethod?.CustomAttributes.Any(x => x.AttributeType.Name == "NullableContextAttribute") == true ||
            pi.DeclaringType?.CustomAttributes.Any(x => x.AttributeType.Name == "NullableContextAttribute" &&
                (byte)x.ConstructorArguments[0].Value! == 2) == true;
    }

    public static bool IsNullable(this Type t) {
        TypeInfo ti = t.GetTypeInfo();

        return
            ti.IsClass || ti.IsInterface ||
            (ti.IsGenericType && ti.GetGenericTypeDefinition() == typeof(Nullable<>));
    }

    public static bool IsSystemNullable(this Type t) {
        TypeInfo ti = t.GetTypeInfo();

        return ti.IsGenericType && ti.GetGenericTypeDefinition() == typeof(Nullable<>);
    }

    public static Type StripOffSystemNullable(this Type t) => IsSystemNullable(t) ? GetNonNullable(t) : t;

    public static Type GetNonNullable(this Type t) {
        TypeInfo ti = t.GetTypeInfo();

        if(ti.IsClass || ti.IsInterface) {
            return t;
        }

        return ti.GenericTypeArguments[0];
    }

    /// <summary>
    /// The nullable form of each value type a data field can have, so that no type is constructed at run time, which
    /// native AOT cannot do for a type it has not seen.
    /// </summary>
    private static readonly Dictionary<Type, Type> NullableOf = new() {
        [typeof(bool)] = typeof(bool?), [typeof(byte)] = typeof(byte?), [typeof(sbyte)] = typeof(sbyte?),
        [typeof(short)] = typeof(short?), [typeof(ushort)] = typeof(ushort?), [typeof(int)] = typeof(int?),
        [typeof(uint)] = typeof(uint?), [typeof(long)] = typeof(long?), [typeof(ulong)] = typeof(ulong?),
        [typeof(float)] = typeof(float?), [typeof(double)] = typeof(double?), [typeof(decimal)] = typeof(decimal?),
        [typeof(System.Numerics.BigInteger)] = typeof(System.Numerics.BigInteger?),
        [typeof(Parquet.Data.BigDecimal)] = typeof(Parquet.Data.BigDecimal?),
        [typeof(DateTime)] = typeof(DateTime?), [typeof(DateOnly)] = typeof(DateOnly?), [typeof(TimeOnly)] = typeof(TimeOnly?),
        [typeof(TimeSpan)] = typeof(TimeSpan?), [typeof(Guid)] = typeof(Guid?),
        [typeof(Parquet.File.Values.Primitives.Interval)] = typeof(Parquet.File.Values.Primitives.Interval?),
        [typeof(ReadOnlyMemory<byte>)] = typeof(ReadOnlyMemory<byte>?), [typeof(ReadOnlyMemory<char>)] = typeof(ReadOnlyMemory<char>?),
    };

    public static Type GetNullable(this Type t) {
        TypeInfo ti = t.GetTypeInfo();

        if(ti.IsClass || ti.IsInterface || (ti.IsGenericType && ti.GetGenericTypeDefinition() == typeof(Nullable<>))) {
            return t;
        }

        if(NullableOf.TryGetValue(t, out Type? nullable)) {
            return nullable;
        }

        // another value type (an enum): constructed where the runtime can; under native AOT, the type itself
        return System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? ConstructNullable(t) : t;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Called only when RuntimeFeature.IsDynamicCodeSupported; net8.0's analyzer does not treat that as a guard")]
    private static Type ConstructNullable(Type t) => typeof(Nullable<>).MakeGenericType(t);

    public static bool IsSimple(this Type t) {
        if(t == null)
            return true;

        return
            t == typeof(bool) ||
            t == typeof(byte) ||
            t == typeof(sbyte) ||
            t == typeof(char) ||
            t == typeof(decimal) ||
            t == typeof(double) ||
            t == typeof(float) ||
            t == typeof(int) ||
            t == typeof(uint) ||
            t == typeof(long) ||
            t == typeof(ulong) ||
            t == typeof(short) ||
            t == typeof(ushort) ||
            t == typeof(TimeSpan) ||
            t == typeof(DateTime) ||
            t == typeof(Guid) ||
            t == typeof(string);
    }

    public static bool IsAssignableToGenericType(this Type givenType, Type genericType) {
        Type[] interfaceTypes = givenType.GetInterfaces();

        foreach(Type it in interfaceTypes) {
            if(it.IsGenericType && it.GetGenericTypeDefinition() == genericType)
                return true;
        }

        if(givenType.IsGenericType && givenType.GetGenericTypeDefinition() == genericType)
            return true;

        Type? baseType = givenType.BaseType;
        if(baseType == null)
            return false;

        return IsAssignableToGenericType(baseType, genericType);
    }

    /// <summary>
    /// Reinterprets this span as a span of a different struct element type without copying.
    /// </summary>
    public static Span<TTo> AsSpan<TFrom, TTo>(this Span<TFrom> span)
        where TTo : struct
        where TFrom : struct {
        ref TFrom sourceRef = ref MemoryMarshal.GetReference(span);
        ref TTo targetRef = ref Unsafe.As<TFrom, TTo>(ref sourceRef);
        return MemoryMarshal.CreateSpan(ref targetRef, span.Length);
    }

    public static ReadOnlySpan<TTo> AsSpan<TFrom, TTo>(this ReadOnlySpan<TFrom> span)
        where TTo : struct
        where TFrom : struct {
        ref TFrom sourceRef = ref MemoryMarshal.GetReference(span);
        ref TTo targetRef = ref Unsafe.As<TFrom, TTo>(ref sourceRef);
        return MemoryMarshal.CreateReadOnlySpan(ref targetRef, span.Length);
    }
}