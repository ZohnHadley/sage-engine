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

    // For a binding written back (issue #340): text as it is, anything else as it shows; a number rounded.
    public static string ToText(UiValue value) => value.Kind == UiValueKind.Text ? (string)value.Ref! : value.ToString();
    public static long ToWhole(UiValue value) => (long)Math.Round(value.Number);

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
// in camel case, C# in Pascal case). A step may be indexed (issue #347): `items[0]`, `stats[health]`,
// `grid[1][2]`, or `[0]` on the object itself — a list or an array by position, a dictionary by key, or
// any other type through its own one-argument indexer (`this[string]`, `this[int]`). A position past the
// end, or a key that is not there, reads as nothing, as a null along the path does.
//
// A path may also start from another scope than the one it is read in (issue #347): `$root.` is the
// view-model even inside a row, and `$parent.` the scope around this one — the view-model around a
// list's rows, the row around a nested list's — and chains (`$parent.$parent.`). BindingScope splits
// that off; the rest is walked as above.
internal enum PathStepKind : byte { Member, Array, List, Dictionary, Indexer }

internal sealed class PathStep
{
    public PathStepKind Kind;
    public MemberInfo? Member;       // Member
    public PropertyInfo? Indexer;    // Indexer, and List's/Dictionary's Item on Container
    public Type? Container;          // List: IList<T> or IReadOnlyList<T>; Dictionary: IDictionary<K,V> or IReadOnlyDictionary<K,V>
    public object? Key;              // the index (int) or the key, as the container's key type
    public Type Result = typeof(object);
    public string Text = "";         // as written, for messages
}

internal static class BindingScope
{
    public const string Root = "$root", Parent = "$parent";

    // `up`: 0 for the scope the path is read in, n for n scopes out, -1 for the view-model.
    public static string Split(string path, out int up)
    {
        up = 0;
        string rest = path;
        while (true)
        {
            if (Starts(rest, Root)) { up = -1; rest = After(rest, Root.Length); continue; }
            if (Starts(rest, Parent)) { if (up >= 0) up++; rest = After(rest, Parent.Length); continue; }
            return rest;
        }
    }

    private static bool Starts(string path, string scope) =>
        path.StartsWith(scope, StringComparison.Ordinal) && (path.Length == scope.Length || path[scope.Length] is '.' or '[');

    private static string After(string path, int length) =>
        path.Length == length ? "" : path[length] == '.' ? path[(length + 1)..] : path[length..];
}

internal static class BindingPaths
{
    private static readonly ConcurrentDictionary<(Type, string), Func<object, UiValue>> Compiled = new();
    private static readonly ConcurrentDictionary<(Type, string), Func<object, object?>> Boxed = new();

    public static bool IsSelf(string path) => path.Length == 0 || path == ".";

    // A path's steps as written: a name, then any number of [index]es; a step may be only indexes.
    // False with the reason when it is not a path.
    public static bool TryParse(string path, out List<(string Name, List<string> Indexes)> steps, out string? problem)
    {
        steps = new();
        problem = null;
        if (IsSelf(path)) return true;
        int i = 0;
        while (true)
        {
            int start = i;
            while (i < path.Length && path[i] is not ('.' or '[' or ']')) i++;
            string name = path[start..i].Trim();
            var indexes = new List<string>();
            while (i < path.Length && path[i] == '[')
            {
                int close = path.IndexOf(']', i + 1);
                if (close < 0) { problem = $"'{path}' has a '[' with no ']'"; return false; }
                string index = path[(i + 1)..close].Trim();
                if (index.Length >= 2 && index[0] is '\'' or '"' && index[^1] == index[0]) index = index[1..^1];
                if (index.Length == 0) { problem = $"'{path}' has an empty []"; return false; }
                indexes.Add(index);
                i = close + 1;
            }
            if (name.Length == 0 && indexes.Count == 0) { problem = $"'{path}' has an empty step"; return false; }
            steps.Add((name, indexes));
            if (i == path.Length) return true;
            if (path[i] != '.') { problem = $"'{path}' has '{path[i]}' where a '.' or the end should be"; return false; }
            i++;
            if (i == path.Length) { problem = $"'{path}' has an empty step"; return false; }
        }
    }

