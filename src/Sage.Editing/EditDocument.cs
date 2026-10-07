#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
#pragma warning disable SAGE0132 // which file already patches the document is RecordStore.Writes: data mods' experimental API

namespace Sage.Editing;

// What the editor has open (issue #217; REDESIGN §4.6, docs/design/15 §3; it replaces F28's
// EditorDocument, which read the world back out to save).
//
// **A document is a placements record**, not a level: brushes belong to TrenchBroom (15 §10a) and what a
// thing *is* belongs to its prefab, so what is left — and what nothing else could edit — is where things
// stand, what they override and how they are wired.
//
// **The document is the source of truth; the world is derived from it.** Opening one copies the record
// and spawns it; every change is a command (IEditorCommand) that changes the copy and then re-spawns only
// the placements it touched, and saving writes the copy. Nothing is read back from the world, so what the
// game would load from the file and what the editor shows cannot drift apart: a re-spawn *is* a load of
// that one placement. The entity ↔ placement map is kept here, by placement (the object, which a command
// holds on to across undo and redo), so a tool that selected an entity finds the same placement after its
// entity was replaced (`Respawned` says which entity became which).
//
// One document per world: the world it was made for is the one it spawns into. Several documents open at
// once are several worlds, one per tab (EditorWorkspace, issue #375).
//
// **Where a save goes** (`Target`, issue #375): with no target, into the record's own file, or a new one in
// the mount of its namespace — the game developer's editor. With a target (a mod's folder, `ed_mod`), the
// target is the only folder the editor writes: a document defined there saves in place, a new one is made
// there, and one defined anywhere else (the shipped game's own level) is read-only and saves as a
// `"patch": true` record of it in the target's `data/patches/`, its whole placement list, which is how a mod
// changes a game's level (docs/MODDING.md §5: a patch replaces a list it names).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class EditDocument
{
    private readonly Dictionary<Placement, Entity> _entities = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Entity, Placement> _placements = new();
    private PlacementsRecord _record = new();
    private bool _neverSaved;      // a new document is unsaved work until it is first written
    private RecordId _newScene;    // a new level's scene, written beside the document on its first save
    private JsonObject? _saved;    // the document as last opened or saved: a save writes what changed since

    public EditDocument(World world, int historyCapacity = CommandLog.DefaultCapacity)
    {
        World = world;
        Engine = world.Engine ?? throw new ArgumentException("a document is opened into a world with an engine", nameof(world));
        History = new CommandLog(historyCapacity);
        History.Changed += () => Changed?.Invoke();
    }

    public Engine Engine { get; }
    public World World { get; }
    public CommandLog History { get; }

    public RecordId Id { get; private set; }
    public string Path { get; private set; } = "";      // where a save will write, "" before it knows

    // The one folder saves go into (issue #375): a mod's mount while modding, null otherwise (each record
    // back into its own file). Set by the workspace for every open document.
    public IMount? Target { get; set; }

    // True when the document is defined somewhere other than the target (the shipped game), so it is not the
    // target's to change in place and a save writes a patch of it there.
    public bool SavesAsPatch => IsOpen && Target != null && Engine.Records.MountOf("placements", Id) is { } home && !ReferenceEquals(home, Target);

    // Where the last save wrote: the document's file, or the target's patch of it; "" before a save.
    public string SavedTo { get; private set; } = "";
    public bool IsOpen => !Id.IsEmpty;
    public bool Dirty => IsOpen && (_neverSaved || History.Dirty);

    // The scene a new level made for this document (NewLevel); empty otherwise.
    public RecordId Scene { get; private set; }

    public string Title => IsOpen ? $"{Id}{(SavesAsPatch ? " (patch)" : "")}{(Dirty ? " *" : "")}" : "(no document)";

    // The document's own copy of the record: read it freely, change it only through commands.
    public PlacementsRecord Record => _record;
    public IReadOnlyList<Placement> Placements => _record.Place;

    // Opened, closed, saved, or a command done, undone or redone.
    public event Action? Changed;

    // A placement's entity was replaced (old, new; either may be null): a command changed it, or it was
    // added, removed, undone or redone. A selection follows its placement through this.
    public event Action<Entity, Entity>? Respawned;

    // ---- Opening and closing --------------------------------------------------------------------------

    public bool Open(RecordId id)
    {
        if (!Engine.Records.TryGet(id, out PlacementsRecord record))
        {
            Log.Error(LogCat.Editor, $"No placements record '{id}' to open");
            return false;
        }

        Close();
        // A scene may have loaded this document already (issue #29): opening it takes those over rather
        // than placing a second copy of each.
        World.ClearPlacements(id);
        Begin(id, Copy(record), FileFor(id));
        Log.Info(LogCat.Editor, $"Opened '{id}' ({record.Place.Count} placement(s)) from {(Path.Length > 0 ? Path : "memory")}");
        return true;
    }

    // A document with nothing in it, belonging to the game that is loaded (or named): saving it writes a
    // new file in that game's content.
    public void New(RecordId id = default)
    {
        Close();
        if (id.IsEmpty) id = new RecordId(NewNamespace, "untitled");
        Begin(id, new PlacementsRecord(), "");
        _saved = null;
        _neverSaved = true;
        Log.Info(LogCat.Editor, $"New document '{id}'");
        Changed?.Invoke();
    }

    // A new level: a scene record naming a new placements document (`<scene>_placements`), which is the
    // one opened. The first save writes both, each into a file of its own; after that the scene is the
    // game's to change (its maps, its player, its weather), and the editor writes only the document.
    public RecordId NewLevel(RecordId scene)
    {
        if (scene.IsEmpty) scene = new RecordId(NewNamespace, "untitled");
        var document = new RecordId(scene.Namespace, scene.Name + "_placements");
        New(document);
        Scene = _newScene = scene;
        Log.Info(LogCat.Editor, $"New level: scene '{scene}' placing '{document}'");
        Changed?.Invoke();
        return document;
    }

    public void Close()
    {
        if (IsOpen)
        {
            // Taken out as content withdrawn, not destroyed by the game: nothing is tombstoned or put to
            // sleep, so opening the document again places it afresh. The sweep after is for whatever this
            // document placed before it was opened here and was never in the map.
            foreach (var entity in _entities.Values) World.DespawnPlacement(Id, entity);
            World.ClearPlacements(Id);
        }
        _entities.Clear();
        _placements.Clear();
        _record = new PlacementsRecord();
        Id = default;
        Scene = _newScene = default;
        _saved = null;
        Path = SavedTo = "";
        _neverSaved = false;
        History.Clear();
        Changed?.Invoke();
    }

    private void Begin(RecordId id, PlacementsRecord record, string path)
    {
        Id = id;
        _record = record;
        Path = path;
        _neverSaved = false;
        for (int i = 0; i < record.Place.Count; i++) Map(record.Place[i], World.SpawnPlacement(id, record, i));
        World.FinishPlacements(id);
        _saved = PlacementsJson();
        History.Clear();
    }

    // Documents this game has, for the Open menu.
    public IEnumerable<RecordId> Available() => Engine.Records.Ids("placements");

    // ---- Commands -----------------------------------------------------------------------------------

    public bool Execute(IEditorCommand command)
    {
        if (!IsOpen)
        {
            Log.Warn(LogCat.Editor, $"'{command.Description}': no document is open");
            return false;
        }
        History.Execute(command);
        return true;
    }

    public bool Undo() => IsOpen && History.Undo();
    public bool Redo() => IsOpen && History.Redo();

    // ---- Placements and their entities --------------------------------------------------------------

    public Entity EntityOf(Placement placement) => _entities.TryGetValue(placement, out var entity) ? entity : default;

    public Placement? PlacementOf(Entity entity) =>
        !entity.IsNull && _placements.TryGetValue(entity, out var placement) ? placement : null;

    // A placement by its name, else its id (what a console command or a wire would say).
    public Placement? Find(string nameOrId)
    {
        foreach (var placement in _record.Place)
            if (string.Equals(placement.Name, nameOrId, StringComparison.OrdinalIgnoreCase)) return placement;
        foreach (var placement in _record.Place)
            if (string.Equals(placement.Id, nameOrId, StringComparison.OrdinalIgnoreCase)) return placement;
        return null;
    }

    public int IndexOf(Placement placement) => _record.Place.IndexOf(placement);

    // An id no other placement of this document has: the name, else the prefab's, made unique with a
    // number. A placement the editor adds gets one, so its identity in saves (4i-3) does not move when
    // the list is reordered or something before it is removed.
    public string UniqueId(string wanted)
    {
        string stem = Slug(wanted);
        if (stem.Length == 0) stem = "placement";
        string id = stem;
        for (int n = 2; _record.Place.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)); n++)
            id = $"{stem}_{n}";
        return id;
    }

    // What the commands do to the document (PlacementCommands.cs): change the list, then put the world
    // back in step with it.
    internal void Insert(int index, Placement placement)
    {
        _record.Place.Insert(index, placement);
        Respawn(Shifted(index + 1, also: index));
    }

    internal void RemoveAt(int index)
    {
        var placement = _record.Place[index];
        var old = Unmap(placement);
        if (!old.IsNull) World.DespawnPlacement(Id, old);
        _record.Place.RemoveAt(index);
        Respawn(Shifted(index, also: -1));
        if (!old.IsNull) Respawned?.Invoke(old, default);
    }

    internal void Respawn(Placement placement)
    {
        int index = IndexOf(placement);
        if (index >= 0) Respawn(new List<int> { index });
    }

    // `also`, and every placement from `first` on whose identity comes from its index (it has no `id`):
    // one added or removed before them moved them, and the ids a load would give them now are not the
    // ones they had. Leaving them would put two entities of one id in the world.
    private List<int> Shifted(int first, int also)
    {
        var touched = new List<int>();
        if (also >= 0) touched.Add(also);
        for (int i = first; i < _record.Place.Count; i++)
            if (string.IsNullOrWhiteSpace(_record.Place[i].Id)) touched.Add(i);
        return touched;
    }

    private void Respawn(List<int> touched)
    {
        // Every old entity out first, then the new ones in: a new entity may take an id an old one held.
        var old = new Entity[touched.Count];
        for (int n = 0; n < touched.Count; n++)
        {
            old[n] = Unmap(_record.Place[touched[n]]);
            if (!old[n].IsNull) World.DespawnPlacement(Id, old[n]);
        }
        for (int n = 0; n < touched.Count; n++)
        {
            var entity = World.SpawnPlacement(Id, _record, touched[n]);
            Map(_record.Place[touched[n]], entity);
            if (!old[n].IsNull || !entity.IsNull) Respawned?.Invoke(old[n], entity);
        }
    }

    private void Map(Placement placement, Entity entity)
    {
        _entities[placement] = entity;
        if (!entity.IsNull) _placements[entity] = placement;
    }

    private Entity Unmap(Placement placement)
    {
        if (!_entities.Remove(placement, out var entity)) return default;
        if (!entity.IsNull) _placements.Remove(entity);
        return World.IsAlive(entity) ? entity : default;
    }

    // ---- Saving ---------------------------------------------------------------------------------------

    // Writes the document to the file it came from (a new one: a file of its own in the game's content),
    // and a new level's scene beside it the first time.
    public bool Save()
    {
        if (!IsOpen) return false;
        if (SavesAsPatch) return SavePatch(Target!);
        if (Target != null && !Engine.Records.Exists("placements", Id) && !InTarget(Id))
        {
            Log.Error(LogCat.Editor, $"Nowhere to save '{Id}': only {Target.Name} is written to (ed_mod), and '{Id.Namespace}' is not its namespace " +
                                     $"('{Target.RecordNamespace}'): start the document as {Target.RecordNamespace}:{Id.Name}");
            return false;
        }

        string path = Path.Length > 0 ? Path : FileFor(Id);
        if (path.Length == 0)
        {
            Log.Error(LogCat.Editor, $"Nowhere to save '{Id}': no writable mount for namespace '{Id.Namespace}'");
            return false;
        }

        var current = PlacementsJson();
        string scenePath = "";
        if (!_newScene.IsEmpty)
        {
            scenePath = FileFor(_newScene, "scene");
            if (scenePath.Length == 0)
            {
                Log.Error(LogCat.Editor, $"Nowhere to save the scene '{_newScene}'");
                return false;
            }
        }

        try
        {
            WriteRecord(path, "placements", Id, _saved, current);
            if (scenePath.Length > 0 && !File.Exists(scenePath)) WriteRecord(scenePath, "scene", _newScene, null, SceneJson(_newScene, Id));
        }
        catch (JsonException ex)
        {
            Log.Error(LogCat.Editor, $"Could not save '{Id}' to {path}: it is not a record file ({ex.Message})");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(LogCat.Editor, $"Could not save '{Id}' to {path}: {ex.Message}");
            return false;
        }

        _saved = current;
        Path = SavedTo = path;
        _neverSaved = false;
        _newScene = default;
        Log.Info(LogCat.Editor, $"Saved '{Id}' ({_record.Place.Count} placement(s)) to {path}");
        History.MarkSaved();
        return true;
    }

    // The namespace a new document is made in: the target's while modding, else the game's.
    private string NewNamespace => Target?.RecordNamespace ?? GameNamespace(Engine);

    private bool InTarget(RecordId id) => Target != null && string.Equals(id.Namespace, Target.RecordNamespace, StringComparison.OrdinalIgnoreCase);

    // The document is defined somewhere the target is not (the shipped game, another mod): the change is a
    // patch of it in the target. The patch says the whole record — every placement, the frame — because a
    // patch replaces a list it names, and a placement list is one value; ids are written in full, since a bare
    // id in the target's file would mean the target's namespace (R11).
    private bool SavePatch(IMount target)
    {
        string virtualFile = PatchFileOf(target) ?? $"data/patches/placements_{Id.Namespace}_{Id.Name}.json";
        string? path = target.WritablePath(VirtualPath.Parse(virtualFile));
        if (path == null)
        {
            Log.Error(LogCat.Editor, $"Nowhere to save the patch of '{Id}': {target.Name} cannot be written to");
            return false;
        }

        var json = PlacementsJson();
        var dialect = new JsonFileEdit("", Engine.Records.Json);
        var patch = new JsonObject { ["type"] = "placements", ["id"] = Id.ToString(), ["patch"] = true };
        patch["origin"] = dialect.ToNode(_record.Origin);
        patch["relativeTo"] = dialect.ToNode(_record.RelativeTo);
        patch["place"] = json["place"]!.DeepClone();
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var edit = JsonFileEdit.Open(path, Engine.Records.Json);
            edit.SetRecord("placements", Id, patch);
            edit.Save(path);
        }
        catch (JsonException ex)
        {
            Log.Error(LogCat.Editor, $"Could not save '{Id}' to {path}: it is not a record file ({ex.Message})");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(LogCat.Editor, $"Could not save '{Id}' to {path}: {ex.Message}");
            return false;
        }

        // `Path` and the last-saved copy stay the document's own file's: the patch is where this target's saves
        // go, not the record's home, and the file itself was not written.
        SavedTo = path;
        _neverSaved = false;
        Log.Info(LogCat.Editor, $"'{Id}' is defined in {Engine.Records.MountOf("placements", Id)?.Name ?? "another mount"}, which {target.Name} " +
                                $"does not own: saved it ({_record.Place.Count} placement(s)) as a patch in {path}");
        History.MarkSaved();
        return true;
    }

    // A file of the target's that already patches the document: the next save changes it, not a second one.
    private string? PatchFileOf(IMount target)
    {
        foreach (var write in Engine.Records.Writes("placements", Id))
        {
            if (write.Op == RecordWriteOp.Define || write.Mount != target) continue;
            int colon = write.File.IndexOf(':');
            if (colon >= 0) return write.File[(colon + 1)..];
        }
        return null;
    }

    // **The one place a record reaches a file** (issue #218's JsonFileEdit): into the file as it stands,
    // writing only what changed between `before` (the record as last opened or saved) and `after`, so
    // comments, key order, the other records in the file and the fields it leaves at their defaults stay
    // as a person wrote them. With no `before` (a new document), or a file that does not hold the record
    // (yet), the record is set whole: added to the file, or making it. The file is written whole and moved
    // into place, so an interrupted save cannot leave half a file behind.
    private void WriteRecord(string path, string type, RecordId id, JsonObject? before, JsonObject after)
    {
        var edit = JsonFileEdit.Open(path, Engine.Records.Json);
        if (before == null || !edit.PatchRecord(type, id, before, after)) edit.SetRecord(type, id, after);
        edit.Save(path);
    }

    // The record as its file says it, in the record store's own dialect (JsonFileEdit.ToNode: its
    // converters, camel case, enums as strings): what a save compares with the last one. With default
    // options the first F28 save wrote `"Prefab"` and `{"X":518,...}`.
    private JsonObject PlacementsJson()
    {
        var dialect = new JsonFileEdit("", Engine.Records.Json);
        var file = new JsonObject { ["type"] = "placements", ["id"] = Id.Name };
        // The document's frame (issue #29), only when it has one: most documents are absolute metres.
        if (_record.Origin != default) file["origin"] = dialect.ToNode(_record.Origin);
        if (_record.RelativeTo != PlacementFrame.World) file["relativeTo"] = dialect.ToNode(_record.RelativeTo);
        var place = dialect.ToNode(_record.Place)!.AsArray();
        // What a placement leaves out is what it does not have: no id (one derived from its place), no wires,
        // no pitch or roll and a scale of 1 (#367), so a yaw-only placement is written as it always was.
        foreach (var placement in place.OfType<JsonObject>())
        {
            if (placement["pitch"] is JsonValue pitch && pitch.GetValue<float>() == 0f) placement.Remove("pitch");
            if (placement["roll"] is JsonValue roll && roll.GetValue<float>() == 0f) placement.Remove("roll");
            if (placement["scale"] is JsonNode scale && JsonNode.DeepEquals(scale, dialect.ToNode(System.Numerics.Vector3.One))) placement.Remove("scale");
            if ((string?)placement["id"] == "") placement.Remove("id");
            if (placement["outputs"] is JsonArray { Count: 0 }) placement.Remove("outputs");
        }
        file["place"] = place;
        return file;
    }

    private static JsonObject SceneJson(RecordId scene, RecordId document) => new()
    {
        ["type"] = "scene",
        ["id"] = scene.Name,
        ["placements"] = new JsonArray(document.Namespace == scene.Namespace ? document.Name : document.ToString()),
    };

    // Where a record lives on disk.
    //
    // **Where it came from, if it came from anywhere.** A record may be defined in a file named after
    // something else entirely — the Sandbox keeps `sandbox:yard` in `placements.json` — and saving it to
    // `yard.json` instead leaves *two* definitions of one id for the loader to trip over. Only a record
    // that has never been written picks its own name.
    private string FileFor(RecordId id, string type = "placements")
    {
        // In the mount that defined it: a later mount with a file of the same name (a mod's own
        // `data/placements.json`) is another file.
        if (Engine.Records.FileOf(type, id) is { } existing)
        {
            if (Engine.Records.MountOf(type, id)?.PhysicalPath(VirtualPath.Parse(existing)) is { } own) return own;
            foreach (var mount in Engine.Vfs.Mounts)
                if (mount.PhysicalPath(VirtualPath.Parse(existing)) is { } path)
                    return path;
        }

        foreach (var mount in Engine.Vfs.Mounts)
        {
            if (!string.Equals(mount.RecordNamespace, id.Namespace, StringComparison.OrdinalIgnoreCase)) continue;
            // Where it *would* be written: `PhysicalPath` resolves files that exist, and a new record is
            // exactly a file that does not.
            string? file = mount.WritablePath(VirtualPath.Parse($"data/{id.Name}.json"));
            if (file != null) return file;
        }
        return "";
    }

    // The namespace a new document belongs to: the game's, the first mount that is not the engine's.
    public static string GameNamespace(Engine engine)
    {
        foreach (var mount in engine.Vfs.Mounts)
            if (mount.RecordNamespace != "sage") return mount.RecordNamespace;
        return "sage";
    }

    // ---- Copies -------------------------------------------------------------------------------------

    // The document's own copy of the record: commands change it, and the record store's stays what the
    // file said until the file is saved and reloaded.
    private static PlacementsRecord Copy(PlacementsRecord record)
    {
        var copy = new PlacementsRecord { Origin = record.Origin, RelativeTo = record.RelativeTo };
        foreach (var placement in record.Place) copy.Place.Add(Copy(placement));
        return copy;
    }

    internal static Placement Copy(Placement placement) => new()
    {
        Prefab = placement.Prefab,
        At = placement.At,
        Yaw = placement.Yaw,
        Pitch = placement.Pitch,
        Roll = placement.Roll,
        Scale = placement.Scale,
        Name = placement.Name,
        Id = placement.Id,
        RelativeTo = placement.RelativeTo,
        Outputs = CopyOutputs(placement.Outputs),
        Overrides = placement.Overrides?.Clone(),
    };

    internal static List<Connection> CopyOutputs(IEnumerable<Connection>? outputs) =>
        outputs == null ? new List<Connection>() : outputs.Where(w => w != null).Select(Copy).ToList();

    internal static Connection Copy(Connection wire) => new()
    {
        Output = wire.Output,
        Target = wire.Target,
        Input = wire.Input,
        Parameter = wire.Parameter,
        Delay = wire.Delay,
        Times = wire.Times,
        Requires = wire.Requires,
    };

    private static string Slug(string text)
    {
        var chars = text.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars).Trim('_');
    }
}
