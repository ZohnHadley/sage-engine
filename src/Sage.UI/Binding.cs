#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Sage.UI;

// Bindings read a view-model by path ("player.health", "items", "." for the object itself), and they are
// read every frame, so reading must not allocate (02 §4.6). Reflection happens once per (type, path):
// the path's members are found and compiled into one delegate that returns a UiValue — a struct, so a
// number is never boxed — with a null anywhere along the path reading as nothing. What a binding shows
// is worked out again only when the value it read differs from the last one.

// One value read from a view-model: a number (and how to write it), a flag, a string, or an object.
internal enum UiValueKind : byte { None, Text, Integer, Single, Double, Flag, Enum, Object }

internal readonly struct UiValue
{
    public readonly UiValueKind Kind;
    public readonly double Number;
    public readonly object? Ref;   // the string, the object, or an enum's type

    public UiValue(UiValueKind kind, double number, object? reference)
    {
        Kind = kind;
        Number = number;
        Ref = reference;
    }

    public static UiValue Text(string? text) => text == null ? default : new(UiValueKind.Text, 0, text);
    public static UiValue Integer(double value) => new(UiValueKind.Integer, value, null);
    public static UiValue Single(double value) => new(UiValueKind.Single, value, null);
    public static UiValue Double(double value) => new(UiValueKind.Double, value, null);
    public static UiValue Flag(bool value) => new(UiValueKind.Flag, value ? 1 : 0, null);
    public static UiValue Enum(double value, Type type) => new(UiValueKind.Enum, value, type);
    public static UiValue Object(object? value) => value == null ? default : new(UiValueKind.Object, 0, value);

    // Truthiness, for `visible` and `enabled`: a flag, a number not 0, a string not empty, an object.
    public bool IsTrue => Kind switch
    {
        UiValueKind.None => false,
        UiValueKind.Text => ((string)Ref!).Length > 0,
        UiValueKind.Object => true,
        _ => Number != 0,
    };

    public bool Same(in UiValue other) =>
        Kind == other.Kind && Number.Equals(other.Number) &&
        (Kind == UiValueKind.Text ? string.Equals((string?)Ref, (string?)other.Ref, StringComparison.Ordinal) : ReferenceEquals(Ref, other.Ref));

    public float AsFloat => Kind is UiValueKind.Text or UiValueKind.Object or UiValueKind.None ? 0f : (float)Number;

    public string? AsString => Kind == UiValueKind.Text ? (string)Ref! : null;

    // As text to show. Allocates for a number: called when the value changed, not every frame.
    public override string ToString() => Kind switch
    {
        UiValueKind.None => "",
        UiValueKind.Text => (string)Ref!,
        UiValueKind.Integer => ((long)Number).ToString(CultureInfo.InvariantCulture),
        UiValueKind.Single => ((float)Number).ToString(CultureInfo.InvariantCulture),
        UiValueKind.Double => Number.ToString(CultureInfo.InvariantCulture),
        UiValueKind.Flag => Number != 0 ? "true" : "false",
        UiValueKind.Enum => System.Enum.GetName((Type)Ref!, System.Enum.ToObject((Type)Ref!, (long)Number)) ?? ((long)Number).ToString(CultureInfo.InvariantCulture),
        _ => Ref?.ToString() ?? "",
    };

    public void AppendTo(StringBuilder builder)
    {
        switch (Kind)
        {
            case UiValueKind.None: break;
            case UiValueKind.Text: builder.Append((string)Ref!); break;
            case UiValueKind.Integer: builder.Append(CultureInfo.InvariantCulture, $"{(long)Number}"); break;
            case UiValueKind.Single: builder.Append(CultureInfo.InvariantCulture, $"{(float)Number}"); break;
            case UiValueKind.Double: builder.Append(CultureInfo.InvariantCulture, $"{Number}"); break;
            default: builder.Append(ToString()); break;
        }
    }
}

// Paths through a type's public fields and properties, names matched ignoring case (content is written
// in camel case, C# in Pascal case).
internal static class BindingPaths
{
    private static readonly ConcurrentDictionary<(Type, string), Func<object, UiValue>> Compiled = new();

    public static bool IsSelf(string path) => path.Length == 0 || path == ".";

    // The members `path` walks from `type`, and the type it ends at; false with the reason when a step is
    // not there (with the nearest name).
    public static bool TryWalk(Type type, string path, out List<MemberInfo> members, out Type leaf, out string? problem)
    {
        members = new List<MemberInfo>();
        leaf = type;
        problem = null;
        if (IsSelf(path)) return true;
        foreach (string step in path.Split('.'))
        {
            if (step.Length == 0)
            {
                problem = $"'{path}' has an empty step";
                return false;
            }
            var member = Member(leaf, step);
            if (member == null)
            {
                // Suggested as content writes names: camel case.
                var names = Readable(leaf).Select(m => char.ToLowerInvariant(m.Name[0]) + m.Name[1..]).ToList();
                problem = $"{Describe(leaf)} has no '{step}'" + Spelling.Suggest(step, names);
                return false;
            }
            members.Add(member);
            leaf = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
        }
        return true;
    }

