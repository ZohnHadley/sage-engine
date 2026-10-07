#nullable enable
#pragma warning disable SAGE0132 // the content report and RecordStore.Writes: data mods' experimental API, which this reads
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// One mod's (or the game's) part in a conflicted field (issue #401): who wrote it, how, what value it
// wrote there, and where.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model
public sealed class FieldContribution
{
    internal FieldContribution(string mount, bool isMod, RecordWriteOp op, string path, string value, string at, RecordId via)
    {
        Mount = mount; IsMod = isMod; Op = op; Path = path; Value = value; At = at; Via = via;
    }

    // The mod's id ("better_blades"), or the mount's name for what is not a mod ("village/content").
    public string Mount { get; }
    public bool IsMod { get; }
    public RecordWriteOp Op { get; }

    // The path this write named; it may be the conflict's path, above it or inside it.
    public string Path { get; }

    // What it wrote at the conflict's path, as JSON ("120", "\"Hilde\"", `{ "count": 3 }`); for an add,
    // what it added; for a disable, "(disabled the record)"; "" when the file can't be read back.
    public string Value { get; }

    // "mount:path:line:column" of the write.
    public string At { get; }

    // The base the record inherited this write through; empty when it is the record's own.
    public RecordId Via { get; }

    // This is the write that stands: the last of the winning mod's, in load order.
    public bool Wins { get; internal set; }

    public override string ToString() =>
        $"{(Wins ? "* " : "  ")}{Mount}: {Op.ToString().ToLowerInvariant()} {(Path.Length == 0 ? "(record)" : Path)} = {Value}  {At}" +
        (Via.IsEmpty ? "" : $" (via base {Via})");
}

// A field of the open record that two or more mods wrote (the content report's conflict, issue #401):
// every write to it in load order, each mod's value, and which one won.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model
public sealed class FieldConflict
{
    internal FieldConflict(ContentConflictKind kind, string path, string winner, string current, string line, IReadOnlyList<FieldContribution> contributions)
    {
        Kind = kind; Path = path; Winner = winner; Current = current; Line = line; Contributions = contributions;
    }

    public ContentConflictKind Kind { get; }

    // The value path, keyed where a list is (`parts.inventory.items[bunker:pistol].instance.condition`);
    // "" for the whole record (a disable against a patch).
    public string Path { get; }

    // The mod whose write stands.
    public string Winner { get; }

    // The value the record has there now, as JSON; "" when it has none (the field was removed).
    public string Current { get; }

    // The content report's line: "prefab village:trader name: better_blades, rival_trade; rival_trade won".
    public string Line { get; }

    // Every write to the path, in load order: the definition (the game's, a base's) first, then each patch.
    public IReadOnlyList<FieldContribution> Contributions { get; }
}

// The per-field conflict view of the Records panel (issue #401; docs/design/15 §10): for one record, the
// content report's conflicts about it, each with every write that reached the field — the value each
// mod wrote, read back from its file, and the winner. Built from `ContentReport` and `RecordStore.Writes`;
// the Records panel draws it and `ed_rec_conflicts` prints it.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model
public static class RecordConflicts
{
    public static IReadOnlyList<FieldConflict> Find(Engine engine, string type, RecordId id) =>
        Find(engine.Records, ContentReport.Build(engine.Records, engine.Vfs), type, id);

    // With a report already built (one report answers for every record).
    public static IReadOnlyList<FieldConflict> Find(RecordStore records, ContentReport report, string type, RecordId id)
    {
        var result = new List<FieldConflict>();
        var mine = report.Conflicts.Where(c => c.Kind != ContentConflictKind.Asset && c.Type == type && c.Id == id).ToList();
        if (mine.Count == 0) return result;
        var writes = records.Writes(type, id);
        var current = records.RawJson(type, id);

        foreach (var conflict in mine)
        {
            string path = conflict.Path;
            var touching = conflict.Kind == ContentConflictKind.Disabled
                ? writes.Where(w => w.Op != RecordWriteOp.Define && ContentReport.IsModMount(w.Mount)).ToList()
                : writes.Where(w => w.Op != RecordWriteOp.Disable && Overlaps(w.Path, path) &&
                                    !(w.Op == RecordWriteOp.Add && IsIntoEntryOf(path, w.Path))).ToList();

            var contributions = touching.Select(w => new FieldContribution(ContentReport.NameOf(w.Mount), ContentReport.IsModMount(w.Mount),
                w.Op, w.Path, ValueOf(w, path), w.At, w.Via)).ToList();
            for (int i = touching.Count - 1; i >= 0; i--)
                if (ReferenceEquals(touching[i].Mount, conflict.Winner)) { contributions[i].Wins = true; break; }

            string now = conflict.Kind == ContentConflictKind.Disabled
                ? (current == null ? "(disabled)" : "(kept)")
                : Find(current, path, patch: false)?.ToJsonString() ?? "";
            result.Add(new FieldConflict(conflict.Kind, path, ContentReport.NameOf(conflict.Winner), now, conflict.Line, contributions));
        }
        return result;
    }

