#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sage.Core;

// Changes a record file the way a person would (issue #218, REDESIGN §4.6, docs/design/15-editor.md).
//
// **A save that re-serialises the file loses what a person wrote in it**: comments, the order they put
// keys in, the one-line vectors and the record beside the one being saved. So this does not serialise a
// file; it keeps the text and splices into it. A small JSONC parser notes where every value starts and
// ends, an edit replaces, adds or removes exactly one value's text, and the file is parsed again before
// the next. Everything an edit does not touch stays byte for byte, so a file nothing changed in is
// written back exactly as it was read.
//
// Files are what the record store reads (RecordStore.Load): one record object, or an array of them, with
// comments and trailing commas. A record is found by its `type` and `id`; a bare id means `Namespace`,
// the namespace of the mount the file is in.
//
// New values are written in the record store's dialect — its converters, camel case, enums as strings,
// nulls left out — with arrays of numbers and strings (vectors, colours, tags) on one line and objects
// indented like the lines around them.
//
// Replacing a record, or setting a value that is an object or an array, compares the new value with what
// the file says and writes only the differences, so moving one placement changes the number that moved
// and nothing else; `PatchRecord` writes the differences between two versions of a record when the file
// does not spell out everything the record holds (fields left at their defaults).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class JsonFileEdit
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions ScalarWriting = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _original;
    private readonly bool _byteOrderMark;
    private readonly JsonSerializerOptions _dialect;
    private Node? _root;

    // `text` is the file's contents ("" for a file that does not exist yet); `options` are the record
    // store's (RecordStore.Json), which carry its converters. Throws JsonException when the text is not
    // JSON a record file could hold.
    public JsonFileEdit(string text, JsonSerializerOptions? options = null) : this(text, options, false) { }

    private JsonFileEdit(string text, JsonSerializerOptions? options, bool byteOrderMark)
    {
        _original = text;
        _byteOrderMark = byteOrderMark;
        Text = text;
        _dialect = options != null ? new JsonSerializerOptions(options) : new JsonSerializerOptions { IncludeFields = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(), Converters = { new Vector2JsonConverter(), new Vector3JsonConverter(), new QuaternionJsonConverter(), new JsonStringEnumConverter() } };
        _dialect.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        _dialect.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        _dialect.WriteIndented = false;
        _root = new Parser(text).ParseDocument();
        // The parser finds values' edges; the reader is what says a number or a literal is well formed,
        // with the message every other JSON error in Sage has.
        if (_root != null) using (JsonDocument.Parse(text.TrimStart('\uFEFF'), DocumentOptions)) { }
    }

    // The file at `path`, or an empty one when there is none yet.
    public static JsonFileEdit Open(string path, JsonSerializerOptions? options = null)
    {
        if (!File.Exists(path)) return new JsonFileEdit("", options, false);
        byte[] bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return new JsonFileEdit(Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), options, bom);
    }

    // The file as it now reads.
    public string Text { get; private set; }

    // Whether any edit changed the text.
    public bool Changed => !string.Equals(Text, _original, StringComparison.Ordinal);

    // What a bare id in this file means: the namespace of the mount it is in. Left null, a bare id
    // matches a record of that name in any namespace.
    public string? Namespace { get; init; }

    // Writes the file whole and moves it into place, so an interrupted save cannot leave half a file for
    // the record loader to refuse. A byte order mark the file had is kept.
    public void Save(string path)
    {
        string? directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, Text, new UTF8Encoding(_byteOrderMark));
        File.Move(temporary, path, overwrite: true);
    }

    // A value as the record store would write it: what `Set` and `SetRecord` turn objects into.
    public JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), _dialect),
    };

    public bool Contains(string type, RecordId id) => FindRecord(type, id) != null;

    // The record as the file has it, comments left out; null when the file does not have it.
    public JsonNode? ReadRecord(string type, RecordId id) => FindRecord(type, id) is { } record ? ToJsonNode(record.Value) : null;

    // The value at `path` inside a record ("place[3].at"); null when either is missing.
    public JsonNode? Read(string type, RecordId id, string path) =>
        FindRecord(type, id) is { } record && Navigate(record.Value, JsonSource.ParsePath(path)) is { } node ? ToJsonNode(node) : null;

    // Makes the file's record `record` (an object, or something that serialises to one), writing only
    // what differs from what the file says; a record the file does not have is added at its end. The
    // record's `type` and `id` are what find it, so the file's spelling of them is kept.
    public void SetRecord(string type, RecordId id, object record)
    {
        var node = ToNode(record) as JsonObject ?? throw new ArgumentException("A record is a JSON object", nameof(record));
        var found = FindRecord(type, id);
        if (found == null) { AddRecord(node); return; }
        var ops = new List<Op>();
        Diff(ToJsonNode(found.Value), node, new List<(string?, int)>(), ops, recordRoot: true);
        Apply(type, id, ops);
    }

    // Writes into the file's record what changed between `before` and `after`, two versions of it as a
    // program holds it: a field the file leaves at its default stays left out unless it changed. A value
    // the file does not have is added, and one it does not have cannot be removed, so this works on a
    // file that was edited since `before` was read. False when the file does not have the record.
    public bool PatchRecord(string type, RecordId id, object before, object after)
    {
        if (FindRecord(type, id) == null) return false;
        var ops = new List<Op>();
        Diff(ToNode(before), ToNode(after), new List<(string?, int)>(), ops, recordRoot: true);
        Apply(type, id, ops);
        return true;
    }

    // Adds a record at the end of the file. A file that held one record becomes an array of two.
    public void AddRecord(object record)
    {
        var node = ToNode(record) as JsonObject ?? throw new ArgumentException("A record is a JSON object", nameof(record));
        switch (_root)
        {
            case Container { IsObject: false } array:
                AddMember(array, null, node);
                break;
            case Container { IsObject: true } single:
            {
                string nl = Newline();
                Splice(new Edit(single.End, single.End, "," + nl + Format(node, "", false) + nl + "]"), new Edit(single.Start, single.Start, "[" + nl));
                break;
            }
            case null:
            {
                string nl = Newline();
                string unit = IndentUnit();
                string text = "[" + nl + unit + Format(node, unit, false) + nl + "]" + nl;
                if (Text.Trim().Length == 0) Splice(new Edit(0, Text.Length, text));
                else Splice(new Edit(Text.Length, Text.Length, (Text.EndsWith('\n') ? "" : nl) + text));   // only comments so far
                break;
            }
            default:
                throw new InvalidOperationException("The file holds a value that is not a record or an array of records");
        }
    }

    public bool RemoveRecord(string type, RecordId id)
    {
        var found = FindRecord(type, id);
        if (found == null) return false;
        if (_root is Container { IsObject: false } array) RemoveMember(array, array.Members.IndexOf(found));
        else Splice(new Edit(found.Value.Start, found.Value.End, "[]"));
        return true;
    }

    // Sets the value at `path` inside a record ("place[3].at", "overrides.components.health.max"),
    // creating the objects on the way that the file does not have yet. An index may be one past the end
    // of its array, which adds an element. False when the record is not in the file or the path cannot
    // be made (an index further out, or a value in the way that is not an object or array).
    public bool Set(string type, RecordId id, string path, object? value)
    {
        var found = FindRecord(type, id);
        if (found == null) return false;
        var parts = JsonSource.ParsePath(path);
        if (parts.Count == 0) throw new ArgumentException("Set a whole record with SetRecord", nameof(path));
        var node = ToNode(value);
        if (Navigate(found.Value, parts) is { } existing)
        {
            var ops = new List<Op>();
            Diff(ToJsonNode(existing), node, parts, ops, recordRoot: false);
            Apply(type, id, ops);
            return true;
        }
        return SetAt(type, id, parts, node);
    }

    // Removes the value at `path` inside a record: a property, or an array's element. False when there
    // was nothing there.
    public bool Remove(string type, RecordId id, string path)
    {
        var parts = JsonSource.ParsePath(path);
        if (parts.Count == 0) throw new ArgumentException("Remove a whole record with RemoveRecord", nameof(path));
        return RemoveAt(type, id, parts);
    }

    // ---- What an edit is, before it is text ----

    private enum OpKind { Set, Add, Remove }

    // Paths are relative to the record. An Add puts `Name` (or, for an array, an element) into the
    // container at `Path`.
    private sealed record Op(OpKind Kind, List<(string? Name, int Index)> Path, string? Name, JsonNode? Value);

    private static readonly HashSet<string> Identity = new(StringComparer.OrdinalIgnoreCase) { "type", "id" };

    // What to do to turn `before` into `after`. Objects are compared key by key (names as records read
    // them, ignoring case) and arrays element by element, so the operations touch only what changed. An
    // array's removals come last and from its end, so an index an earlier operation used still holds.
    private static void Diff(JsonNode? before, JsonNode? after, List<(string?, int)> path, List<Op> ops, bool recordRoot)
    {
        if (before is JsonObject a && after is JsonObject b)
        {
            foreach (var (name, value) in b)
            {
                if (recordRoot && Identity.Contains(name)) continue;
                if (TryGetProperty(a, name, out string? written, out var old)) Diff(old, value, With(path, written, 0), ops, false);
                else ops.Add(new Op(OpKind.Add, path, name, value));
            }
            foreach (var (name, _) in a)
                if (!(recordRoot && Identity.Contains(name)) && !TryGetProperty(b, name, out _, out _))
                    ops.Add(new Op(OpKind.Remove, With(path, name, 0), null, null));
            return;
        }
        if (before is JsonArray x && after is JsonArray y)
        {
            int common = Math.Min(x.Count, y.Count);
            for (int i = 0; i < common; i++) Diff(x[i], y[i], With(path, null, i), ops, false);
            for (int i = common; i < y.Count; i++) ops.Add(new Op(OpKind.Add, path, null, y[i]));
            for (int i = x.Count - 1; i >= common; i--) ops.Add(new Op(OpKind.Remove, With(path, null, i), null, null));
            return;
        }
        if (!Same(before, after)) ops.Add(new Op(OpKind.Set, path, null, after));
    }

    private static List<(string?, int)> With(List<(string?, int)> path, string? name, int index) => new(path) { (name, index) };

    private static bool TryGetProperty(JsonObject obj, string name, out string? written, out JsonNode? value)
    {
        foreach (var (key, node) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) { written = key; value = node; return true; }
        written = null;
        value = null;
        return false;
    }

    // Scalars compare as values, so `1` and `1.0` are the same number and a file is not rewritten for it.
    private static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is not JsonValue || b is not JsonValue) return false;
        var kind = a.GetValueKind();
        if (kind != b.GetValueKind()) return false;
        switch (kind)
        {
            case JsonValueKind.String: return string.Equals(a.GetValue<string>(), b.GetValue<string>(), StringComparison.Ordinal);
            case JsonValueKind.Number:
            {
                string p = a.ToJsonString(), q = b.ToJsonString();
                return p == q || (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) &&
                                  double.TryParse(q, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && m == n);
            }
            default: return true;   // true, false, null: the kind is the value
        }
    }

    // Each operation is applied to the text as it then stands, parsed again, so positions are never stale.
    private void Apply(string type, RecordId id, List<Op> ops)
    {
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case OpKind.Set:
                    if (op.Path.Count == 0) throw new InvalidOperationException("A record cannot be replaced by a value that is not an object");
                    SetAt(type, id, op.Path, op.Value);
                    break;
                case OpKind.Remove:
                    RemoveAt(type, id, op.Path);
                    break;
                case OpKind.Add:
                {
                    var record = FindRecord(type, id)!;
                    var container = op.Path.Count == 0 ? record.Value : Navigate(record.Value, op.Path);
                    if (container == null)
                    {
                        // The file does not have the container (a field left at its default): make it.
                        SetAt(type, id, op.Path, op.Name != null ? new JsonObject { [op.Name] = op.Value?.DeepClone() } : new JsonArray(op.Value?.DeepClone()));
                    }
                    else if (container is Container c && c.IsObject == (op.Name != null))
                    {
                        if (op.Name != null && FindMember(c, op.Name) is { } existing) Replace(existing.Value, op.Value, IsInline(c));
                        else AddMember(c, op.Name, op.Value);
                    }
                    break;
                }
            }
        }
    }

    private bool SetAt(string type, RecordId id, List<(string? Name, int Index)> parts, JsonNode? value)
    {
        var record = FindRecord(type, id);
        if (record == null) return false;
        Node current = record.Value;
        for (int i = 0; i < parts.Count; i++)
        {
            if (current is not Container c) return false;
            var (name, index) = parts[i];
            Member? member = name != null
                ? (c.IsObject ? FindMember(c, name) : null)
                : (!c.IsObject && index >= 0 && index < c.Members.Count ? c.Members[index] : null);
            if (member == null)
            {
                if (c.IsObject != (name != null)) return false;
                if (name == null && index != c.Members.Count) return false;
                // What is left of the path is made here, around the value.
                JsonNode? built = value?.DeepClone();
                for (int j = parts.Count - 1; j > i; j--)
                {
                    if (parts[j].Name is { } inner) built = new JsonObject { [inner] = built };
                    else if (parts[j].Index == 0) built = new JsonArray(built);
                    else return false;
                }
                AddMember(c, name, built);
                return true;
            }
            if (i == parts.Count - 1)
            {
                if (!JsonNodeEquals(ToJsonNode(member.Value), value)) Replace(member.Value, value, IsInline(c));
                return true;
            }
            current = member.Value;
        }
        return false;
    }

    private bool RemoveAt(string type, RecordId id, List<(string? Name, int Index)> parts)
    {
        var record = FindRecord(type, id);
        if (record == null) return false;
        var parent = parts.Count == 1 ? record.Value : Navigate(record.Value, parts.GetRange(0, parts.Count - 1));
        if (parent is not Container c) return false;
        var (name, index) = parts[^1];
        if (name != null)
        {
            if (!c.IsObject || FindMember(c, name) is not { } member) return false;
            RemoveMember(c, c.Members.IndexOf(member));
            return true;
        }
        if (c.IsObject || index < 0 || index >= c.Members.Count) return false;
        RemoveMember(c, index);
        return true;
    }

    private static bool JsonNodeEquals(JsonNode? a, JsonNode? b)
    {
        if (a is JsonObject x && b is JsonObject y)
        {
            if (x.Count != y.Count) return false;
            foreach (var (name, value) in y)
                if (!TryGetProperty(x, name, out _, out var other) || !JsonNodeEquals(other, value)) return false;
            return true;
        }
        if (a is JsonArray p && b is JsonArray q)
        {
            if (p.Count != q.Count) return false;
            for (int i = 0; i < p.Count; i++) if (!JsonNodeEquals(p[i], q[i])) return false;
            return true;
        }
        return Same(a, b);
    }

    // ---- Finding things in the parsed file ----

    private Member? FindRecord(string type, RecordId id)
    {
        IEnumerable<Member> candidates = _root switch
        {
            Container { IsObject: false } array => array.Members,
            Container { IsObject: true } single => new[] { new Member { Start = single.Start, Value = single } },
            _ => Array.Empty<Member>(),
        };
        foreach (var member in candidates)
        {
            if (member.Value is not Container { IsObject: true } obj) continue;
            if (StringOf(FindMember(obj, "type")) != type) continue;   // record types are case-sensitive, as the store has them
            if (StringOf(FindMember(obj, "id")) is not { Length: > 0 } text) continue;
            if (Matches(text, id)) return member;
        }
        return null;
    }

    private bool Matches(string text, RecordId id)
    {
        if (text.Contains(':'))
        {
            try { return RecordId.Parse(text, Namespace ?? id.Namespace) == id; }
            catch (FormatException) { return false; }
        }
        return string.Equals(text, id.Name, StringComparison.OrdinalIgnoreCase) &&
               (Namespace == null || string.Equals(Namespace, id.Namespace, StringComparison.OrdinalIgnoreCase));
    }

    private string? StringOf(Member? member) =>
        member?.Value is Scalar && ToJsonNode(member.Value) is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    // Records are read ignoring case, so a path finds `Place` as well as `place`.
    private static Member? FindMember(Container obj, string name)
    {
        foreach (var member in obj.Members)
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase)) return member;
        return null;
    }

    private static Node? Navigate(Node node, List<(string? Name, int Index)> parts)
    {
        Node? current = node;
        foreach (var (name, index) in parts)
        {
            if (current is not Container c) return null;
            if (name != null) current = c.IsObject ? FindMember(c, name)?.Value : null;
            else current = !c.IsObject && index >= 0 && index < c.Members.Count ? c.Members[index].Value : null;
            if (current == null) return null;
        }
        return current;
    }

    private JsonNode? ToJsonNode(Node node) => JsonNode.Parse(Text.AsSpan(node.Start, node.End - node.Start).ToString(), documentOptions: DocumentOptions);

    // ---- Edits as text ----

    private readonly record struct Edit(int Start, int End, string Text);

    // Applies edits (against the current text, not overlapping) from the back, then parses again.
    private void Splice(params Edit[] edits)
    {
        Array.Sort(edits, (p, q) => q.Start.CompareTo(p.Start));
        var text = new StringBuilder(Text);
        foreach (var edit in edits)
        {
            text.Remove(edit.Start, edit.End - edit.Start);
            text.Insert(edit.Start, edit.Text);
        }
        Text = text.ToString();
        _root = new Parser(Text).ParseDocument();
    }

    private void Replace(Node old, JsonNode? value, bool inline) => Splice(new Edit(old.Start, old.End, Format(value, IndentOf(old.Start), inline)));

    // A container whose members share a line with its opening bracket is written on one line.
    private bool IsInline(Container c) => c.Members.Count > 0 && !StartsLine(c.Members[0].Start);

    private void AddMember(Container c, string? name, JsonNode? value)
    {
        string nl = Newline();
        string key = name != null ? JsonSerializer.Serialize(name, ScalarWriting) + ": " : "";
        if (c.Members.Count == 0)
        {
            string indent = IndentOf(c.Start);
            string child = indent + IndentUnit();
            string entry = nl + child + key + Format(value, child, false);
            if (Text.AsSpan(c.Start + 1, c.End - c.Start - 2).IsWhiteSpace()) Splice(new Edit(c.Start + 1, c.End - 1, entry + nl + indent));
            else Splice(new Edit(c.Start + 1, c.Start + 1, entry));   // only comments inside: the value goes before them
            return;
        }

        var last = c.Members[^1];
        if (StartsLine(last.Start))
        {
            string indent = IndentOf(last.Start);
            string entry = nl + indent + key + Format(value, indent, false);
            int after = last.Comma >= 0 ? last.Comma + 1 : last.Value.End;
            // After a comment that ends the last member's line, not before it: the comment is the member's.
            int anchor = SkipSpaces(after);
            if (At(anchor, "//")) anchor = LineEnd(anchor);
            if (anchor < Text.Length && Text[anchor] != '\n' && Text[anchor] != '\r') anchor = after;
            if (last.Comma >= 0) Splice(new Edit(anchor, anchor, entry + ","));
            // One edit when both go at the same place: Splice's sort is not stable, and two inserts at one
            // position could land the entry before the comma that separates it.
            else if (anchor == last.Value.End) Splice(new Edit(anchor, anchor, "," + entry));
            else Splice(new Edit(last.Value.End, last.Value.End, ","), new Edit(anchor, anchor, entry));
        }
        else
        {
            string entry = key + Format(value, IndentOf(c.Start), true);
            if (last.Comma >= 0) Splice(new Edit(last.Comma + 1, last.Comma + 1, " " + entry + ","));
            else Splice(new Edit(last.Value.End, last.Value.End, ", " + entry));
        }
    }

    private void RemoveMember(Container c, int index)
    {
        var member = c.Members[index];
        int start = member.Start;
        int end = member.Comma >= 0 ? member.Comma + 1 : member.Value.End;

        // The last member without a comma of its own: the comma before it goes with it.
        if (member.Comma < 0 && index > 0 && c.Members[index - 1] is { Comma: >= 0 } previous &&
            Text.AsSpan(previous.Comma + 1, start - previous.Comma - 1).IsWhiteSpace())
        {
            Splice(new Edit(previous.Comma, end, ""));
            return;
        }

        var edits = new List<Edit>();
        if (member.Comma < 0 && index > 0 && c.Members[index - 1].Comma >= 0)
            edits.Add(new Edit(c.Members[index - 1].Comma, c.Members[index - 1].Comma + 1, ""));

        int rest = SkipSpaces(end);
        if (At(rest, "//")) rest = LineEnd(rest);
        if (StartsLine(start) && (rest >= Text.Length || Text[rest] == '\n' || Text[rest] == '\r'))
        {
            // A member on lines of its own takes its lines with it.
            start = LineStart(start);
            end = rest < Text.Length && Text[rest] == '\r' ? rest + 1 : rest;
            if (end < Text.Length && Text[end] == '\n') end++;
        }
        else end = SkipSpaces(end);
        edits.Add(new Edit(start, end, ""));
        Splice(edits.ToArray());
    }

    // ---- Writing new values ----

    // `indent` is the indentation of the line the value starts on. Arrays of numbers and strings go on
    // one line while they are short; objects and other arrays are indented one step further per level,
    // unless `inline`.
    private string Format(JsonNode? value, string indent, bool inline)
    {
        switch (value)
        {
            case null: return "null";
            case JsonArray array:
            {
                if (array.Count == 0) return "[]";
                bool flat = true;
                foreach (var item in array) flat &= item is null or JsonValue;
                if (flat || inline)
                {
                    var parts = new List<string>();
                    foreach (var item in array) parts.Add(Format(item, indent, true));
                    string line = "[" + string.Join(", ", parts) + "]";
                    if (inline || line.Length <= 80) return line;
                }
                string nl = Newline(), child = indent + IndentUnit();
                var lines = new List<string>();
                foreach (var item in array) lines.Add(child + Format(item, child, false));
                return "[" + nl + string.Join("," + nl, lines) + nl + indent + "]";
            }
            case JsonObject obj:
            {
                if (obj.Count == 0) return "{}";
                if (inline)
                {
                    var parts = new List<string>();
                    foreach (var (name, item) in obj) parts.Add(JsonSerializer.Serialize(name, ScalarWriting) + ": " + Format(item, indent, true));
                    return "{ " + string.Join(", ", parts) + " }";
                }
                string nl = Newline(), child = indent + IndentUnit();
                var lines = new List<string>();
                foreach (var (name, item) in obj) lines.Add(child + JsonSerializer.Serialize(name, ScalarWriting) + ": " + Format(item, child, false));
                return "{" + nl + string.Join("," + nl, lines) + nl + indent + "}";
            }
            default:
                return value.ToJsonString(ScalarWriting);
        }
    }

    private string Newline() => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    // The file's step of indentation: a tab if it indents with tabs, else its smallest indent, else two
    // spaces (how the repository's record files are written).
    private string IndentUnit()
    {
        int smallest = int.MaxValue;
        for (int i = 0; i < Text.Length; i = LineEnd(i) + 1)
        {
            int j = i;
            while (j < Text.Length && Text[j] == ' ') j++;
            if (j < Text.Length && Text[j] == '\t' && j == i) return "\t";
            if (j > i && j < Text.Length && Text[j] != '\n' && Text[j] != '\r') smallest = Math.Min(smallest, j - i);
        }
        return new string(' ', smallest is > 0 and <= 8 ? smallest : 2);
    }

    private int LineStart(int position)
    {
        int i = position;
        while (i > 0 && Text[i - 1] != '\n') i--;
        return i;
    }

    // Where the line holding `position` ends: its '\r' or '\n', or the end of the text.
    private int LineEnd(int position)
    {
        int i = position;
        while (i < Text.Length && Text[i] != '\n' && Text[i] != '\r') i++;
        return i;
    }

    private bool StartsLine(int position) => Text.AsSpan(LineStart(position), position - LineStart(position)).IsWhiteSpace();

    private string IndentOf(int position)
    {
        int start = LineStart(position), i = start;
        while (i < Text.Length && (Text[i] == ' ' || Text[i] == '\t')) i++;
        return Text[start..i];
    }

    private int SkipSpaces(int position)
    {
        while (position < Text.Length && (Text[position] == ' ' || Text[position] == '\t')) position++;
        return position;
    }

    private bool At(int position, string what) => string.CompareOrdinal(Text, position, what, 0, what.Length) == 0;

    // ---- The parser: JSON with comments and trailing commas, keeping where every value is ----

    private abstract class Node
    {
        public int Start;
        public int End;      // one past the last character
    }

    private sealed class Scalar : Node { }

    private sealed class Container : Node
    {
        public bool IsObject;
        public readonly List<Member> Members = new();
    }

    private sealed class Member
    {
        public string? Name;     // null in an array
        public int Start;        // where its name starts, or its value in an array
        public Node Value = null!;
        public int Comma = -1;   // the comma after it, if it has one
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _at;

        public Parser(string text) => _text = text;

        // Null for a file with nothing but whitespace and comments in it.
        public Node? ParseDocument()
        {
            if (_text.Length > 0 && _text[0] == '﻿') _at = 1;
            SkipTrivia();
            if (_at >= _text.Length) return null;
            var root = ParseValue();
            SkipTrivia();
            if (_at < _text.Length) throw Error("unexpected text after the value");
            return root;
        }

        private Node ParseValue()
        {
            if (_at >= _text.Length) throw Error("a value was expected");
            char c = _text[_at];
            if (c == '{' || c == '[') return ParseContainer(c == '{');
            int start = _at;
            if (c == '"') { ReadString(); return new Scalar { Start = start, End = _at }; }
            while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] is '-' or '+' or '.')) _at++;
            if (_at == start) throw Error($"unexpected '{c}'");
            return new Scalar { Start = start, End = _at };
        }

        private Container ParseContainer(bool isObject)
        {
            var container = new Container { Start = _at, IsObject = isObject };
            char close = isObject ? '}' : ']';
            _at++;
            while (true)
            {
                SkipTrivia();
                if (_at >= _text.Length) throw Error($"'{close}' was expected");
                if (_text[_at] == close) break;
                var member = new Member { Start = _at };
                if (isObject)
                {
                    if (_text[_at] != '"') throw Error("a property name was expected");
                    member.Name = ReadString();
                    SkipTrivia();
                    if (_at >= _text.Length || _text[_at] != ':') throw Error("':' was expected");
                    _at++;
                    SkipTrivia();
                }
                member.Value = ParseValue();
                container.Members.Add(member);
                SkipTrivia();
                if (_at < _text.Length && _text[_at] == ',') { member.Comma = _at; _at++; continue; }
                SkipTrivia();
                if (_at >= _text.Length || _text[_at] != close) throw Error($"',' or '{close}' was expected");
                break;
            }
            _at++;
            container.End = _at;
            return container;
        }

        private string ReadString()
        {
            var value = new StringBuilder();
            _at++;
            while (true)
            {
                if (_at >= _text.Length) throw Error("the string does not end");
                char c = _text[_at++];
                if (c == '"') return value.ToString();
                if (c != '\\') { value.Append(c); continue; }
                if (_at >= _text.Length) throw Error("the string does not end");
                char e = _text[_at++];
                switch (e)
                {
                    case 'n': value.Append('\n'); break;
                    case 't': value.Append('\t'); break;
                    case 'r': value.Append('\r'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'u':
                        if (_at + 4 > _text.Length || !int.TryParse(_text.AsSpan(_at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            throw Error("a \\u escape needs four hex digits");
                        value.Append((char)code);
                        _at += 4;
                        break;
                    default: value.Append(e); break;   // \" \\ \/
                }
            }
        }

        private void SkipTrivia()
        {
            while (_at < _text.Length)
            {
                char c = _text[_at];
                if (char.IsWhiteSpace(c)) { _at++; continue; }
                if (c == '/' && _at + 1 < _text.Length && _text[_at + 1] == '/')
                {
                    while (_at < _text.Length && _text[_at] != '\n') _at++;
                    continue;
                }
                if (c == '/' && _at + 1 < _text.Length && _text[_at + 1] == '*')
                {
                    int close = _text.IndexOf("*/", _at + 2, StringComparison.Ordinal);
                    if (close < 0) throw Error("the comment does not end");
                    _at = close + 2;
                    continue;
                }
                return;
            }
        }

        private JsonException Error(string what)
        {
            int line = 1, column = 1;
            for (int i = 0; i < _at && i < _text.Length; i++)
            {
                if (_text[i] == '\n') { line++; column = 1; }
                else column++;
            }
            return new JsonException($"{line}:{column}: {what}", null, line - 1, column - 1);
        }
    }
}
