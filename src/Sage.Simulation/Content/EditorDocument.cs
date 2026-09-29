#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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
        Dirty = false;
        Changed?.Invoke();
    }

    // Writes the world back out as the file a game will load. The document is one record in a file of
    // its own, which keeps a save from touching whatever else a game keeps beside it.
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
        var file = new Dictionary<string, object?>
        {
            ["type"] = "placements",
            ["id"] = Id.Name,
        };
        // The document's frame (issue #29), only when it has one: most documents are absolute metres.
        if (record.Origin != default) file["origin"] = record.Origin;
        if (record.RelativeTo != PlacementFrame.World) file["relativeTo"] = record.RelativeTo;
        file["place"] = record.Place;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // The record store's own options, not fresh ones: they carry the field naming and the
            // Vector3 converter that make this a *record* rather than a similar-looking object. Written
            // with defaults, the first save produced `"Prefab"` and `{"X":518,...}`, which the loader
            // that has to read it back would not recognise.
            // Camel case for writing, because that is how every other record file in the repository
            // reads. The loader does not care — its options are case-insensitive — but a person editing
            // the file afterwards does.
            var options = new JsonSerializerOptions(_engine.Records.Json)
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                // A placement's own `relativeTo` is optional: left out, it is the document's.
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            };
            string json = JsonSerializer.Serialize(new[] { file }, options);

            // Written whole and moved into place, so an interrupted save cannot leave a half-file that
            // the record loader will refuse on the next start (09 §3.3 does the same for saves).
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
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
