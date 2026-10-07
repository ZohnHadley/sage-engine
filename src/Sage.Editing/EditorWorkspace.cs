#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Editing;

// Several documents open at once (issue #375; docs/design/15 §3): the editor's tabs.
//
// **A tab is a document and the world it spawns into.** A document is the source of truth for one world
// (EditDocument), so two documents side by side are two edit worlds, each placed with its own scene and each
// with its own undo history: a level of the shipped game and a mod's level never share a space, a selection
// or an undo. The workspace makes a tab's world when it opens one (`Engine.CreateEditWorld`, in the scene
// that names the document) and destroys it when the tab closes; the first tab is the host's edit world,
// which it did not make and so never destroys: closing that tab closes its document and leaves it empty.
//
// **One tab is active**: the one the editor's commands (`doc_*`, `ed_undo`, the tools) act on, whose world
// has the screen. Switching changes nothing in either document.
//
// **Where saves go** (`Target`): null, each document into its own file (the game developer's editor); a
// mod's folder while modding (`ed_mod <id>`), the only folder the editor then writes, so the shipped game's
// own levels are read-only and save as a patch in the mod (EditDocument.Target). Every tab has the same target.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class EditorWorkspace
{
    private readonly List<EditDocument> _documents = new();
    private readonly List<bool> _ownsWorld = new();   // per tab: the workspace made its world, and destroys it
    private int _active = -1;
    private int _made;   // worlds made so far, for their names

    public EditorWorkspace(Engine engine)
    {
        Engine = engine;
    }

    public Engine Engine { get; }

    // The open tabs, in order; the active one's document is `Active`.
    public IReadOnlyList<EditDocument> Documents => _documents;
    public int Count => _documents.Count;
    public int ActiveIndex => _active;
    public EditDocument? Active => _active >= 0 ? _documents[_active] : null;

    // The one folder saves go into, null for each record's own file (see above).
    public IMount? Target { get; private set; }

    // A tab opened, closed or switched to, or the target changed.
    public event Action? Changed;

    // The active tab is another document (old, new; either may be null): whoever draws or ticks the editor's
    // world follows it.
    public event Action<EditDocument?, EditDocument?>? ActiveChanged;

    // ---- Tabs -----------------------------------------------------------------------------------------

    // A tab over a world the caller made (the host's edit world): its document, made here, is returned. The
    // first tab added becomes the active one.
    public EditDocument Add(World world)
    {
        var document = new EditDocument(world) { Target = Target };
        _documents.Add(document);
        _ownsWorld.Add(false);
        if (_active < 0) Switch(_documents.Count - 1);
        else Changed?.Invoke();
        return document;
    }

    // The tab that has `id` open, made active; else a new tab, in an edit world of its own placed with the
    // scene that names the document (the start scene when none does), and that document opened in it. An
    // active tab with nothing open in the same scene is used rather than making another world. Null (and a
    // log line) when there is no such placements document.
    public EditDocument? Open(RecordId id)
    {
        if (IndexOf(id) is int open and >= 0)
        {
            Activate(open);
            return _documents[open];
        }
        if (!Engine.Records.Exists("placements", id))
        {
            Log.Error(LogCat.Editor, $"No placements record '{id}' to open");
            return null;
        }

        EditTarget.TryResolve(Engine.Records, Engine.Scenes.Start, id.ToString(), out var target, out _);
        if (Active is { IsOpen: false } empty && Scenes.Current(empty.World) == target.Scene)
        {
            empty.Open(id);
            Changed?.Invoke();
            return empty;
        }

        var document = NewTab(target.Scene);
        if (!document.Open(id))
        {
            Close(_documents.Count - 1, force: true);
            return null;
        }
        Changed?.Invoke();
        return document;
    }

    // A new tab with an empty document in it (`id`, else "untitled" in the target's namespace or the game's),
    // in an edit world of the start scene.
    public EditDocument New(RecordId id = default)
    {
        var document = NewTab(default);
        document.New(id);
        Changed?.Invoke();
        return document;
    }

    private EditDocument NewTab(RecordId scene)
    {
        var world = Engine.CreateEditWorld($"edit {++_made + 1}", scene);
        var document = new EditDocument(world) { Target = Target };
        _documents.Add(document);
        _ownsWorld.Add(true);
        Switch(_documents.Count - 1);
        return document;
    }

    public bool Activate(int index)
    {
        if (index < 0 || index >= _documents.Count) return false;
        if (index != _active) Switch(index);
        return true;
    }

    public bool Activate(EditDocument document) => Activate(_documents.FindIndex(d => ReferenceEquals(d, document)));

    // The tab whose document is `id`, -1 when none is.
    public int IndexOf(RecordId id) => _documents.FindIndex(d => d.IsOpen && d.Id == id);

    // Closes a tab: its document is closed, and its world destroyed when the workspace made it. A tab over the
    // host's world stays, empty. A document with unsaved changes is kept open unless `force`: false, and the
    // log says so.
    public bool Close(int index, bool force = false)
    {
        if (index < 0 || index >= _documents.Count) return false;
        var document = _documents[index];
        if (document.Dirty && !force)
        {
            Log.Warn(LogCat.Editor, $"'{document.Title}' has unsaved changes: save it (doc_save), or close it anyway (ed_tab_close {index + 1} !)");
            return false;
        }

        if (!_ownsWorld[index])
        {
            document.Close();
            Changed?.Invoke();
            return true;
        }

        var old = Active;
        document.Close();
        _documents.RemoveAt(index);
        _ownsWorld.RemoveAt(index);
        if (index < _active || _active >= _documents.Count) _active--;
        var now = Active;
        // The new active world is the screen's before the old one goes, so nothing draws a destroyed world.
        if (!ReferenceEquals(old, now)) ActiveChanged?.Invoke(old, now);
        if (Engine.Worlds.Contains(document.World)) Engine.DestroyWorld(document.World);
        Changed?.Invoke();
        return true;
    }

    private void Switch(int index)
    {
        var old = Active;
        _active = index;
        ActiveChanged?.Invoke(old, Active);
        Changed?.Invoke();
    }

    // ---- Where saves go -------------------------------------------------------------------------------

    // The mods this run mounted (ModManager's `mods/<id>` folders), which `ed_mod` can write into.
    public IEnumerable<IMount> Mods() =>
        Engine.Vfs.Mounts.Where(m => m is FolderMount && m.Name.StartsWith(ModManager.MountPrefix, StringComparison.Ordinal));

    // Makes the mod `id` the only folder saves go into; null or empty goes back to each record's own file.
    // False, with `error` saying why, when no loaded mod has that id.
    public bool SetTarget(string? id, out string error)
    {
        error = "";
        IMount? target = null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            target = Mods().FirstOrDefault(m => string.Equals(m.Name, ModManager.MountPrefix + id, StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(m.RecordNamespace, id, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                var mods = Mods().Select(m => m.RecordNamespace).ToList();
                error = $"No mod '{id}' is loaded: " + (mods.Count == 0 ? "this run has no mods (put one in the game's mods folder, or -mods <dir>)"
                                                                           : $"the loaded mods are {string.Join(", ", mods)}");
                return false;
            }
        }

        Target = target;
        foreach (var document in _documents) document.Target = target;
        Log.Info(LogCat.Editor, target == null
            ? "Saving each document into its own file"
            : $"Saving into the mod '{target.RecordNamespace}' ({target.Name}) only: a document defined anywhere else saves as a patch there");
        Changed?.Invoke();
        return true;
    }

    // What a tab says about where its document saves: its file, the target's patch, or nowhere yet.
    public static string Describe(EditDocument document) =>
        !document.IsOpen ? "empty"
        : document.SavesAsPatch ? $"read-only here: saves as a patch in {document.Target!.Name}"
        : document.Path.Length > 0 ? document.Path
        : "never saved";
}
