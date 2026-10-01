#nullable enable
#pragma warning disable SAGE0132 // provenance is RecordStore.Writes: data mods' experimental API, which this reads
using System;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// One record open in the record browser (issue #224; docs/design/15 §10).
//
// **It edits the record's JSON, not the object the game holds.** A record is what a file says, and the
// file is what a save has to change; the built object (`RecordStore.TryGet`) has the defaults filled in
// and the bases merged, none of which a person wrote. So the document opens `RecordStore.RawJson` (the
// definition and every patch merged, comments gone), keeps a working copy that commands change, and a save
// writes what differs between the two.
//
// **Where a save goes** (Save): into the file the record is defined in when that is a file of the game's
// own mount, with `JsonFileEdit.PatchRecord`, so comments, key order and the other records in the file
// stay as a person wrote them. A record defined somewhere the game does not own (the engine, a kit,
// another mod) is changed the way a mod changes it, with a `"patch": true` record in the game's own
// `data/patches/` folder (docs/MODDING.md §5), and the log says so. Either way the records are then
// reloaded (the hot reload path) so the game sees the change, and what the load made of it becomes the
// saved copy.
//
// Its undo history is its own (`History`), apart from the placements document's.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class RecordDocument
{
    private JsonObject _original;

    private RecordDocument(Engine engine, string type, RecordId id, JsonObject raw)
    {
        Engine = engine;
        Type = type;
        Id = id;
        _original = raw;
        Working = (JsonObject)raw.DeepClone();
        History = new CommandLog();
        History.Changed += () => Changed?.Invoke();
        Meta = engine.Records.TypeOf(type) is { } clr ? Metadata.Of(clr) : null;
    }

    // Opens a loaded record; null when there is no such record (and says why).
    public static RecordDocument? Open(Engine engine, string type, RecordId id)
    {
        if (engine.Records.RawJson(type, id) is not { } raw)
        {
            Log.Error(LogCat.Editor, $"No {type} record '{id}' to open");
            return null;
        }
        return new RecordDocument(engine, type, id, raw);
    }

    public Engine Engine { get; }
    public string Type { get; }
    public RecordId Id { get; }
    public CommandLog History { get; }

    // The record as it was opened or last saved: read-only, to compare with.
    public JsonObject Original => _original;

    // The working copy: read it freely, change it only through commands.
    public JsonObject Working { get; }

    public bool Dirty => History.Dirty;
    public string Title => $"{Type} {Id}{(Dirty ? " *" : "")}";

    // The record's declared fields, when the type has metadata (the form's ranges, tooltips, choices).
    public TypeMetadata? Meta { get; }

    // A command done, undone or redone, or a save.
    public event Action? Changed;

    // The raw JSON as it reads now: what the read-only view shows.
    public string RawText => Working.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    // ---- Editing --------------------------------------------------------------------------------------

    // Sets the value at `path` (see RecordPath). False, with a warning, when the path cannot be made.
    public bool Set(string path, JsonNode? value)
    {
        if (RecordPath.Parse(path) is not { Count: > 0 } parts)
        {
            Log.Warn(LogCat.Editor, $"'{path}' is not a path into {Type} {Id} (stats.health, items[2])");
            return false;
        }
        var probe = (JsonObject)Working.DeepClone();
        if (!RecordPath.Set(probe, parts, value?.DeepClone(), out _, out _))
        {
            Log.Warn(LogCat.Editor, $"Cannot set '{path}' in {Type} {Id}: something in the path is not an object or an array");
            return false;
        }
        History.Execute(new SetRecordValue(this, path, value?.DeepClone()));
        return true;
    }

    public JsonNode? Get(string path) =>
        RecordPath.Parse(path) is { } parts ? RecordPath.Get(Working, parts) : null;

    // Whether the field at `path` differs from the record as opened or last saved.
    public bool IsEdited(string path) =>
        RecordPath.Parse(path) is { } parts && !JsonNode.DeepEquals(RecordPath.Get(Working, parts), RecordPath.Get(_original, parts));

    // The path of a child of the value at `parent` ("" for the record): a name that has a dot or a
    // bracket in it is quoted, as RecordPath reads one.
    public static string ChildPath(string parent, string name)
    {
        string step = name.Contains('.') || name.Contains('[') || name.Contains(']') ? $"['{name}']" : name;
        return parent.Length == 0 ? step : step[0] == '[' ? parent + step : parent + "." + step;
    }

    public bool Undo() => History.Undo();
    public bool Redo() => History.Redo();

    // A console value: a JSON literal (`120`, `true`, `"Bob"`, `[1,2,3]`, `{"a":1}`), and anything that is
    // not one (`Bob`) is a string. The inspector's value parser by ValueKind (#223) is for typed fields;
    // a record's JSON is untyped here, so a literal says what it is.
    public static JsonNode? ParseValue(string text)
    {
        text = text.Trim();
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip };
        try { return JsonNode.Parse(text, documentOptions: options); }
        catch (JsonException) { }
        // The console takes double quotes off an argument, so `["a", "b"]` arrives as `[a, b]`: in a list or
        // an object, a bare word is a string.
        if (text.StartsWith('[') || text.StartsWith('{'))
        {
            string quoted = BareWord.Replace(text, m => m.Value is "true" or "false" or "null" ? m.Value : $"\"{m.Value}\"");
            try { return JsonNode.Parse(quoted, documentOptions: options); }
            catch (JsonException) { }
        }
        return JsonValue.Create(text);
    }

    private static readonly System.Text.RegularExpressions.Regex BareWord = new(@"(?<![\w""])[A-Za-z_][\w./-]*(?::[A-Za-z_][\w./-]*)?(?![\w""])");

    // ---- What a field is, and where it came from ---------------------------------------------------

    // The metadata of the field at `path`, by walking the type's declared fields: a name inside an Object,
    // a name inside a Map (its value), an index inside a List (its item). Null when it is not declared.
    public FieldMetadata? MetaAt(string path)
    {
        if (Meta == null || RecordPath.Parse(path) is not { Count: > 0 } parts) return null;
        FieldMetadata? field = null;
        foreach (var part in parts)
        {
            if (field == null || field.Kind == ValueKind.Object)
            {
                if (part.Name == null) return null;
                var fields = field == null ? Meta.Fields : field.Fields;
                field = fields.FirstOrDefault(f => string.Equals(f.JsonName, part.Name, StringComparison.OrdinalIgnoreCase));
            }
            else if (field.Kind is ValueKind.Map or ValueKind.List) field = field.Item;
            else return null;
            if (field == null) return null;
        }
        return field;
    }

    // Which file wrote the field at `path` (the last write to it or into it, in load order), as
    // `RecordStore.Writes` has it: "set stats.health  game:data/items.json:12:9 (via base x)"; null when
    // nothing wrote it (the type's default, or a field added here and not saved yet).
    public string? Provenance(string path)
    {
        string field = ProvenancePath(path);
        var writes = Engine.Records.Writes(Type, Id);
        for (int i = writes.Count - 1; i >= 0; i--)
        {
            if (writes[i].Op == RecordWriteOp.Disable || !Overlaps(writes[i].Path, field)) continue;
            return writes[i].ToString();
        }
        return null;
    }

    // The files that wrote the record: where it is defined, then each that patched it.
    public IReadOnlyList<string> Sources() =>
        Engine.Records.Writes(Type, Id).Select(w => w.File).Distinct(StringComparer.Ordinal).ToList();

    // The path as the loader writes one: names only, ending where the first index starts ("items[2].x" is
    // "items": a patch writes a list whole).
    private static string ProvenancePath(string path)
    {
        var names = new List<string>();
        foreach (var part in RecordPath.Parse(path) ?? new List<RecordPath.Segment>())
        {
            if (part.Name == null) break;
            names.Add(part.Name);
        }
        return string.Join('.', names);
    }

    private static bool Overlaps(string a, string b) =>
        a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a + ".", StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + ".", StringComparison.OrdinalIgnoreCase);

    // ---- Saving ---------------------------------------------------------------------------------------

    // Where the last save wrote, "" before one.
    public string SavedTo { get; private set; } = "";

    // True when a save would be a patch file rather than a change in the record's own.
    public bool SavesAsPatch => !CanSaveInPlace(out _);

    public bool Save()
    {
        if (!Dirty) { Log.Info(LogCat.Editor, $"{Type} {Id}: nothing to save"); return true; }
        try
        {
            string path = CanSaveInPlace(out string inPlace) ? SaveInPlace(inPlace) : SavePatch();
            if (path.Length == 0) return false;
            SavedTo = path;
        }
        catch (JsonException ex)
        {
            Log.Error(LogCat.Editor, $"Could not save {Type} {Id}: the file is not a record file ({ex.Message})");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(LogCat.Editor, $"Could not save {Type} {Id}: {ex.Message}");
            return false;
        }

        // The hot reload path: the game's records are read again, so what is saved is what the game holds.
        Engine.Records.Reload();
        if (Engine.Records.RawJson(Type, Id) is { } reloaded)
        {
            if (!JsonNode.DeepEquals(reloaded, Working))
                Log.Warn(LogCat.Editor, $"{Type} {Id} reads differently after the reload: a later file patches what was saved (see Provenance)");
            _original = reloaded;
        }
        else _original = (JsonObject)Working.DeepClone();
        History.MarkSaved();
        return true;
    }

    // The game's own folder: the last folder mount that is neither the engine's nor a mod's (a mod is
    // mounted after the game, a kit's content before it and not a folder). Where a file new to the game goes.
    internal static IMount? GameMount(Engine engine)
    {
        IMount? game = null;
        foreach (var mount in engine.Vfs.Mounts)
            if (mount.RecordNamespace != "sage" && mount is FolderMount && !mount.Name.StartsWith(ModManager.MountPrefix, StringComparison.Ordinal))
                game = mount;
        return game;
    }

    // The record's file, when it is one of the game's own and can be written.
    private bool CanSaveInPlace(out string path)
    {
        path = "";
        var mount = Engine.Records.MountOf(Type, Id);
        var game = GameMount(Engine);
        if (mount == null || game == null || Engine.Records.FileOf(Type, Id) is not { } file) return false;
        if (!string.Equals(mount.RecordNamespace, game.RecordNamespace, StringComparison.OrdinalIgnoreCase)) return false;
        var virtualPath = VirtualPath.Parse(file);
        if (mount.WritablePath(virtualPath) == null || mount.PhysicalPath(virtualPath) is not { } physical) return false;
        path = physical;
        return true;
    }

    private JsonObject WithMeta(JsonObject fields)
    {
        var record = new JsonObject { ["type"] = Type, ["id"] = Id.Name };
        foreach (var (key, value) in fields) record[key] = value?.DeepClone();
        return record;
    }

    private string SaveInPlace(string path)
    {
        var edit = JsonFileEdit.Open(path, Engine.Records.Json);
        if (!edit.PatchRecord(Type, Id, WithMeta(_original), WithMeta(Working)))
        {
            Log.Error(LogCat.Editor, $"Could not save {Type} {Id}: {path} no longer holds the record");
            return "";
        }
        edit.Save(path);
        Log.Info(LogCat.Editor, $"Saved {Type} {Id} into {path}");
        return path;
    }

    // The record is not the game's to change in place, so the change is a patch of it in the game's own data.
    private string SavePatch()
    {
        var game = GameMount(Engine);
        if (game == null)
        {
            Log.Error(LogCat.Editor, $"Nowhere to save {Type} {Id}: it is not defined in the game's own files and the game has no folder to patch it from");
            return "";
        }

        var changes = new List<(List<RecordPath.Segment> Path, JsonNode? Value)>();
        var removed = new List<string>();
        Diff(_original, Working, new List<RecordPath.Segment>(), changes, removed);
        foreach (string gone in removed)
            Log.Warn(LogCat.Editor, $"'{gone}' was removed, and a patch cannot remove a field (docs/MODDING.md §5): it stays as it is");
        if (changes.Count == 0)
        {
            Log.Warn(LogCat.Editor, $"{Type} {Id}: nothing a patch can say changed");
            return "";
        }

        string virtualFile = PatchFileOf(game) ?? $"data/patches/{Type}_{Id.Namespace}_{Id.Name}.json";
        string? path = game.WritablePath(VirtualPath.Parse(virtualFile));
        if (path == null)
        {
            Log.Error(LogCat.Editor, $"Nowhere to save the patch of {Type} {Id}: {game.Name} cannot be written to");
            return "";
        }

        var edit = JsonFileEdit.Open(path, Engine.Records.Json);
        if (edit.Contains(Type, Id))
        {
            // A patch of this record is already there (an earlier save): its other lines stay as they are.
            foreach (var (changePath, value) in changes) edit.Set(Type, Id, RecordPath.Format(changePath), value);
        }
        else
        {
            var patch = new JsonObject
            {
                ["type"] = Type,
                ["id"] = string.Equals(Id.Namespace, game.RecordNamespace, StringComparison.OrdinalIgnoreCase) ? Id.Name : Id.ToString(),
                ["patch"] = true,
            };
            foreach (var (changePath, value) in changes) RecordPath.Set(patch, changePath, value?.DeepClone(), out _, out _);
            edit.AddRecord(patch);
        }
        edit.Save(path);
        Log.Info(LogCat.Editor, $"{Type} {Id} is defined in {Engine.Records.FileOf(Type, Id) ?? "another mount"}, which this game does not own: " +
                                $"saved the change as a patch in {path}");
        return path;
    }

    // A file of the game's own that already patches this record: the next save adds to it, not to a second one.
    private string? PatchFileOf(IMount game)
    {
        foreach (var write in Engine.Records.Writes(Type, Id))
        {
            if (write.Op == RecordWriteOp.Define || write.Mount != game) continue;
            int colon = write.File.IndexOf(':');
            if (colon >= 0) return write.File[(colon + 1)..];
        }
        return null;
    }

    // What `after` says that `before` does not, as a patch says it: objects merge, so a changed leaf is one
    // line, and anything else (a list, a value of another kind) is replaced whole.
    private static void Diff(JsonNode? before, JsonNode? after, List<RecordPath.Segment> path,
                             List<(List<RecordPath.Segment> Path, JsonNode? Value)> changes, List<string> removed)
    {
        if (before is JsonObject a && after is JsonObject b)
        {
            foreach (var (key, value) in b)
            {
                var found = a.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
                var next = new List<RecordPath.Segment>(path) { new(found.Key ?? key, 0) };
                if (found.Key == null) changes.Add((next, value?.DeepClone()));
                else Diff(found.Value, value, next, changes, removed);
            }
            foreach (var (key, _) in a)
                if (!b.Any(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)))
                    removed.Add(RecordPath.Format(new List<RecordPath.Segment>(path) { new(key, 0) }));
            return;
        }
        if (!JsonNode.DeepEquals(before, after) && path.Count > 0) changes.Add((path, after?.DeepClone()));
    }
}

