#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// What the editor has open (docs/design/15 §3, TODO F28).
//
// **A document is a placements file**, not a level: brushes belong to TrenchBroom (15 §10a) and what a
// thing *is* belongs to its prefab, so what is left — and what nothing else could edit — is where things
// stand. Opening one spawns its placements into the world; saving reads the world back out.
//
// There is no command log yet, so "dirty" is set by whatever changed something and cleared by a save
// (undo/redo is F30, and the log is where it will go).
//
// It lives in the **engine** rather than in the editor because it is records and files with no screen in
// it — the same rule that puts `Panel` here and `PanelView` in the client (13 §3). What that buys is
// tests: opening, saving and reopening a document is checked headlessly, which is not something an
// editor window could be.
public sealed class EditorDocument
{
    private readonly Engine _engine;
    private JsonObject? _saved;   // the document as last opened or saved: a save writes what changed since

    public EditorDocument(Engine engine) => _engine = engine;

    public RecordId Id { get; private set; }
    public string Path { get; private set; } = "";      // where a save will write, "" for a new document
    public bool Dirty { get; private set; }
    public bool IsOpen => !Id.IsEmpty;

    public string Title => IsOpen ? $"{Id}{(Dirty ? " *" : "")}" : "(no document)";

    public event Action? Changed;

    public void Touch()
    {
        if (Dirty) return;
        Dirty = true;
        Changed?.Invoke();
    }

    // A document with nothing in it, belonging to the game that is loaded. Saving it writes a new file.
    public void New(World world, string namespaceId)
    {
        Close(world);
        Id = new RecordId(namespaceId, "untitled");
        Path = "";
        Dirty = true;
        Log.Info(LogCat.Editor, $"New document '{Id}'");
        Changed?.Invoke();
    }

    public bool Open(World world, RecordId id)
    {
        if (!_engine.Records.TryGet(id, out PlacementsRecord record))
        {
            Log.Error(LogCat.Editor, $"No placements record '{id}' to open");
            return false;
        }

        Close(world);
        // A scene may have loaded this document already (issue #29): opening it takes those over rather
        // than placing a second copy of each.
        world.ClearPlacements(id);
        Id = id;
        Path = FileFor(id);
        world.SpawnPlacements(id, record);
        _saved = Written(world.ReadPlacements(id));
        Dirty = false;
        Log.Info(LogCat.Editor, $"Opened '{id}' ({record.Place.Count} placement(s)) from {(Path.Length > 0 ? Path : "memory")}");
        Changed?.Invoke();
        return true;
    }

    public void Close(World world)
    {
        if (IsOpen) world.ClearPlacements(Id);
        Id = default;
        Path = "";
        _saved = null;
        Dirty = false;
        Changed?.Invoke();
    }

    // Writes the world back out into the file a game will load, touching only this document's record
    // and, within it, only what changed.
    public bool Save(World world)
    {
        if (!IsOpen) return false;

        string path = Path.Length > 0 ? Path : FileFor(Id);
        if (path.Length == 0)
        {
            Log.Error(LogCat.Editor, $"Nowhere to save '{Id}': no mount for namespace '{Id.Namespace}'");
            return false;
        }

        var record = world.ReadPlacements(Id);
        var file = Written(record);

        try
        {
            // Into the file as it stands (issue #218): only what changed since the document was opened
            // or last saved is written, so comments, key order, the other records in the file and the
            // fields it leaves at their defaults stay as a person wrote them. A document the file does
            // not hold yet (a new one) is added to it, or makes it. JsonFileEdit writes the file whole
            // and moves it into place, so an interrupted save cannot leave half a file behind.
            var edit = JsonFileEdit.Open(path, _engine.Records.Json);
            if (_saved == null || !edit.PatchRecord("placements", Id, _saved, file)) edit.SetRecord("placements", Id, file);
            edit.Save(path);
            _saved = file;
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

        Path = path;
        Dirty = false;
        Log.Info(LogCat.Editor, $"Saved '{Id}' ({record.Place.Count} placement(s)) to {path}");
        Changed?.Invoke();
        return true;
    }

    // The document as its file says it, in the record store's dialect (its converters, camel case,
    // enums as strings): what a save compares with the last one. The frame (issue #29) only when it has
    // one: most documents are absolute metres.
    private JsonObject Written(PlacementsRecord record)
    {
        var dialect = new JsonFileEdit("", _engine.Records.Json);
        var file = new JsonObject { ["type"] = "placements", ["id"] = Id.Name };
        if (record.Origin != default) file["origin"] = dialect.ToNode(record.Origin);
        if (record.RelativeTo != PlacementFrame.World) file["relativeTo"] = dialect.ToNode(record.RelativeTo);
        file["place"] = dialect.ToNode(record.Place);
        return file;
    }

    // Documents this game has, for the Open menu.
    public IEnumerable<RecordId> Available() => _engine.Records.Ids("placements");

    // Where this document lives on disk.
    //
    // **Where it came from, if it came from anywhere.** A record may be defined in a file named after
    // something else entirely — the Sandbox keeps `sandbox:yard` in `placements.json` — and saving it to
    // `yard.json` instead leaves *two* definitions of one id for the loader to trip over. Only a
    // document that has never been written picks its own name.
    private string FileFor(RecordId id)
    {
        if (_engine.Records.FileOf("placements", id) is { } existing)
            foreach (var mount in _engine.Vfs.Mounts)
                if (mount.PhysicalPath(VirtualPath.Parse(existing)) is { } path)
                    return path;

        foreach (var mount in _engine.Vfs.Mounts)
        {
            if (!string.Equals(mount.RecordNamespace, id.Namespace, StringComparison.OrdinalIgnoreCase)) continue;
            // Where it *would* be written: `PhysicalPath` resolves files that exist, and a new document
            // is exactly a file that does not.
            string? file = mount.WritablePath(VirtualPath.Parse($"data/{id.Name}.json"));
            if (file != null) return file;
        }
        return "";
    }
}