    // What a value of `type` can be bound to: a number, a flag, text or an object (a list, a row).
    public static bool IsReadable(Type type) => Kind(type) != UiValueKind.None;

    // The element type of a list, for a row template's paths: T of T[], List<T>, IReadOnlyList<T>, ...;
    // object when it cannot be told (a plain IList).
    public static Type ElementType(Type list)
    {
        if (list.IsArray) return list.GetElementType()!;
        foreach (var candidate in new[] { list }.Concat(list.GetInterfaces()))
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() is var d && (d == typeof(IList<>) || d == typeof(IReadOnlyList<>) || d == typeof(IEnumerable<>)))
                return candidate.GetGenericArguments()[0];
        return typeof(object);
    }

    // The reader for `path` on objects of exactly `type`. Compiled once per (type, path) for the process.
    public static Func<object, UiValue> Reader(Type type, string path) =>
        Compiled.GetOrAdd((type, path), static key => Compile(key.Item1, key.Item2));

    private static Func<object, UiValue> Compile(Type type, string path)
    {
        if (!TryWalk(type, path, out var members, out var leaf, out string? problem))
        {
            Log.Warn(LogCat.UI, $"binding '{path}': {problem}; it reads nothing");
            return static _ => default;
        }
        var kind = Kind(leaf);
        if (kind == UiValueKind.None)
        {
            Log.Warn(LogCat.UI, $"binding '{path}': {Describe(leaf)} is not a number, flag, text or object; it reads nothing");
            return static _ => default;
        }

        var o = Expression.Parameter(typeof(object), "o");
        var result = Expression.Label(typeof(UiValue), "result");
        var variables = new List<ParameterExpression>();
        var body = new List<Expression>();
        Expression current = type.IsValueType ? Expression.Unbox(o, type) : Expression.Convert(o, type);
        foreach (var member in members)
        {
            if (!current.Type.IsValueType)
            {
                var v = Expression.Variable(current.Type);
                variables.Add(v);
                body.Add(Expression.Assign(v, current));
                body.Add(Expression.IfThen(Expression.ReferenceEqual(v, Expression.Constant(null, current.Type)),
                                           Expression.Return(result, Expression.Default(typeof(UiValue)))));
                current = v;
            }
            current = Expression.MakeMemberAccess(current, member);
        }
        body.Add(Expression.Label(result, Wrap(current, kind)));
        return Expression.Lambda<Func<object, UiValue>>(Expression.Block(typeof(UiValue), variables, body), o).Compile();
    }

    private static Expression Wrap(Expression value, UiValueKind kind)
    {
        var type = value.Type;
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            var v = Expression.Variable(type);
            return Expression.Block(typeof(UiValue), new[] { v },
                Expression.Assign(v, value),
                Expression.Condition(Expression.Property(v, "HasValue"),
                                     Wrap(Expression.Property(v, "Value"), kind),
                                     Expression.Default(typeof(UiValue))));
        }
        Expression AsDouble(Expression e) => Expression.Convert(e.Type.IsEnum ? Expression.Convert(e, Enum.GetUnderlyingType(e.Type)) : e, typeof(double));
        return kind switch
        {
            UiValueKind.Text => Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.Text))!, value),
            UiValueKind.Flag => Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.Flag))!, value),
            UiValueKind.Integer => Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.Integer))!, AsDouble(value)),
            UiValueKind.Single => Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.Single))!, AsDouble(value)),
            UiValueKind.Double => Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.Double))!, AsDouble(value)),
            UiValueKind.Enum => Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.Enum))!, AsDouble(value), Expression.Constant(type, typeof(Type))),
            _ => Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.Object))!, Expression.Convert(value, typeof(object))),
        };
    }

    private static UiValueKind Kind(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return UiValueKind.Text;
        if (type == typeof(bool)) return UiValueKind.Flag;
        if (type.IsEnum) return UiValueKind.Enum;
        if (type == typeof(float)) return UiValueKind.Single;
        if (type == typeof(double) || type == typeof(decimal)) return UiValueKind.Double;
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
            || type == typeof(sbyte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong)) return UiValueKind.Integer;
        // An object — a list of rows, a nested view-model, whatever Widget.Data should hold. A struct would
        // be boxed on every read, so only classes (and interfaces) are.
        return type.IsValueType ? UiValueKind.None : UiValueKind.Object;
    }

    private static MemberInfo? Member(Type type, string name) =>
        Readable(type).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<MemberInfo> Readable(Type type)
    {
        var all = type.IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : new[] { type };
        foreach (var t in all)
        {
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) yield return field;
            foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.CanRead && property.GetIndexParameters().Length == 0 && property.GetMethod!.IsPublic) yield return property;
        }
    }

    private static string Describe(Type type) => type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] + "<…>" : type.Name;
}

// One binding's reader: compiled for the type of object it last read, and again only if that changes.
internal sealed class BindingReader
{
    private Type? _type;
    private Func<object, UiValue>? _read;

    public BindingReader(string path) { Path = path; }

    public string Path { get; }

    public UiValue Read(object? source)
    {
        if (source == null) return default;
        var type = source.GetType();
        if (type != _type)
        {
            _read = BindingPaths.Reader(type, Path);
            _type = type;
        }
        return _read!(source);
    }
}
