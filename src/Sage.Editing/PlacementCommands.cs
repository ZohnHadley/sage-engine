#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// The edits a placements document takes (issue #217). Each changes the document's copy of the record
// (EditDocument.Record), then re-spawns the one placement it touched; none of them reads the world.
//
// A command holds the placement itself, not its index or its entity: an undo puts a removed placement back
// as the same object, so a later command in the log still finds it, and its entity is whatever the
// document spawned for it last (EditDocument.EntityOf).

// Places a prefab: appended to the document, or at `index`. A placement with no `id` is given one
// (EditDocument.UniqueId) so its identity does not hang on its place in the list.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class AddPlacement : IEditorCommand
{
    private readonly EditDocument _document;
    private readonly int _index;

    public AddPlacement(EditDocument document, Placement placement, int index = -1)
    {
        _document = document;
        Placement = placement;
        _index = index;
        if (string.IsNullOrWhiteSpace(placement.Id))
            placement.Id = document.UniqueId(placement.Name.Length > 0 ? placement.Name : placement.Prefab.Id.Name);
    }

    public Placement Placement { get; }
    public string Description => $"Place {Label(Placement)}";

    public void Do()
    {
        int count = _document.Placements.Count;
        _document.Insert(_index < 0 || _index > count ? count : _index, Placement);
    }

    public void Undo()
    {
        int index = _document.IndexOf(Placement);
        if (index >= 0) _document.RemoveAt(index);
    }

    internal static string Label(Placement placement) =>
        placement.Name.Length > 0 ? placement.Name
        : placement.Id.Length > 0 ? placement.Id
        : placement.Prefab.Id.ToString();
}

// Takes a placement out of the document (and its entity out of the world); undo puts it back where it was.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class RemovePlacement : IEditorCommand
{
    private readonly EditDocument _document;
    private int _index = -1;

    public RemovePlacement(EditDocument document, Placement placement)
    {
        _document = document;
        Placement = placement;
    }

    public Placement Placement { get; }
    public string Description => $"Delete {AddPlacement.Label(Placement)}";

    public void Do()
    {
        _index = _document.IndexOf(Placement);
        if (_index >= 0) _document.RemoveAt(_index);
    }

    public void Undo()
    {
        if (_index < 0) return;
        _document.Insert(Math.Min(_index, _document.Placements.Count), Placement);
    }
}

// The fields of a placement that say where it stands and what it is called: its `at`, its `yaw`, its
// `name` and the frame `at` is in (`relativeTo`; null is the document's).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct PlacementFields(Vector3 At, float Yaw, string Name, PlacementFrame? RelativeTo)
{
    public static PlacementFields Of(Placement placement) =>
        new(placement.At, placement.Yaw, placement.Name, placement.RelativeTo);

    internal void ApplyTo(Placement placement)
    {
        placement.At = At;
        placement.Yaw = Yaw;
        placement.Name = Name ?? "";
        placement.RelativeTo = RelativeTo;
    }
}

// Moves, turns, renames or re-frames a placement. A drag is one of these per frame, merged into one.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class SetPlacement : IEditorCommand
{
    private readonly EditDocument _document;
    private readonly PlacementFields _before;
    private PlacementFields _after;

    public SetPlacement(EditDocument document, Placement placement, PlacementFields fields)
    {
        _document = document;
        Placement = placement;
        _before = PlacementFields.Of(placement);
        _after = fields;
    }

    public Placement Placement { get; }
    public PlacementFields Before => _before;
    public PlacementFields After => _after;

    public string Description
    {
        get
        {
            string what = _before.Name.Length > 0 ? _before.Name : AddPlacement.Label(Placement);
            if (_before.Name != _after.Name) return $"Rename {what} to {_after.Name}";
            if (_before.At != _after.At && _before.Yaw == _after.Yaw) return $"Move {what}";
            if (_before.At == _after.At && _before.Yaw != _after.Yaw) return $"Rotate {what}";
            return $"Set {what}";
        }
    }

    public void Do() => Set(_after);
    public void Undo() => Set(_before);

    private void Set(PlacementFields fields)
    {
        fields.ApplyTo(Placement);
        _document.Respawn(Placement);
    }

    public bool TryMerge(IEditorCommand next)
    {
        if (next is not SetPlacement later || !ReferenceEquals(later.Placement, Placement)) return false;
        _after = later._after;
        return true;
    }
}

// Which half of a placement's overrides a field is in: a component's body, or a part's options.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public enum OverrideSection
{
    Component,
    Part,
}

