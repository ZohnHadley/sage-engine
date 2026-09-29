#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sage.Core;

// The metadata table (docs/REDESIGN.md §3.4 item 2, issue #18): for every declaration — component,
// tag, record type, saved resource, prefab part — its id and its fields, with each field's type, default,
// range, unit, tooltip, category, enum values and what it refers to. The inspector, `ent_dump`,
// `ent_types`, the FGD export and the registry dump read it, and a JSON Schema (#21) is written from it.
//
//   [Component("sage:point_light")]
//   public struct PointLight : IComponent
//   {
//       [Property(Min = 0, Unit = "m", Tooltip = "Where the light fades to nothing")] public float Range;
//   }
//
// Sage.Generators writes each assembly's table (MetadataGenerator): the shape is read at compile time
// and the field accessors are plain code, so nothing here reflects in a build that used the generator.
// An assembly built without it — the test assembly, a tool — is read by reflection over the same
// attributes (Metadata.Reflect), which is the dev fallback and nothing more.

// What a field is, for a person: the range a value may take, what it is measured in, what it is for
// and where it goes in a form. Every part optional; a field without one is still in the table.
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class PropertyAttribute : Attribute
{
    // NaN = no bound. (An attribute argument cannot be a nullable double.)
    public double Min { get; set; } = double.NaN;
    public double Max { get; set; } = double.NaN;

    // What the number is measured in, as a person writes it: "m", "s", "hp", "deg", "kg".
    public string? Unit { get; set; }

    // One sentence: what the field does. The inspector's tooltip and the FGD's key description.
    public string? Tooltip { get; set; }

    // A heading to group fields under in a form ("Vitals", "Shape").
    public string? Category { get; set; }
}

// A plain RecordId field (or a list or map of them) names a record of this type: `[RecordRef("item")]`.
// What makes a form offer the item ids instead of a text box, and what a schema turns into an enum.
// A RecordRef<T> field (issue #22) already says so through T's [Record], checked when content loads,
// and is the better choice where it fits; this is for the RecordId fields that stay plain (a component's
// runtime state, say). Putting it on a RecordRef<T> is a build error (SAGE0041).
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class RecordRefAttribute : Attribute
{
    public RecordRefAttribute(string recordType) { RecordType = recordType; }
    public string RecordType { get; }
}

// An AssetPath field (or a list of them) names an asset of this kind: "texture", "sound", "map", "mesh".
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
public sealed class AssetKindAttribute : Attribute
{
    public AssetKindAttribute(string kind) { Kind = kind; }
    public string Kind { get; }
}

public enum DeclarationKind { Component, Tag, Record, SavedResource, PrefabPart, Other }

// The shape of a value, as content writes it. Chosen so that each maps to one JSON Schema type (#21).
public enum ValueKind
{
    Bool, Integer, Number, String, Enum,
    Vector2, Vector3, Vector4, Quaternion,
    RecordId, AssetPath, Entity,
    List,       // an array or list; Item describes one element
    Map,        // a dictionary keyed by string; Item describes one value
    Object,     // a nested class or struct; Fields describe it
    Json,       // free-form JSON (JsonObject, JsonNode)
    Other,      // anything else: shown, not edited
}

// One field of a declaration (or of an object nested in one).
public sealed class FieldMetadata
{
    public FieldMetadata(string name, Type type, ValueKind kind,
                         Func<object, object?>? get = null, Action<object, object?>? set = null)
    {
        Name = name;
        JsonName = name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
        Type = type;
        Kind = kind;
        Get = get;
        Set = set;
    }

    // The C# member name, and the key content writes (camel case, matched ignoring case).
    public string Name { get; }
    public string JsonName { get; init; }
    public Type Type { get; }
    public ValueKind Kind { get; }

    // Read and write the field on a boxed instance (a boxed struct is written in place). Null for a list
    // item or a map value, which is not a member of anything.
    public Func<object, object?>? Get { get; }
    public Action<object, object?>? Set { get; }

    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Unit { get; init; }
    public string? Tooltip { get; init; }
    public string? Category { get; init; }

    // Enum member names, for Kind == Enum.
    public IReadOnlyList<string> EnumValues { get; init; } = Array.Empty<string>();

    // What a RecordId names (a record type), or an AssetPath (an asset kind).
    public string? RecordType { get; init; }
    public string? AssetKind { get; init; }