// Sets one value in a record document's JSON (`stats.health`, `items[2]`): the command of the record
// browser's form and of `ed_rec_set`. What it undoes is the value that was there, or the new value's
// absence (and the empty objects it made on the way).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class SetRecordValue : IEditorCommand
{
    private readonly RecordDocument _document;
    private JsonNode? _after;
    private JsonNode? _before;
    private bool _existed;
    private int _firstMissing = -1;

    public SetRecordValue(RecordDocument document, string path, JsonNode? value)
    {
        _document = document;
        Path = path;
        _after = value;
    }

    public string Path { get; }
    public JsonNode? Value => _after;

    public string Description => $"Set {_document.Id.Name}.{Path} {Show(_before)} → {Show(_after)}";

    private static string Show(JsonNode? node) => node == null ? "null" : node.ToJsonString();

    public void Do()
    {
        var parts = RecordPath.Parse(Path)!;
        RecordPath.Set(_document.Working, parts, _after?.DeepClone(), out var before, out _firstMissing);
        _existed = _firstMissing < 0;
        _before = before;
    }

    public void Undo()
    {
        var parts = RecordPath.Parse(Path)!;
        if (_existed) RecordPath.Set(_document.Working, parts, _before?.DeepClone(), out _, out _);
        else RecordPath.Remove(_document.Working, parts.Take(_firstMissing + 1).ToList());
    }

    // A drag on a slider is a command a frame: one edit.
    public bool TryMerge(IEditorCommand next)
    {
        if (next is not SetRecordValue other || other._document != _document || other.Path != Path) return false;
        _after = other._after;
        return true;
    }
}
