#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// A path into a record's JSON as the console and the form write it: `stats.health`, `items[2]`,
// `parts.body.shape`, `['odd name'].x`. A leading `$` is allowed. A name is matched ignoring case, as the
// record loader matches one; an index may be one past the end of its array, which adds an element.
internal static class RecordPath
{
    internal readonly record struct Segment(string? Name, int Index);

    public static List<Segment>? Parse(string? path)
    {
        var parts = new List<Segment>();
        if (string.IsNullOrWhiteSpace(path)) return parts;
        int i = path[0] == '$' ? 1 : 0;
        while (i < path.Length)
        {
            char c = path[i];
            if (c == '.') { i++; continue; }
            if (c == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0) return null;
                string inside = path[(i + 1)..close];
                if (inside.Length >= 2 && inside[0] == '\'' && inside[^1] == '\'') parts.Add(new Segment(inside[1..^1], 0));
                else if (int.TryParse(inside, out int n) && n >= 0) parts.Add(new Segment(null, n));
                else return null;
                i = close + 1;
                continue;
            }
            int end = i;
            while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
            parts.Add(new Segment(path[i..end], 0));
            i = end;
        }
        return parts;
    }

    public static string Format(IReadOnlyList<Segment> parts, int count = -1)
    {
        if (count < 0) count = parts.Count;
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (parts[i].Name is { } name) text.Append(text.Length > 0 ? "." : "").Append(name);
            else text.Append('[').Append(parts[i].Index).Append(']');
        }
        return text.ToString();
    }

    public static JsonNode? Get(JsonNode? root, IEnumerable<Segment> parts)
    {
        var node = root;
        foreach (var part in parts)
        {
            if (!Find(node, part, out node, out _)) return null;
        }
        return node;
    }

    // The node a segment names inside `container` (which may itself be JSON null); `key` is the object's
    // spelling of it (names match ignoring case).
    private static bool Find(JsonNode? container, Segment part, out JsonNode? node, out string? key)
    {
        node = null;
        key = null;
        if (part.Name != null && container is JsonObject obj)
        {
            foreach (var (k, v) in obj)
                if (string.Equals(k, part.Name, StringComparison.OrdinalIgnoreCase)) { key = k; node = v; return true; }
            return false;
        }
        if (part.Name == null && container is JsonArray array && part.Index < array.Count) { node = array[part.Index]; return true; }
        return false;
    }

    // Puts `value` at `parts`, making the objects on the way. `existed` is whether the leaf was there;
    // `created` how many segments of the path were missing (the first of them is what an undo removes, so
    // it leaves no empty objects behind). False when the path cannot be made: an index further out than
    // one past the end, a name inside an array, a value in the way that is not an object or array.
    public static bool Set(JsonObject root, IReadOnlyList<Segment> parts, JsonNode? value, out JsonNode? before, out int firstMissing)
    {
        before = null;
        firstMissing = -1;
        if (parts.Count == 0) return false;
        JsonNode container = root;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            bool last = i == parts.Count - 1;
            bool found = Find(container, part, out var existing, out string? key);
            if (!found)
            {
                if (firstMissing < 0)
                {
                    if (part.Name != null && container is not JsonObject) return false;
                    if (part.Name == null && (container is not JsonArray a || part.Index > a.Count)) return false;
                    firstMissing = i;
                }
                if (last) { Put(container, part, key, value); return true; }
                // The next segment decides what to make: an index makes an array, a name an object.
                Put(container, part, key, parts[i + 1].Name == null ? new JsonArray() : new JsonObject());
                Find(container, part, out var made, out _);
                container = made!;
                continue;
            }
            if (last)
            {
                before = existing?.DeepClone();
                Put(container, part, key, value);
                return true;
            }
            if (existing is not (JsonObject or JsonArray)) return false;
            container = existing;
        }
        return false;
    }

    private static void Put(JsonNode container, Segment part, string? key, JsonNode? value)
    {
        if (container is JsonObject obj) obj[key ?? part.Name!] = value;
        else
        {
            var array = (JsonArray)container;
            if (part.Index < array.Count) array[part.Index] = value;
            else array.Add(value);
        }
    }

    // Removes what `parts` names; true when something was there.
    public static bool Remove(JsonObject root, IReadOnlyList<Segment> parts)
    {
        if (parts.Count == 0) return false;
        var container = Get(root, parts.Take(parts.Count - 1));
        var last = parts[^1];
        if (!Find(container, last, out _, out string? key)) return false;
        if (container is JsonObject obj) return obj.Remove(key ?? last.Name!);
        ((JsonArray)container!).RemoveAt(last.Index);
        return true;
    }

}