// One field of one component or part overridden for one placement, in the shape a placement writes
// (`"overrides": { "components": { "health": { "max": 40 } } }`, PrefabOverrides): `id` is the component
// or part as the prefab names it, `field` its JSON name, and `value` what a file would say there. A body
// already written under another spelling of the same component (`health` and `sage:health`), or a field
// under another spelling (`baseY`, `base_y`), is the one changed, as spawning would read them as one.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class SetOverride : IEditorCommand
{
    private readonly EditDocument _document;
    private readonly PrefabOverrides? _before;
    private JsonNode? _value;

    public SetOverride(EditDocument document, Placement placement, OverrideSection section, string id, string field, JsonNode? value)
    {
        _document = document;
        Placement = placement;
        Section = section;
        Id = id;
        Field = field;
        _value = value?.DeepClone();
        _before = placement.Overrides;
    }

    public Placement Placement { get; }
    public OverrideSection Section { get; }
    public string Id { get; }
    public string Field { get; }
    public JsonNode? Value => _value;

    public string Description =>
        $"Set {AddPlacement.Label(Placement)} {Id}.{Field} = {(_value == null ? "null" : _value.ToJsonString())}";

    public void Do()
    {
        var overrides = _before?.Clone() ?? new PrefabOverrides();
        var section = Overrides.Section(overrides, Section, create: true)!;
        string key = Overrides.KeyFor(_document.Engine, section, Section, Id, Placement.Prefab.Id.Namespace);
        if (section[key] is not JsonObject body)
        {
            // A part written as its shorthand (`"faction": "beasts"`) becomes an object here; the merge
            // that spawns it reads the two alike.
            body = new JsonObject();
            section[key] = body;
        }
        Overrides.RemoveField(body, Field);
        body[Field] = _value?.DeepClone();
        Placement.Overrides = overrides;
        _document.Respawn(Placement);
    }

    public void Undo()
    {
        Placement.Overrides = _before;
        _document.Respawn(Placement);
    }

    // A slider dragged over a field is one edit.
    public bool TryMerge(IEditorCommand next)
    {
        if (next is not SetOverride later || !ReferenceEquals(later.Placement, Placement) || later.Section != Section
            || !string.Equals(later.Id, Id, StringComparison.OrdinalIgnoreCase) || !Overrides.SameField(later.Field, Field))
            return false;
        _value = later._value?.DeepClone();
        return true;
    }
}

// The field's override taken away, so the placement has its prefab's value again ("revert to prefab");
// a body left empty goes with it, and so do overrides left empty.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class ClearOverride : IEditorCommand
{
    private readonly EditDocument _document;
    private readonly PrefabOverrides? _before;

    public ClearOverride(EditDocument document, Placement placement, OverrideSection section, string id, string field)
    {
        _document = document;
        Placement = placement;
        Section = section;
        Id = id;
        Field = field;
        _before = placement.Overrides;
    }

    public Placement Placement { get; }
    public OverrideSection Section { get; }
    public string Id { get; }
    public string Field { get; }

    public string Description => $"Revert {AddPlacement.Label(Placement)} {Id}.{Field}";

    public void Do()
    {
        if (_before == null) return;
        var overrides = _before.Clone();
        if (Overrides.Section(overrides, Section, create: false) is { } section)
        {
            string key = Overrides.KeyFor(_document.Engine, section, Section, Id, Placement.Prefab.Id.Namespace);
            if (section[key] is JsonObject body)
            {
                Overrides.RemoveField(body, Field);
                if (body.Count == 0) section.Remove(key);
            }
        }
        if (overrides.Components is { Count: 0 }) overrides.Components = null;
        if (overrides.Parts is { Count: 0 }) overrides.Parts = null;
        Placement.Overrides = overrides.IsEmpty ? null : overrides;
        _document.Respawn(Placement);
    }

    public void Undo()
    {
        Placement.Overrides = _before;
        _document.Respawn(Placement);
    }
}

// A placement's wires (`outputs`, issue #80) replaced as a whole: the I/O panel edits a list.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class SetOutputs : IEditorCommand
{
    private readonly EditDocument _document;
    private readonly List<Connection> _before;
    private readonly List<Connection> _after;

    public SetOutputs(EditDocument document, Placement placement, IEnumerable<Connection> outputs)
    {
        _document = document;
        Placement = placement;
        _before = EditDocument.CopyOutputs(placement.Outputs);
        _after = EditDocument.CopyOutputs(outputs);
    }

    public Placement Placement { get; }
    public string Description => $"Wire {AddPlacement.Label(Placement)} ({_after.Count} output(s))";

    public void Do() => Set(_after);
    public void Undo() => Set(_before);

    private void Set(List<Connection> outputs)
    {
        Placement.Outputs = EditDocument.CopyOutputs(outputs);
        _document.Respawn(Placement);
    }
}

// Finding a body and a field in an override the way spawning does (PrefabOverriding).
internal static class Overrides
{
    public static JsonObject? Section(PrefabOverrides overrides, OverrideSection section, bool create)
    {
        if (section == OverrideSection.Component)
        {
            if (overrides.Components == null && create) overrides.Components = new JsonObject();
            return overrides.Components;
        }
        if (overrides.Parts == null && create) overrides.Parts = new JsonObject();
        return overrides.Parts;
    }

    // The key a body is already written under: the same spelling, or (for a component) any id that
    // names the same component. Otherwise `id` as given.
    public static string KeyFor(Engine engine, JsonObject section, OverrideSection kind, string id, string prefabNamespace)
    {
        foreach (var (key, _) in section)
            if (string.Equals(key, id, StringComparison.OrdinalIgnoreCase)) return key;
        if (kind == OverrideSection.Component
            && engine.Components.TryResolveComponent(id, prefabNamespace, out var type, out _))
            foreach (var (key, _) in section)
                if (engine.Components.TryResolveComponent(key, prefabNamespace, out var other, out _) && other == type)
                    return key;
        return id;
    }

    public static void RemoveField(JsonObject body, string field)
    {
        foreach (var key in body.Select(kv => kv.Key).Where(k => SameField(k, field)).ToList()) body.Remove(key);
    }

    // Record fields read case-insensitively; `baseY`, `BaseY` and `base_y` are one field.
    public static bool SameField(string a, string b) =>
        string.Equals(a.Replace("_", ""), b.Replace("_", ""), StringComparison.OrdinalIgnoreCase);
}
