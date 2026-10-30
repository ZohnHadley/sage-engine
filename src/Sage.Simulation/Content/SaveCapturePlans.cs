#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Simulation;

// A save converter (SaveSystem.AddConverter) whose values can be written off the tick (issue #602). A plugin's
// converter may read the world (gameplay's attributes and tags are written by name), so a component it writes
// used to be serialised on the tick, entity by entity: 10k creatures with attributes cost an autosave 80 ms
// and more. A converter that implements this is asked instead for a copy of each value on the tick, and for
// a converter that writes those copies reading nothing of the world, which the save's writer uses on its
// thread. A converter that does not implement it is left as it was: what it writes is serialised on the tick.
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public interface ISaveCapture
{
    // On the tick, for each value of the converted type a save holds (never null): a copy that nothing the game
    // does afterwards changes, or the value itself when nothing can. Not asked for a struct with no references.
    object Capture(object value);

    // On the tick, once a save: a converter that writes the copies as this one would write the values now,
    // reading nothing of the world (what it needs of it, copied). Only its Write is called, on the writer.
    JsonConverter ForWriter();
}

#pragma warning disable SAGE0131 // the save system's own capture: ISaveCapture is experimental for plugins, not for itself

// How a component type that is not plain (SaveSerializer.CanDefer) is captured for the writer (issue #602):
// its column copied, as a plain one is, and then, row by row, each thing in it the game could change under
// the writer replaced with a copy — a list, an array, a plugin's value (ISaveCapture.Capture). An `Entity` is
// a handle, copied with the column; the writer's entity converter turns it into the persistent id the world
// gave it on the tick (SaveJson.ForWriter). What cannot be captured — a dictionary, a class with no
// ISaveCapture converter, a converter of a plugin's that does not implement it, a type that holds itself —
// is serialised on the tick, as before.
internal sealed class CapturePlan
{
    private CapturePlan(Action<Array>? fix, bool entities)
    {
        Fix = fix;
        Entities = entities;
    }

    // Replaces, in a copied column, what the game could change with copies; null: nothing needs it.
    public Action<Array>? Fix { get; }

    // Something in it is an entity: the writer needs the entities' persistent ids.
    public bool Entities { get; }

    // Plans by world dialect (the plugin converters are the dialect's), then by type; null: not capturable.
    private static readonly ConditionalWeakTable<JsonSerializerOptions, Dictionary<Type, CapturePlan?>> Plans = new();

    // Asked on the tick, for a type CanDefer said no to; made once per dialect and type.
    public static CapturePlan? For(Type type, JsonSerializerOptions json)
    {
        if (type.IsCollectible) return null;   // a code mod's (#396): nothing compiled for it is kept
        var plans = Plans.GetOrCreateValue(json);
        lock (plans)
        {
            if (plans.TryGetValue(type, out var known)) return known;
            var builder = new Builder(json);
            CapturePlan? plan = null;
            if (builder.TryCopy(type, out var copy))
                plan = new CapturePlan(copy == null ? null : FixColumn(type, copy), builder.Entities);
            plans[type] = plan;
            return plan;
        }
    }

