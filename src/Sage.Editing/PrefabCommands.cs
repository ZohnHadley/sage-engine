#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// Revert to prefab, whole, and nesting placements into a prefab (issue #372; F31's editor half, docs/design/15
// §10e). A field's revert is ClearOverride (#223); these are the two a level designer reaches for next.
//
//   ed_revert_all <placement>                    every override taken away: the prefab, as it is
//   ed_make_prefab <id> <placement> [more...]    the placements become a new prefab's `children`, and
//                                                one placement of it stands where they were

// Every override of a placement taken away: it is its prefab again. One undo step.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class RevertPlacement : IEditorCommand
{
    private readonly EditDocument _document;
    private readonly PrefabOverrides? _before;

    public RevertPlacement(EditDocument document, Placement placement)
    {
        _document = document;
        Placement = placement;
        _before = placement.Overrides;
    }

    public Placement Placement { get; }
    public string Description => $"Revert {AddPlacement.Label(Placement)} to its prefab";

    public void Do()
    {
        Placement.Overrides = null;
        _document.Respawn(Placement);
    }

    public void Undo()
    {
        Placement.Overrides = _before;
        _document.Respawn(Placement);
    }
}

[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class PrefabCommands
{
    // Whether the placement overrides anything of its prefab.
    public static bool Overrides(Placement placement) => placement.Overrides is { IsEmpty: false };

    // Every override off `placement`, as one undo step. False (and why) when it has none.
    public static bool RevertAll(EditDocument document, Placement placement, out string error)
    {
        error = "";
        if (!Overrides(placement))
        {
            error = $"{AddPlacement.Label(placement)} overrides nothing: it is its prefab already";
            return false;
        }
        return document.Execute(new RevertPlacement(document, placement));
    }

    // **Nesting** (F31 in the editor): `placements` become the `children` of a new prefab `id` — at their
    // places relative to the first of them, with their yaw, name and overrides — written to `data/<name>.json`
    // in the game's mount and usable at once; then one placement of it, where the first stood, replaces
    // them in the document as one undo step (undo puts them back; the prefab stays, as any record a person
    // wrote does). What a prefab's child cannot say is refused: a pitch, a roll or a scale, a frame of its
    // own, and wires, from it or to it (a child has no outputs, and a wire names a placement).
    public static Placement? MakePrefab(EditDocument document, RecordId id, IReadOnlyList<Placement> placements, out string error)
    {
        error = "";
        if (!document.IsOpen) return Fail(out error, "no document open");
        if (placements.Count == 0) return Fail(out error, "nothing to nest: name or select placements");
        if (id.IsEmpty) return Fail(out error, "the new prefab needs an id");
        if (document.Engine.Records.Exists("prefab", id)) return Fail(out error, $"there is a prefab '{id}' already");
        foreach (var p in placements)
        {
            string label = AddPlacement.Label(p);
            if (document.IndexOf(p) < 0) return Fail(out error, $"{label} is not a placement of {document.Id}");
            if (p.Pitch != 0f || p.Roll != 0f || p.Scale != Vector3.One)
                return Fail(out error, $"{label} is pitched, rolled or scaled, and a prefab's child has only a yaw");
            if (p.RelativeTo != placements[0].RelativeTo)
                return Fail(out error, $"{label} is measured from another frame than {AddPlacement.Label(placements[0])}");
            if (p.Outputs.Count > 0) return Fail(out error, $"{label} has wires, and a prefab's child cannot");
            var wired = document.Placements.Where(o => !placements.Contains(o)).SelectMany(o => o.Outputs)
                .FirstOrDefault(w => Names(p, w.Target));
            if (wired != null) return Fail(out error, $"{label} is wired to (by '{wired.Target}'), and a prefab's child cannot be named by a wire");
        }

        // The prefab: its children where they stand, measured from the first.
        var pivot = placements[0].At;
        var dialect = new JsonFileEdit("", document.Engine.Records.Json);
        var record = new PrefabRecord { Name = id.Name };
        var children = new JsonArray();
        foreach (var p in placements)
        {
            var child = new PrefabChild { Prefab = p.Prefab, At = p.At - pivot, Yaw = p.Yaw, Name = p.Name, Overrides = p.Overrides?.Clone() };
            record.Children.Add(child);
            var node = new JsonObject
            {
                ["prefab"] = p.Prefab.Id.Namespace == id.Namespace ? p.Prefab.Id.Name : p.Prefab.Id.ToString(),
                ["at"] = dialect.ToNode(child.At),
            };
            if (child.Yaw != 0f) node["yaw"] = child.Yaw;
            if (child.Name.Length > 0) node["name"] = child.Name;
            if (child.Overrides is { IsEmpty: false } overrides) node["overrides"] = dialect.ToNode(overrides);
            children.Add(node);
        }
        string file = FileFor(document.Engine, id);
        if (file.Length == 0) return Fail(out error, $"no writable mount for namespace '{id.Namespace}'");
        try
        {
            var edit = JsonFileEdit.Open(file, document.Engine.Records.Json);
            edit.SetRecord("prefab", id, new JsonObject { ["type"] = "prefab", ["id"] = id.Name, ["children"] = children });
            edit.Save(file);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Fail(out error, $"could not write {file}: {ex.Message}");
        }
        // Usable now, without a reload that would place the open document's records again from the store.
        document.Engine.Records.AddRuntime(id, record);
        Log.Info(LogCat.Editor, $"Wrote prefab '{id}' ({placements.Count} child(ren)) to {file}");

        // In the document: the placements out, one of the prefab in where the first was.
        int index = placements.Min(document.IndexOf);
        var nested = new Placement { Prefab = new RecordRef<PrefabRecord>(id), At = pivot, Name = id.Name, RelativeTo = placements[0].RelativeTo };
        var steps = placements.Select(p => (IEditorCommand)new RemovePlacement(document, p)).ToList();
        steps.Add(new AddPlacement(document, nested, index));
        document.Execute(new CommandGroup($"Nest {placements.Count} placement(s) into {id}", steps));
        return nested;
    }

    public static void Register(CVarRegistry cvars, Func<EditDocument?> document, Func<IReadOnlyList<Placement>>? selected = null)
    {
        cvars.RegisterCommand("ed_revert_all", CVarFlags.DevOnly,
            "ed_revert_all <placement>: take every override away, back to the prefab (one undo step).", a =>
        {
            if (a.Count < 1) { Log.Warn(LogCat.Console, "ed_revert_all <placement>"); return; }
            if (!Find(document, a[0], out var doc, out var placement)) return;
            if (RevertAll(doc, placement, out string error)) Log.Info(LogCat.Console, $"{AddPlacement.Label(placement)} is its prefab again");
            else Log.Warn(LogCat.Console, $"ed_revert_all: {error}");
        });

        cvars.RegisterCommand("ed_make_prefab", CVarFlags.DevOnly,
            "ed_make_prefab <id> [placement...]: nest the placements (or the selection) into a new prefab, and place it where they were.", a =>
        {
            if (a.Count < 1) { Log.Warn(LogCat.Console, "ed_make_prefab <id> [placement...]   (no placements: the selection)"); return; }
            if (document() is not { IsOpen: true } doc) { Log.Warn(LogCat.Console, "no document open (doc_open)"); return; }
            var list = new List<Placement>();
            for (int i = 1; i < a.Count; i++)
            {
                if (doc.Find(a[i]) is not { } p) { Log.Warn(LogCat.Console, $"'{a[i]}' is not a placement of {doc.Id}"); return; }
                list.Add(p);
            }
            if (list.Count == 0 && selected != null) list.AddRange(selected());
            var id = RecordId.Parse(a[0], EditDocument.GameNamespace(doc.Engine));
            if (MakePrefab(doc, id, list, out string error) is { } nested)
                Log.Info(LogCat.Console, $"{list.Count} placement(s) nested into prefab '{id}', placed at {InspectorValue.Format(nested.At)}");
            else Log.Warn(LogCat.Console, $"ed_make_prefab: {error}");
        });
    }

    // Where a new prefab is written: data/<name>.json in the last writable mount of its namespace.
    private static string FileFor(Engine engine, RecordId id)
    {
        foreach (var mount in engine.Vfs.Mounts.Reverse())
            if (string.Equals(mount.RecordNamespace, id.Namespace, StringComparison.OrdinalIgnoreCase)
                && mount.WritablePath(VirtualPath.Parse($"data/{id.Name}.json")) is { } file)
                return file;
        return "";
    }

    private static bool Names(Placement placement, string target) =>
        target.Length > 0 && ((placement.Name.Length > 0 && string.Equals(placement.Name, target, StringComparison.OrdinalIgnoreCase))
                              || (placement.Id.Length > 0 && string.Equals(placement.Id, target, StringComparison.OrdinalIgnoreCase)));

    private static bool Find(Func<EditDocument?> document, string name, [NotNullWhen(true)] out EditDocument? doc, [NotNullWhen(true)] out Placement? placement)
    {
        placement = null;
        doc = document();
        if (doc is not { IsOpen: true }) { Log.Warn(LogCat.Console, "no document open (doc_open)"); return false; }
        placement = doc.Find(name);
        if (placement == null) Log.Warn(LogCat.Console, $"'{name}' is not a placement of {doc.Id}");
        return placement != null;
    }

    private static Placement? Fail(out string error, string why)
    {
        error = why;
        return null;
    }
}