    // [Transient]: never saved.
    public bool Transient { get; init; }

    // One element of a List, or one value of a Map.
    public FieldMetadata? Item { get; init; }

    // The fields of an Object.
    public IReadOnlyList<FieldMetadata> Fields { get; init; } = Array.Empty<FieldMetadata>();

    // A short C# spelling of the type, for people: "float", "List<RecordId>".
    public string TypeName => Metadata.ShortName(Type);

    // Whether a form can edit it in place.
    public bool Editable => Set != null && Kind is not (ValueKind.List or ValueKind.Map or ValueKind.Object
                                                         or ValueKind.Json or ValueKind.Other or ValueKind.Entity);

    public override string ToString() => $"{JsonName}: {TypeName}";
}

// One declaration: its kind, its stable id and its fields.
public sealed class TypeMetadata
{
    private readonly Func<object>? _create;
    private object? _defaults;
    private bool _madeDefaults;

    public TypeMetadata(DeclarationKind kind, string id, Type type, Func<object>? create, IReadOnlyList<FieldMetadata> fields,
                        bool generated = true)
    {
        Kind = kind;
        Id = id;
        Type = type;
        _create = create;
        Fields = fields;
        Generated = generated;
    }

    public DeclarationKind Kind { get; }
    public string Id { get; }
    public Type Type { get; }
    public IReadOnlyList<FieldMetadata> Fields { get; }

    // True when Sage.Generators wrote this; false when it was read by reflection (the dev fallback).
    public bool Generated { get; }

    // A new instance with every field at its default — what `{}` in content means.
    public object? CreateDefault()
    {
        try { return _create?.Invoke(); }
        catch (Exception ex) when (ex is MissingMethodException or InvalidOperationException or TargetInvocationException) { return null; }
    }

    // A field's default value: what a new instance holds. Null when the type cannot be made.
    public object? DefaultOf(FieldMetadata field)
    {
        if (!_madeDefaults)
        {
            _defaults = CreateDefault();
            _madeDefaults = true;
        }
        return _defaults != null && field.Get != null ? field.Get(_defaults) : null;
    }

    public FieldMetadata? Field(string name)
    {
        foreach (var f in Fields)
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.JsonName, name, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    public override string ToString() => $"{Kind} {Id} ({Type.Name}, {Fields.Count} field(s))";
}

// What Sage.Generators writes into each assembly that declares things: its metadata table.
public interface IGeneratedMetadata
{
    IReadOnlyList<TypeMetadata> Types { get; }
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GeneratedMetadataAttribute : Attribute
{
    public GeneratedMetadataAttribute(Type type) { Type = type; }
    public Type Type { get; }
}

// The table, for every loaded assembly. A pure function of the assembly, so it is built once per
// process and shared by every engine in it.
public static class Metadata
{
    private static readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<Type, TypeMetadata>> ByAssembly = new();
    private static readonly ConcurrentDictionary<Type, TypeMetadata> Undeclared = new();

    // The metadata of a type: its declaration's, from the generated table (or by reflection for an
    // assembly built without the generator); for a type that declares nothing — one of Friflo's own
    // components, a test's probe — by reflection, with Kind Other.
    public static TypeMetadata Of(Type type)
    {
        if (In(type.Assembly).TryGetValue(type, out var declared)) return declared;
        return Undeclared.GetOrAdd(type, static t => Reflect(t, DeclarationKind.Other, t.Name));
    }

    // Every declaration an assembly makes.
    public static IReadOnlyDictionary<Type, TypeMetadata> In(Assembly assembly) => ByAssembly.GetOrAdd(assembly, static a =>
    {
        var table = new Dictionary<Type, TypeMetadata>();
        if (a.GetCustomAttribute<GeneratedMetadataAttribute>() is { } generated)
        {
            foreach (var t in ((IGeneratedMetadata)Activator.CreateInstance(generated.Type)!).Types)
                table[t.Type] = t;
            return table;
        }
        foreach (var type in AllTypes(a))
            if (DeclarationOf(type) is { } d)
                table[type] = Reflect(type, d.Kind, d.Id);
        return table;
    });