    // The console's and the panel's text for one conflict: its line, then a line per write.
    public static IEnumerable<string> Lines(FieldConflict conflict)
    {
        yield return $"{(conflict.Path.Length == 0 ? "(record)" : conflict.Path)}: {conflict.Winner} won" +
                     (conflict.Current.Length > 0 ? $", now {conflict.Current}" : "");
        foreach (var c in conflict.Contributions) yield return "  " + c;
    }

    // What `write` wrote at `path`: the deeper of the two paths, looked up in the record as its file has
    // it (a definition's "parts" holds the conflict's "parts.inventory.items[x].count"; a patch's
    // "items[x].count" is inside a conflict about "items[x]").
    private static string ValueOf(RecordWrite write, string path)
    {
        if (write.Op == RecordWriteOp.Disable) return "(disabled the record)";
        var written = write.Written();
        if (written == null) return "";
        string at = write.Path.Length >= path.Length ? write.Path : path;
        if (write.Op == RecordWriteOp.Remove && at.EndsWith(']')) return "(removed)";
        if (at.Length == 0) return written.ToJsonString();
        return Find(written, at, patch: true)?.ToJsonString() ?? (write.Op == RecordWriteOp.Define ? "(not written here)" : "");
    }

    // The node at a keyed path (`parts.inventory.items[village:lantern].count`) in a record's JSON. A
    // name is matched ignoring case, as the loader does; in a patch, `items+` / `items-` answer for
    // `items`. A key finds the entry of a list one of whose values is the key, a bare id counting as any
    // namespace's ("lantern" is `village:lantern`'s entry in the village's own file).
    internal static JsonNode? Find(JsonNode? node, string path, bool patch)
    {
        int i = 0;
        while (node != null && i < path.Length)
        {
            if (path[i] == '.') { i++; continue; }
            if (path[i] == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0) return null;
                node = Entry(node as JsonArray, path[(i + 1)..close]);
                i = close + 1;
                continue;
            }
            int end = i;
            while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
            node = Property(node as JsonObject, path[i..end], patch);
            i = end;
        }
        return node;
    }

    private static JsonNode? Property(JsonObject? obj, string name, bool patch)
    {
        if (obj == null) return null;
        foreach (var (key, value) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        if (!patch) return null;
        foreach (var (key, value) in obj)
            if (key.Length == name.Length + 1 && key[^1] is '+' or '-' && key.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static JsonNode? Entry(JsonArray? list, string key)
    {
        if (list == null) return null;
        if (int.TryParse(key, out int index) && index >= 0 && index < list.Count && list.All(e => e is not JsonObject)) return list[index];
        foreach (var entry in list)
        {
            if (entry is not JsonObject obj) continue;
            foreach (var (_, value) in obj)
            {
                if (value is not JsonValue scalar) continue;
                string text = scalar.ToJsonString().Trim('"').Trim();
                if (text.Length == 0) continue;
                if (string.Equals(text, key, StringComparison.OrdinalIgnoreCase) ||
                    (!text.Contains(':') && key.EndsWith(":" + text, StringComparison.OrdinalIgnoreCase)))
                    return entry;
            }
        }
        return null;
    }

    // As the content report judges overlap: one path is the other or inside it ("" is the whole record),
    // a list's keyed entry being inside the list.
    private static bool IsUnder(string path, string field) =>
        path.Length >= field.Length && field.Length > 0 &&
        string.Compare(path, 0, field, 0, field.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
        (path.Length == field.Length || path[field.Length] is '.' or '[');

    private static bool Overlaps(string a, string b) =>
        a.Length == 0 || b.Length == 0 || (a.Length <= b.Length ? IsUnder(b, a) : IsUnder(a, b));

    private static bool IsIntoEntryOf(string path, string add) =>
        add.Length > 0 && path.Length > add.Length + 1 && path[add.Length] == '[' &&
        string.Compare(path, 0, add, 0, add.Length, StringComparison.OrdinalIgnoreCase) == 0;
}