    // The steps `path` walks from `type`, and the type it ends at; false with the reason when a step is
    // not there (with the nearest name) or cannot be indexed.
    public static bool TryWalk(Type type, string path, out List<PathStep> steps, out Type leaf, out string? problem)
    {
        steps = new List<PathStep>();
        leaf = type;
        if (!TryParse(path, out var written, out problem)) return false;
        foreach (var (name, indexes) in written)
        {
            if (name.Length > 0)
            {
                var member = Member(leaf, name);
                if (member == null)
                {
                    // Suggested as content writes names: camel case.
                    var names = Readable(leaf).Select(m => char.ToLowerInvariant(m.Name[0]) + m.Name[1..]).ToList();
                    problem = $"{Describe(leaf)} has no '{name}'" + Spelling.Suggest(name, names);
                    return false;
                }
                leaf = member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;
                steps.Add(new PathStep { Kind = PathStepKind.Member, Member = member, Result = leaf, Text = name });
            }
            foreach (string index in indexes)
            {
                if (!TryIndex(leaf, index, out var step, out problem)) return false;
                steps.Add(step);
                leaf = step.Result;
            }
        }
        return true;
    }

    private static bool TryIndex(Type type, string index, out PathStep step, out string? problem)
    {
        step = new PathStep { Text = $"[{index}]" };
        problem = null;
        if ((Generic(type, typeof(IDictionary<,>)) ?? Generic(type, typeof(IReadOnlyDictionary<,>))) is { } dictionary)
        {
            var args = dictionary.GetGenericArguments();
            if (!TryKey(index, args[0], out step.Key)) { problem = $"'{index}' is not a key of {Describe(type)}: it is keyed by {args[0].Name}"; return false; }
            step.Kind = PathStepKind.Dictionary;
            step.Container = dictionary;
            step.Result = args[1];
            return true;
        }
        bool isList = type.IsArray && type.GetArrayRank() == 1 || (Generic(type, typeof(IReadOnlyList<>)) ?? Generic(type, typeof(IList<>))) != null;
        if (isList)
        {
            if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int position))
            {
                problem = $"'{index}' is not a position in {Describe(type)}: a list is indexed from 0";
                return false;
            }
            step.Key = position;
            if (type.IsArray)
            {
                step.Kind = PathStepKind.Array;
                step.Result = type.GetElementType()!;
            }
            else
            {
                step.Kind = PathStepKind.List;
                step.Container = Generic(type, typeof(IList<>)) ?? Generic(type, typeof(IReadOnlyList<>));
                step.Result = step.Container!.GetGenericArguments()[0];
            }
            return true;
        }
        // A type's own indexer: by string first, then by a number.
        var indexers = (type.IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : new[] { type })
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.GetIndexParameters().Length == 1 && p.GetMethod is { IsPublic: true })
            .OrderBy(p => p.GetIndexParameters()[0].ParameterType == typeof(string) ? 0 : 1).ToList();
        foreach (var indexer in indexers)
            if (TryKey(index, indexer.GetIndexParameters()[0].ParameterType, out step.Key))
            {
                step.Kind = PathStepKind.Indexer;
                step.Indexer = indexer;
                step.Result = indexer.PropertyType;
                return true;
            }
        problem = indexers.Count == 0 ? $"{Describe(type)} cannot be indexed: '[{index}]' needs a list, an array, a dictionary or an indexer"
                                      : $"'{index}' is not an index of {Describe(type)}";
        return false;
    }

    // An index as written, as a value of the key type: text, a whole number, or an enum's name.
    private static bool TryKey(string text, Type type, out object? key)
    {
        key = null;
        if (type == typeof(string) || type == typeof(object)) { key = text; return true; }
        if (type.IsEnum) return Enum.TryParse(type, text, ignoreCase: true, out key);
        if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) { key = i; return true; }
        if (type == typeof(long) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) { key = l; return true; }
        return false;
    }

    // The closed generic interface `definition` that `type` is or implements, or null.
    private static Type? Generic(Type type, Type definition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == definition) return type;
        foreach (var candidate in type.GetInterfaces())
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == definition) return candidate;
        return null;
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

    private static readonly ConcurrentDictionary<(Type, string), Action<object, UiValue>?> Writers = new();

    // Whether `path` on `type` can be written (issue #340): it ends at a settable field or property of a
    // number, flag, enum or string — or an index of a list, an array, a dictionary or an indexer that
    // has a setter (issue #347) — and every step before it is a class (a struct read along the way is a
    // copy, and writing into it would be lost).
    public static bool IsWritable(Type type, string path, out string? problem)
    {
        problem = null;
        if (IsSelf(path)) { problem = "'.' is the object itself, which cannot be replaced"; return false; }
        if (!TryWalk(type, path, out var steps, out var leaf, out problem)) return false;
        var owner = type;
        for (int i = 0; i < steps.Count; i++)
        {
            if (owner.IsValueType) { problem = $"{Describe(owner)} is a struct: a value written into it would be lost"; return false; }
            owner = steps[i].Result;
        }
        var last = steps[^1];
        bool settable = last.Kind switch
        {
            PathStepKind.Member => last.Member is FieldInfo field ? !field.IsInitOnly && !field.IsLiteral : ((PropertyInfo)last.Member!).SetMethod is { IsPublic: true },
            PathStepKind.Array => true,
            PathStepKind.List => last.Container!.GetGenericTypeDefinition() == typeof(IList<>),
            PathStepKind.Dictionary => last.Container!.GetGenericTypeDefinition() == typeof(IDictionary<,>),
            _ => last.Indexer!.SetMethod is { IsPublic: true },
        };
        if (!settable) { problem = $"'{last.Member?.Name ?? last.Text}' is read-only"; return false; }
        if (Kind(leaf) is UiValueKind.None or UiValueKind.Object) { problem = $"{Describe(leaf)} is not a number, flag or text"; return false; }
        return true;
    }

    // What writes a value to `path` on objects of exactly `type`, or null when it cannot be (said once).
    public static Action<object, UiValue>? Writer(Type type, string path) =>
        Writers.GetOrAdd((type, path), static key => CompileWriter(key.Item1, key.Item2));

    private static Action<object, UiValue>? CompileWriter(Type type, string path)
    {
        if (!IsWritable(type, path, out string? problem))
        {
            Log.Warn(LogCat.UI, $"binding '{path}': {problem}; what the player changes is not written back");
            return null;
        }
        TryWalk(type, path, out var steps, out var leaf, out _);
        var o = Expression.Parameter(typeof(object), "o");
        var value = Expression.Parameter(typeof(UiValue), "value");
        var done = Expression.Label("done");
        var fail = Expression.Return(done);
        var variables = new List<ParameterExpression>();
        var body = new List<Expression>();
        Expression current = Expression.Convert(o, type);
        for (int i = 0; i < steps.Count - 1; i++) current = Next(current, steps[i], variables, body, fail);
        var holder = Expression.Variable(current.Type);
        variables.Add(holder);
        body.Add(Expression.Assign(holder, current));
        body.Add(Expression.IfThen(Expression.ReferenceEqual(holder, Expression.Constant(null, current.Type)), fail));
        body.Add(Expression.Assign(Target(holder, steps[^1], body, fail), Unwrap(value, leaf)));
        body.Add(Expression.Label(done));
        return Expression.Lambda<Action<object, UiValue>>(Expression.Block(variables, body), o, value).Compile();
    }

    // The place the last step names on `holder`, to be assigned: a member, or an index (in range).
    private static Expression Target(ParameterExpression holder, PathStep step, List<Expression> body, Expression fail)
    {
        switch (step.Kind)
        {
            case PathStepKind.Member:
                return Expression.MakeMemberAccess(holder, step.Member!);
            case PathStepKind.Array:
                body.Add(Expression.IfThen(Expression.GreaterThanOrEqual(Expression.Constant(step.Key), Expression.ArrayLength(holder)), fail));
                return Expression.ArrayAccess(holder, Expression.Constant(step.Key));
            case PathStepKind.List:
            {
                var element = step.Result;
                var list = Expression.Convert(holder, step.Container!);
                var count = Expression.Property(Expression.Convert(holder, typeof(ICollection<>).MakeGenericType(element)), "Count");
                body.Add(Expression.IfThen(Expression.GreaterThanOrEqual(Expression.Constant(step.Key), count), fail));
                return Expression.MakeIndex(list, step.Container!.GetProperty("Item"), new[] { Expression.Constant(step.Key) });
            }
            case PathStepKind.Dictionary:
                return Expression.MakeIndex(Expression.Convert(holder, step.Container!), step.Container!.GetProperty("Item"),
                                            new[] { Expression.Constant(step.Key, step.Container!.GetGenericArguments()[0]) });
            default:
                return Expression.MakeIndex(Receiver(holder, step.Indexer!), step.Indexer!, new[] { Expression.Constant(step.Key, step.Indexer!.GetIndexParameters()[0].ParameterType) });
        }
    }

    // `value` as the indexer's declaring type wants it (an interface's indexer on a class).
    private static Expression Receiver(Expression value, PropertyInfo indexer) =>
        indexer.DeclaringType is { IsInterface: true } declaring && declaring != value.Type ? Expression.Convert(value, declaring) : value;

    // One step of a read: `current` held in a variable (null: fail), then the member or the index of it
    // (out of range or not there: fail).
    private static Expression Next(Expression current, PathStep step, List<ParameterExpression> variables, List<Expression> body, Expression fail)
    {
        if (!current.Type.IsValueType || step.Kind != PathStepKind.Member)
        {
            var v = Expression.Variable(current.Type);
            variables.Add(v);
            body.Add(Expression.Assign(v, current));
            if (!current.Type.IsValueType) body.Add(Expression.IfThen(Expression.ReferenceEqual(v, Expression.Constant(null, current.Type)), fail));
            current = v;
        }
        switch (step.Kind)
        {
            case PathStepKind.Member:
                return Expression.MakeMemberAccess(current, step.Member!);
            case PathStepKind.Array:
                body.Add(Expression.IfThen(Expression.GreaterThanOrEqual(Expression.Constant(step.Key), Expression.ArrayLength(current)), fail));
                return Expression.ArrayIndex(current, Expression.Constant(step.Key));
            case PathStepKind.List:
            {
                var counted = step.Container!.GetGenericTypeDefinition() == typeof(IList<>) ? typeof(ICollection<>) : typeof(IReadOnlyCollection<>);
                var count = Expression.Property(Expression.Convert(current, counted.MakeGenericType(step.Result)), "Count");
                body.Add(Expression.IfThen(Expression.GreaterThanOrEqual(Expression.Constant(step.Key), count), fail));
                return Expression.MakeIndex(Expression.Convert(current, step.Container!), step.Container!.GetProperty("Item"), new[] { Expression.Constant(step.Key) });
            }
            case PathStepKind.Dictionary:
            {
                var found = Expression.Variable(step.Result);
                variables.Add(found);
                var key = Expression.Constant(step.Key, step.Container!.GetGenericArguments()[0]);
                body.Add(Expression.IfThen(Expression.Not(Expression.Call(Expression.Convert(current, step.Container!), step.Container!.GetMethod("TryGetValue")!, key, found)), fail));
                return found;
            }
            default:
                return Expression.MakeIndex(Receiver(current, step.Indexer!), step.Indexer!, new[] { Expression.Constant(step.Key, step.Indexer!.GetIndexParameters()[0].ParameterType) });
        }
    }

    // A UiValue as a value of `type`: a number rounded to a whole one for an integer or an enum.
    private static Expression Unwrap(Expression value, Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        Expression result;
        if (underlying == typeof(string)) result = Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.ToText))!, value);
        else if (underlying == typeof(bool)) result = Expression.Property(value, nameof(UiValue.IsTrue));
        else if (underlying.IsEnum)
            result = Expression.Convert(Expression.Convert(Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.ToWhole))!, value), Enum.GetUnderlyingType(underlying)), underlying);
        else if (underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal))
            result = Expression.Convert(Expression.Field(value, nameof(UiValue.Number)), underlying);
        else result = Expression.Convert(Expression.Call(typeof(UiValue).GetMethod(nameof(UiValue.ToWhole))!, value), underlying);
        return underlying == type ? result : Expression.Convert(result, type);
    }

    // The reader for `path` on objects of exactly `type`. Compiled once per (type, path) for the process.
    public static Func<object, UiValue> Reader(Type type, string path) =>
        Compiled.GetOrAdd((type, path), static key => Compile(key.Item1, key.Item2));

    // What `path` leads to on objects of exactly `type`, whatever it is, boxed: for a command's
    // placeholders (UiCommandAction), read on a press rather than every frame. Null when it leads nowhere.
    public static Func<object, object?> ObjectReader(Type type, string path) =>
        Boxed.GetOrAdd((type, path), static key =>
        {
            if (!TryWalk(key.Item1, key.Item2, out var steps, out _, out string? problem))
            {
                Log.Warn(LogCat.UI, $"'{key.Item2}': {problem}; it reads nothing");
                return static _ => null;
            }
            return Build<object?>(key.Item1, steps, static current => Expression.Convert(current, typeof(object)));
        });

    private static Func<object, UiValue> Compile(Type type, string path)
    {
        if (!TryWalk(type, path, out var steps, out var leaf, out string? problem))
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
        return Build<UiValue>(type, steps, current => Wrap(current, kind));
    }

    // Walks `steps` from an object of `type`, and returns `wrap` of where they end; default(T) when a
    // step finds nothing.
    private static Func<object, T> Build<T>(Type type, List<PathStep> steps, Func<Expression, Expression> wrap)
    {
        var o = Expression.Parameter(typeof(object), "o");
        var result = Expression.Label(typeof(T), "result");
        var fail = Expression.Return(result, Expression.Default(typeof(T)));
        var variables = new List<ParameterExpression>();
        var body = new List<Expression>();
        Expression current = type.IsValueType ? Expression.Unbox(o, type) : Expression.Convert(o, type);
        foreach (var step in steps) current = Next(current, step, variables, body, fail);
        body.Add(Expression.Label(result, wrap(current)));
        return Expression.Lambda<Func<object, T>>(Expression.Block(typeof(T), variables, body), o).Compile();
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

// The scopes around the one being read (issue #347): the view-model, then each row (or `scope`) a
// refresh has gone into, innermost last. One per view, reused, so a refresh allocates nothing once it has
// been as deep as it goes.
internal sealed class UiScopes
{
    private object?[] _stack = new object?[4];

    public int Count { get; private set; }

    public void Clear()
    {
        Array.Clear(_stack, 0, Count);
        Count = 0;
    }

    public void Push(object? scope)
    {
        if (Count == _stack.Length) Array.Resize(ref _stack, Count * 2);
        _stack[Count++] = scope;
    }

    public void Pop() => _stack[--Count] = null;

    // What a path `up` scopes out reads from, `source` being the one it is in: -1 the outermost (the
    // view-model), n the nth one out; null when there are not that many.
    public object? Resolve(object? source, int up)
    {
        if (up == 0) return source;
        if (up < 0) return Count == 0 ? source : _stack[0];
        return up <= Count ? _stack[Count - up] : null;
    }
}

// One binding's reader: compiled for the type of object it last read, and again only if that changes.
// A path naming another scope (`$root.`, `$parent.`, issue #347) reads from it when given the scopes.
internal sealed class BindingReader
{
    private readonly string _rest;
    private readonly int _up;
    private Type? _type;
    private Func<object, UiValue>? _read;

    public BindingReader(string path)
    {
        Path = path;
        _rest = BindingScope.Split(path, out _up);
    }

    public string Path { get; }

    // What it last read from: where a value the player changes is written (issue #340).
    public object? LastSource { get; private set; }

    public UiValue Read(object? source, UiScopes? scopes) => Read(scopes == null ? (_up == 0 ? source : null) : scopes.Resolve(source, _up));

    public UiValue Read(object? source)
    {
        LastSource = source;
        if (source == null) return default;
        var type = source.GetType();
        if (type != _type)
        {
            _read = BindingPaths.Reader(type, _rest);
            _type = type;
        }
        return _read!(source);
    }

    private Type? _writeType;
    private Action<object, UiValue>? _write;

    // Writes `value` to the path on what it last read from (the player changed a widget, issue #340);
    // nothing when the path cannot be written, which is said once.
    public void Write(UiValue value)
    {
        if (LastSource is not { } target) return;
        var type = target.GetType();
        if (type != _writeType)
        {
            _write = BindingPaths.Writer(type, _rest);
            _writeType = type;
        }
        _write?.Invoke(target, value);
    }
}