    // The kind and id a type is declared with, read off its attribute.
    public static (DeclarationKind Kind, string Id)? DeclarationOf(Type type)
    {
        if (type.GetCustomAttribute<ComponentAttribute>(false) is { } c) return (DeclarationKind.Component, c.Id);
        if (type.GetCustomAttribute<TagAttribute>(false) is { } t) return (DeclarationKind.Tag, t.Id);
        if (type.GetCustomAttribute<RecordAttribute>(false) is { } r) return (DeclarationKind.Record, r.Type);
        if (type.GetCustomAttribute<SavedResourceAttribute>(false) is { } s) return (DeclarationKind.SavedResource, s.Name);
        if (type.GetCustomAttribute<PrefabPartAttribute>(false) is { } p) return (DeclarationKind.PrefabPart, p.Id);
        return null;
    }

    // ---- the reflection fallback -----------------------------------------------------------------
    //
    // Reads what the generator reads, the same way, so the two tables agree (test:
    // TheGeneratedTableMatchesReflectionForEveryEngineDeclaration). Only a type's public instance fields
    // and its public get/set properties, without [JsonIgnore]: exactly what content can write.

    private const int MaxDepth = 3;

    public static TypeMetadata Reflect(Type type, DeclarationKind kind, string id)
    {
        Func<object>? create = type.IsValueType
            ? () => Activator.CreateInstance(type)!
            : type.GetConstructor(Type.EmptyTypes) is { } ctor && !type.IsAbstract ? () => ctor.Invoke(null) : null;
        return new TypeMetadata(kind, id, type, create, ReflectFields(type, 0, new HashSet<Type>()), generated: false);
    }

    private static IReadOnlyList<FieldMetadata> ReflectFields(Type type, int depth, HashSet<Type> seen)
    {
        var fields = new List<FieldMetadata>();
        if (!seen.Add(type)) return fields;
        try
        {
            foreach (var member in Members(type))
            {
                Type memberType = member is FieldInfo fi ? fi.FieldType : ((PropertyInfo)member).PropertyType;
                Func<object, object?> get;
                Action<object, object?>? set;
                if (member is FieldInfo field)
                {
                    get = field.GetValue;
                    set = field.IsInitOnly ? null : field.SetValue;
                }
                else
                {
                    var property = (PropertyInfo)member;
                    get = property.GetValue;
                    set = property.SetValue;
                }
                fields.Add(Describe(member.Name, memberType, member, depth, seen, get, set));
            }
        }
        finally
        {
            seen.Remove(type);
        }
        return fields;
    }

    private static FieldMetadata Describe(string name, Type type, MemberInfo? member, int depth, HashSet<Type> seen,
                                          Func<object, object?>? get, Action<object, object?>? set)
    {
        var property = member?.GetCustomAttribute<PropertyAttribute>(false);
        var kind = KindOf(type, out var element);
        FieldMetadata? item = null;
        IReadOnlyList<FieldMetadata> nested = Array.Empty<FieldMetadata>();
        string? recordType = member?.GetCustomAttribute<RecordRefAttribute>(false)?.RecordType;
        string? assetKind = member?.GetCustomAttribute<AssetKindAttribute>(false)?.Kind;

        if (element != null)
        {
            item = Describe("item", element, null, depth + 1, seen, null, null);
            if ((recordType != null && item.RecordType == null) || assetKind != null) item = WithRefs(item, item.RecordType ?? recordType, assetKind);
        }
        else if (kind == ValueKind.Object && depth < MaxDepth)
            nested = ReflectFields(type, depth + 1, seen);

        var enumType = Nullable.GetUnderlyingType(type) ?? type;
        return new FieldMetadata(name, type, kind, get, set)
        {
            JsonName = member?.GetCustomAttribute<JsonPropertyNameAttribute>(false)?.Name ?? Camel(name),
            Min = property != null && !double.IsNaN(property.Min) ? property.Min : null,
            Max = property != null && !double.IsNaN(property.Max) ? property.Max : null,
            Unit = property?.Unit,
            Tooltip = property?.Tooltip,
            Category = property?.Category,
            EnumValues = kind == ValueKind.Enum ? Enum.GetNames(enumType) : Array.Empty<string>(),
            RecordType = kind == ValueKind.RecordId ? TypedTarget(type) ?? recordType : null,
            AssetKind = kind == ValueKind.AssetPath ? assetKind : null,
            Transient = member?.GetCustomAttribute<TransientAttribute>(false) != null,
            Item = item,
            Fields = nested,
        };
    }

