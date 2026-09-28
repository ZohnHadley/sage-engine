#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace sage_engine;

// The pieces strict loading is made of (REDESIGN §4.2, issue #22), shared by records, prefab
// components and prefab part options so all three say the same thing about the same mistake.

// "did you mean 'colour'?" — the nearest of the names a thing could have been, by edit distance.
public static class Spelling
{
    // The closest candidate to `name`, ignoring case, when it is close enough to be a typo: at most a
    // third of the name's length away (and at least 2), so "color" finds "colour" and "x" finds nothing.
    public static string? Nearest(string name, IEnumerable<string> candidates)
    {
        string wanted = name.ToLowerInvariant();
        int limit = Math.Max(2, wanted.Length / 3);
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (string candidate in candidates.OrderBy(c => c, StringComparer.Ordinal))
        {
            int distance = Distance(wanted, candidate.ToLowerInvariant());
            if (distance < bestDistance) { best = candidate; bestDistance = distance; }
        }
        return bestDistance <= limit ? best : null;
    }

    // "; did you mean 'x'?" or nothing.
    public static string Suggest(string name, IEnumerable<string> candidates) =>
        Nearest(name, candidates) is { } near ? $"; did you mean '{near}'?" : "";

    // Levenshtein distance: insertions, deletions and substitutions.
    public static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}

// Fields a file writes that the type it is read as does not have (UnmappedMemberHandling.Disallow,
// with a better message): every one of them rather than the first, each at its own path, with the
// nearest real field suggested. Walks the JSON beside the type as System.Text.Json would read it —
// its contract, not reflection — so a member with a converter of its own (a colour, an AI task, a
// vector) is taken as a leaf and never second-guessed.
internal static class JsonMembers
{
    public readonly record struct Unknown(string Path, string Name, string Parent, string? Suggestion)
    {
        public string Message =>
            $"unknown field '{Name}'" + (Parent.Length > 0 ? $" in '{Parent}'" : "") +
            (Suggestion != null ? $"; did you mean '{Suggestion}'?" : "");
    }

    public static List<Unknown> Find(JsonNode? node, Type type, JsonSerializerOptions options, string path = "")
    {
        var found = new List<Unknown>();
        Walk(node, type, options, path, found, 0);
        return found;
    }

    private static void Walk(JsonNode? node, Type type, JsonSerializerOptions options, string path, List<Unknown> found, int depth)
    {
        if (node is null || depth > 32) return;
        type = Nullable.GetUnderlyingType(type) ?? type;
        JsonTypeInfo info;
        try { info = options.GetTypeInfo(type); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { return; }

        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object when node is JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    var property = info.Properties.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
                    string at = Child(path, key);
                    if (property == null)
                    {
                        found.Add(new Unknown(at, key, Display(path), Spelling.Nearest(key, info.Properties.Select(p => JsonName(p.Name)))));
                        continue;
                    }
                    if (property.CustomConverter == null) Walk(value, property.PropertyType, options, at, found, depth + 1);
                }
                break;
            case JsonTypeInfoKind.Enumerable when node is JsonArray array && ElementOf(type) is { } element:
                for (int i = 0; i < array.Count; i++) Walk(array[i], element, options, $"{path}[{i}]", found, depth + 1);
                break;
            case JsonTypeInfoKind.Dictionary when node is JsonObject map && ElementOf(type) is { } element:
                foreach (var (key, value) in map) Walk(value, element, options, Child(path, key), found, depth + 1);
                break;
        }
    }

    // A list's item type or a dictionary's value type (JsonTypeInfo.ElementType is not in .NET 8).
    public static Type? ElementOf(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        foreach (var i in type.GetInterfaces().Prepend(type))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)) return i.GetGenericArguments()[1];
        foreach (var i in type.GetInterfaces().Prepend(type))
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) return i.GetGenericArguments()[0];
        return null;
    }

    // A C# member as a file writes it: "Colour" is "colour".
    public static string JsonName(string member) => member.Length == 0 ? member : char.ToLowerInvariant(member[0]) + member[1..];

    // A property under `path`, in the form JsonSource.Locate reads: plain when it can be, quoted when
    // the name has characters a dotted path would split on.
    public static string Child(string path, string key)
    {
        bool plain = key.Length > 0 && key.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '+');
        return plain ? (path.Length == 0 ? key : $"{path}.{key}") : $"{path}['{key}']";
    }

    // A path for a message: "Parts['melee'].Attack" -> "parts.melee.attack", indices dropped.
    public static string Display(string path)
    {
        var parts = JsonSource.ParsePath(path).Where(p => p.Name != null).Select(p => JsonName(p.Name!));
        return string.Join('.', parts);
    }
}

// Every record reference and asset path inside a value read from content, with where it is. Walks
// what System.Text.Json reads (the options' contract), into nested objects, lists and dictionaries, so
// an inventory stack's item is checked as well as a record's top-level fields.
internal static class ContentValues
{
    public readonly record struct Found(string Path, object Value);

    public static List<Found> Find(object? value, JsonSerializerOptions options, string path = "")
    {
        var found = new List<Found>();
        Walk(value, value?.GetType(), options, path, found, 0);
        return found;
    }

    private static void Walk(object? value, Type? type, JsonSerializerOptions options, string path, List<Found> found, int depth)
    {
        if (value is null || type is null || depth > 32) return;
        switch (value)
        {
            case RecordId or IRecordRef or AssetPath:
                found.Add(new Found(path, value));
                return;
            case string or JsonNode:
                return;
        }

        JsonTypeInfo info;
        try { info = options.GetTypeInfo(value.GetType()); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { return; }

        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                foreach (var property in info.Properties)
                {
                    if (property.Get == null) continue;
                    object? member;
                    try { member = property.Get(value); }
                    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { continue; }
                    Walk(member, property.PropertyType, options, JsonMembers.Child(path, property.Name), found, depth + 1);
                }
                break;
            case JsonTypeInfoKind.Dictionary when value is IDictionary map:
                foreach (DictionaryEntry entry in map)
                    Walk(entry.Value, JsonMembers.ElementOf(value.GetType()), options, JsonMembers.Child(path, entry.Key.ToString() ?? ""), found, depth + 1);
                break;
            case JsonTypeInfoKind.Enumerable when value is IEnumerable list:
                int i = 0;
                foreach (var item in list) Walk(item, JsonMembers.ElementOf(value.GetType()), options, $"{path}[{i++}]", found, depth + 1);
                break;
        }
    }
}

// An asset a record names exists in some mount (issue #22). A compiled shader counts as there when
// its source is: the build turns shaders/lit.fx into shaders/lit.mgfxo, and content names the output.
internal static class AssetChecks
{
    public static bool Exists(VirtualFileSystem vfs, VirtualPath path)
    {
        if (vfs.Exists(path)) return true;
        return IsCompiledShader(path) && vfs.Exists(VirtualPath.Parse(path.Value[..^".mgfxo".Length] + ".fx"));
    }

    public static bool IsCompiledShader(VirtualPath path) => path.Value.EndsWith(".mgfxo", StringComparison.OrdinalIgnoreCase);
}