    private static Action<Array> FixColumn(Type type, Func<Expression, Expression> copy)
    {
        var value = Expression.Parameter(type, "value");
        var fix = Expression.Lambda(typeof(Func<,>).MakeGenericType(type, type), copy(value), value).Compile();
        var method = typeof(CapturePlan).GetMethod(nameof(FixRows), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(type);
        return (Action<Array>)method.Invoke(null, new object[] { fix })!;
    }

    private static Action<Array> FixRows<T>(Func<T, T> fix) => column =>
    {
        var rows = (T[])column;
        for (int i = 0; i < rows.Length; i++) rows[i] = fix(rows[i]);
    };

    private static List<T>? CopyList<T>(List<T>? list, Func<T, T>? fix)
    {
        if (list == null) return null;
        if (fix == null) return new List<T>(list);
        var copy = new List<T>(list.Count);
        foreach (var item in list) copy.Add(fix(item));
        return copy;
    }

    private static T[]? CopyArray<T>(T[]? array, Func<T, T>? fix)
    {
        if (array == null) return null;
        var copy = (T[])array.Clone();
        if (fix != null)
            for (int i = 0; i < copy.Length; i++) copy[i] = fix(copy[i]);
        return copy;
    }

    // Works out, for a type, whether it can be captured and the expression that copies a value of it (null:
    // the value is its own copy).
    private sealed class Builder
    {
        private readonly JsonSerializerOptions _json;
        private readonly HashSet<Type> _seen = new();

        public Builder(JsonSerializerOptions json) => _json = json;

        public bool Entities { get; private set; }

        public bool TryCopy(Type type, out Func<Expression, Expression>? copy)
        {
            copy = null;
            if (SaveSerializer.IsPlain(type, _json)) return true;
            if (type == typeof(Entity)) { Entities = true; return true; }
            if (type == typeof(RecordId)) return true;   // two strings; its converter reads nothing else

            foreach (var converter in _json.Converters)
            {
                if (!converter.CanConvert(type)) continue;
                if (converter is not ISaveCapture capture) return SaveSerializer.IsStateless(converter);
                if (type.IsValueType && !ContainsReferences(type)) return true;   // its own copy
                copy = value => Captured(value, type, capture);
                return true;
            }
            if (type.GetCustomAttribute<JsonConverterAttribute>() != null) return false;

            if (Nullable.GetUnderlyingType(type) is { } underlying)
                return TryCopy(underlying, out var inner) && inner == null;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return Collection(type, type.GetGenericArguments()[0], nameof(CopyList), out copy);
            if (type.IsSZArray)
                return Collection(type, type.GetElementType()!, nameof(CopyArray), out copy);
            if (!type.IsValueType || type.IsPrimitive || type.IsPointer || type.IsByRefLike) return false;
            return Struct(type, out copy);
        }

        private static bool ContainsReferences(Type type) =>
            (bool)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!
                .MakeGenericMethod(type).Invoke(null, null)!;

        private static Expression Captured(Expression value, Type type, object capture)
        {
            var call = Expression.Convert(
                Expression.Call(Expression.Constant(capture, typeof(ISaveCapture)),
                                typeof(ISaveCapture).GetMethod(nameof(ISaveCapture.Capture))!,
                                Expression.Convert(value, typeof(object))),
                type);
            if (type.IsValueType) return call;
            return Expression.Condition(Expression.ReferenceEqual(value, Expression.Constant(null, type)), Expression.Constant(null, type), call);
        }

        private bool Collection(Type type, Type element, string helper, out Func<Expression, Expression>? copy)
        {
            copy = null;
            if (!TryCopy(element, out var inner)) return false;
            Delegate? fix = null;
            if (inner != null)
            {
                var item = Expression.Parameter(element, "item");
                fix = Expression.Lambda(typeof(Func<,>).MakeGenericType(element, element), inner(item), item).Compile();
            }
            var method = typeof(CapturePlan).GetMethod(helper, BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(element);
            var fixType = typeof(Func<,>).MakeGenericType(element, element);
            copy = value => Expression.Call(method, value, Expression.Constant(fix, fixType));
            return true;
        }

        // A struct: each member the save writes, captured; one with a property the save writes is left to the
        // tick (a property may read what a copy of its fields does not hold).
        private bool Struct(Type type, out Func<Expression, Expression>? copy)
        {
            copy = null;
            if (!_seen.Add(type)) return false;   // holds itself, through a list: left to the tick
            try
            {
                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    if (property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0 && Written(property))
                        return false;
                var fixes = new List<(FieldInfo Field, Func<Expression, Expression> Copy)>();
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!(field.IsPublic || field.IsDefined(typeof(JsonIncludeAttribute))) || !Written(field)) continue;
                    if (field.IsDefined(typeof(JsonConverterAttribute))) return false;
                    if (!TryCopy(field.FieldType, out var inner)) return false;
                    if (inner == null) continue;
                    if (field.IsInitOnly) return false;
                    fixes.Add((field, inner));
                }
                if (fixes.Count == 0) return true;
                copy = value =>
                {
                    var copied = Expression.Variable(type, "copied");
                    var body = new List<Expression> { Expression.Assign(copied, value) };
                    foreach (var (field, inner) in fixes)
                        body.Add(Expression.Assign(Expression.Field(copied, field), inner(Expression.Field(copied, field))));
                    body.Add(copied);
                    return Expression.Block(new[] { copied }, body);
                };
                return true;
            }
            finally { _seen.Remove(type); }
        }

        // Whether the save dialect writes the member: not [Transient], not always [JsonIgnore]d.
        private static bool Written(MemberInfo member) =>
            !member.IsDefined(typeof(TransientAttribute))
            && member.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always };
    }
}
#pragma warning restore SAGE0131