    // A list's item carries the reference its field declares: `[RecordRef("effect")] List<RecordId> Ids`.
    private static FieldMetadata WithRefs(FieldMetadata item, string? recordType, string? assetKind) =>
        new(item.Name, item.Type, item.Kind)
        {
            JsonName = item.JsonName, EnumValues = item.EnumValues, Item = item.Item, Fields = item.Fields,
            RecordType = item.Kind == ValueKind.RecordId ? recordType : item.RecordType,
            AssetKind = item.Kind == ValueKind.AssetPath ? assetKind : item.AssetKind,
        };

    private static IEnumerable<MemberInfo> Members(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (field.GetCustomAttribute<JsonIgnoreAttribute>() == null) yield return field;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetIndexParameters().Length == 0 && property.GetMethod is { IsPublic: true }
                && property.SetMethod is { IsPublic: true } setter && !IsInitOnly(setter)
                && property.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                yield return property;
    }

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    // The same rules as MetadataGenerator.KindOf, which says why each is what it is.
    public static ValueKind KindOf(Type type, out Type? element)
    {
        element = null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(bool)) return ValueKind.Bool;
        if (type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            return ValueKind.Integer;
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return ValueKind.Number;
        if (type == typeof(string)) return ValueKind.String;
        if (type.IsEnum) return ValueKind.Enum;
        if (type == typeof(Vector2)) return ValueKind.Vector2;
        if (type == typeof(Vector3)) return ValueKind.Vector3;
        if (type == typeof(Vector4)) return ValueKind.Vector4;
        if (type == typeof(Quaternion)) return ValueKind.Quaternion;
        if (type == typeof(RecordId)) return ValueKind.RecordId;
        if (type == typeof(AssetPath)) return ValueKind.AssetPath;
        // By name: the kernel does not reference the simulation (as MetadataGenerator reads it).
        if (type.FullName == "Sage.Simulation.Entity") return ValueKind.Entity;
        if (TypedTarget(type) != null) return ValueKind.RecordId;   // RecordRef<T> (issue #22)
        if (typeof(JsonNode).IsAssignableFrom(type) || type == typeof(System.Text.Json.JsonElement)) return ValueKind.Json;
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            element = type.GetElementType();
            return ValueKind.List;
        }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();
            if (args.Length == 1 && (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>)
                                     || definition == typeof(HashSet<>) || definition == typeof(ICollection<>)
                                     || definition == typeof(IReadOnlyCollection<>) || definition == typeof(IEnumerable<>)))
            {
                element = args[0];
                return ValueKind.List;
            }
            if (args.Length == 2 && args[0] == typeof(string)
                && (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)))
            {
                element = args[1];
                return ValueKind.Map;
            }
        }
        if (type.IsPrimitive || type.IsPointer || typeof(Delegate).IsAssignableFrom(type)
            || type.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))
            || typeof(IEnumerable).IsAssignableFrom(type))
            return ValueKind.Other;
        return ValueKind.Object;
    }

    // For RecordRef<T>, the record type T is declared as ("" when T is not a record type); else null.
    public static string? TypedTarget(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(RecordRef<>)
            ? RecordRefs.TypeNameOf(type.GetGenericArguments()[0]) ?? ""
            : null;
    }

    public static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    // "float", "List<RecordId>", "Vector3?".
    public static string ShortName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return ShortName(inner) + "?";
        if (type.IsArray) return ShortName(type.GetElementType()!) + "[]";
        string? keyword = Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => "bool", TypeCode.Byte => "byte", TypeCode.SByte => "sbyte",
            TypeCode.Int16 => "short", TypeCode.UInt16 => "ushort", TypeCode.Int32 => "int", TypeCode.UInt32 => "uint",
            TypeCode.Int64 => "long", TypeCode.UInt64 => "ulong", TypeCode.Single => "float", TypeCode.Double => "double",
            TypeCode.Decimal => "decimal", TypeCode.String => "string", _ => null,
        };
        if (keyword != null && !type.IsEnum) return keyword;
        if (!type.IsGenericType) return type.Name;
        string name = type.Name.Substring(0, type.Name.IndexOf('`'));
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(ShortName)) + ">";
    }

    private static IEnumerable<Type> AllTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }
}
