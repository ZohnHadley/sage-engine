#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Sage.Core;

// One record file's bytes, kept through a load so an error can say *where*: "game:data/items.json:12:9"
// rather than "game:data/items.json[3]" (issue #22). Positions are worked out only when there is
// something to report — a clean load pays for keeping the bytes and nothing else. Lines and columns are
// 1-based, and a column counts characters, not bytes, so it is the column an editor shows.
internal sealed class JsonSource
{
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly byte[] _bytes;
    private readonly int _start;   // past a UTF-8 byte order mark, which the JSON reader will not take

    public JsonSource(string name, byte[] bytes, IMount? mount = null)
    {
        Name = name;
        Mount = mount;
        _bytes = bytes;
        _start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
    }

    public string Name { get; }   // "mount:path"

    // The mount the file was read from: who wrote a record's field, for the content report (4j-2).
    public IMount? Mount { get; }

    public ReadOnlySpan<byte> Utf8 => _bytes.AsSpan(_start);

    // `offset` is into `Utf8`.
    public string At(long offset)
    {
        var text = Utf8;
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 1, lineStart = 0;
        for (int i = 0; i < offset; i++)
            if (text[i] == (byte)'\n') { line++; lineStart = i + 1; }
        int column = Encoding.UTF8.GetCharCount(text[lineStart..(int)offset]) + 1;
        return $"{Name}:{line}:{column}";
    }

    // Where a JsonException from the parser points: it counts lines from 0 and bytes within the line.
    public string AtLine(long line, long bytePositionInLine)
    {
        var text = Utf8;
        long offset = 0;
        for (long seen = 0; seen < line && offset < text.Length; offset++)
            if (text[(int)offset] == (byte)'\n') seen++;
        return At(offset + bytePositionInLine);
    }

    // Record `index` of the file (its position in the top-level array, or 0 for a file holding one
    // record), and inside it `path` as System.Text.Json writes one ("$.tasks[1]", "stats.agility"). The
    // deepest part of the path that exists in this file wins, so a path into merged data that this file
    // only partly wrote still lands on the property it did write. A property named "tags+" or "tags-"
    // answers for "tags": that is how a patch writes it.
    public string Locate(int index, string? path = null)
    {
        try { return At(Find(index, ParsePath(path))); }
        catch (JsonException) { return $"{Name}[{index}]"; }   // it parsed once, so this is not expected
    }

    private long Find(int index, List<(string? Name, int Index)> path)
    {
        var reader = new Utf8JsonReader(Utf8, ReaderOptions);
        if (!reader.Read()) return 0;
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            if (!StepIntoElement(ref reader, index)) return 0;
        }
        else if (index != 0) return reader.TokenStartIndex;

        long best = reader.TokenStartIndex;
        foreach (var (name, element) in path)
        {
            if (name != null)
            {
                if (reader.TokenType != JsonTokenType.StartObject || !StepIntoProperty(ref reader, name, out long at)) break;
                best = at;
            }
            else
            {
                if (reader.TokenType != JsonTokenType.StartArray || !StepIntoElement(ref reader, element)) break;
                best = reader.TokenStartIndex;
            }
        }
        return best;
    }

    // On a StartArray: moves to the start of element `index`.
    private static bool StepIntoElement(ref Utf8JsonReader reader, int index)
    {
        for (int i = 0; reader.Read() && reader.TokenType != JsonTokenType.EndArray; i++)
        {
            if (i == index) return true;
            reader.Skip();
        }
        return false;
    }

    // On a StartObject: moves to the value of property `name` (records are case-insensitive), and
    // gives where the property's name starts, which is where a person looks for it.
    private static bool StepIntoProperty(ref Utf8JsonReader reader, string name, out long at)
    {
        at = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString() ?? "";
            at = reader.TokenStartIndex;
            reader.Read();
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) ||
                (key.Length == name.Length + 1 && (key[^1] == '+' || key[^1] == '-') &&
                 key.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                return true;
            reader.Skip();
        }
        return false;
    }

    // "$.tasks[1]", "tasks[1]", "$['odd name'].x" -> [("tasks", 0), (null, 1)] and so on.
    internal static List<(string? Name, int Index)> ParsePath(string? path)
    {
        var parts = new List<(string?, int)>();
        if (string.IsNullOrEmpty(path)) return parts;
        int i = path[0] == '$' ? 1 : 0;
        while (i < path.Length)
        {
            char c = path[i];
            if (c == '.') { i++; continue; }
            if (c == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0) break;
                string inside = path[(i + 1)..close];
                if (inside.Length >= 2 && inside[0] == '\'' && inside[^1] == '\'') parts.Add((inside[1..^1], 0));
                else if (int.TryParse(inside, out int n)) parts.Add((null, n));
                else break;
                i = close + 1;
                continue;
            }
            int end = i;
            while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
            parts.Add((path[i..end], 0));
            i = end;
        }
        return parts;
    }

    // Record `index` of the file as it is written there (issue #401): what one definition or patch said,
    // parsed again from the kept bytes when asked. Null when it is not an object.
    public System.Text.Json.Nodes.JsonObject? Record(int index)
    {
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(Utf8, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return (root is System.Text.Json.Nodes.JsonArray array ? index < array.Count ? array[index] : null : index == 0 ? root : null) as System.Text.Json.Nodes.JsonObject;
        }
        catch (JsonException) { return null; }   // it parsed once, so this is not expected
    }

    // The first property a path names: the record field an error belongs to.
    internal static string? FieldOf(string? path)
    {
        var parts = ParsePath(path);
        return parts.Count > 0 ? parts[0].Name : null;
    }
}

// A record as written in one file: the file, and its position among the file's records.
internal readonly record struct RecordSource(JsonSource File, int Index)
{
    public string At(string? path = null) => File.Locate(Index, path);

    public System.Text.Json.Nodes.JsonObject? Record() => File?.Record(Index);
}
