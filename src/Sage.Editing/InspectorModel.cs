#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// What the inspector shows for one entity, and what an edit there does (issue #223, docs/design/15 §3).
//
// **An edit is an override, not a poke at the live struct.** The entity is derived from the document
// (§10e), so changing its component in place would last until the next re-spawn and never reach a file.
// Here every field of a placed entity maps to where a placement writes it — the component's body or the
// part's options in `overrides` — and an edit is a `SetOverride` on the document; "revert to prefab" is a
// `ClearOverride`. The mapping:
//
//   - a component the **prefab names in `components`**: one group, headed by its stable id; each field is
//     `overrides.components.<the prefab's key>.<field's JSON name>`, and its value is the live entity's
//     (which is the prefab's with the overrides merged in, since that is what spawned it);
//   - a **part** the prefab names in `parts`: one group per part, its fields the part's options
//     (`[PrefabPart]`'s class), `overrides.parts.<key>.<field>`; the value is the prefab's body with the
//     placement's merged in, read as the part reads it;
//   - a component the entity has that the prefab does **not** name — a part's collider, the engine's
//     name and placement tags — is shown read-only: overriding it would add a second copy the part then
//     fights. Its numbers are the part's to set; `Transform` is the placement's own `at` and `yaw`.
//
// Each field says where its value came from (`Provenance`): "this placement" when it is overridden, else
// the last write to that field in the prefab's files (`RecordStore.Writes`: the defining file, a base
// prefab, or a mod's patch), else "default" when nothing writes it.
//
// An entity the document did not place (one the game spawned, a prefab's child) has no placement to
// write to: the model lists its components read-only (`FromDocument` is false), and the ImGui inspector
// edits those live with a "not saved" note, as it did before the document existed.
//
// A model is a snapshot: an edit re-spawns the entity (the selection follows it through
// `EditDocument.Respawned`), so the inspector builds a new one each time it draws.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class InspectorModel
{
    // The placement's own fields, as `ed_set <name> at 1 2 3` names them.
    public static readonly IReadOnlyList<string> PlacementFieldNames = new[] { "at", "yaw", "pitch", "roll", "scale", "name", "relativeTo" };

    private readonly List<InspectorGroup> _groups = new();

    private InspectorModel(EditDocument? document, World world, Entity entity, Placement? placement)
    {
        Document = document;
        World = world;
        Entity = entity;
        Placement = placement;
    }

    public EditDocument? Document { get; }
    public World World { get; }
    public Entity Entity { get; }

    // The document's placement that spawned the entity; null when the document did not place it.
    public Placement? Placement { get; }
    public bool FromDocument => Placement != null;

    public IReadOnlyList<InspectorGroup> Groups => _groups;
    public IEnumerable<InspectorRow> Rows => _groups.SelectMany(g => g.Rows);

    // ---- Building -----------------------------------------------------------------------------------

    public static InspectorModel Of(EditDocument? document, World world, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(world);
        var placement = document is { IsOpen: true } && world.IsAlive(entity) ? document.PlacementOf(entity) : null;
        var model = new InspectorModel(document, world, entity, placement);
        if (world.Engine is { } engine && world.IsAlive(entity)) model.Build(engine);
        return model;
    }

    private void Build(Engine engine)
    {
        PrefabRecord? prefab = null;
        RecordId prefabId = Placement?.Prefab.Id ?? default;
        if (Placement != null && engine.Records.TryGet(prefabId, out PrefabRecord found)) prefab = found;
        string ns = prefabId.IsEmpty ? "sage" : prefabId.Namespace;
        var writes = prefab != null ? engine.Records.Writes("prefab", prefabId) : Array.Empty<RecordWrite>();
        var overrides = Placement?.Overrides;

        // Components, in the entity's order; the ones the prefab names are the editable ones.
        foreach (var (id, value) in engine.Components.ComponentsOf(Entity))
        {
            var type = value.GetType();
            string? key = prefab?.Components == null ? null : KeyOf(engine, prefab.Components, type, ns);
            bool editable = key != null && type != typeof(Transform);
            string? note = Placement == null ? null
                : type == typeof(Transform) ? "where it stands is the placement's `at` and `yaw`"
                : key == null ? "not in the prefab's components: set by a part or the engine"
                : null;
            var group = new InspectorGroup(id, editable ? OverrideSection.Component : null, key ?? id, note);
            JsonObject? body = key != null && overrides?.Components is { } section ? BodyOf(engine, section, type, ns) : null;
            var inPrefab = key != null ? prefab!.Components![key] as JsonObject : null;
            foreach (var field in Metadata.Of(type).Fields)
            {
                var path = $"components.{key ?? id}.{field.JsonName}";
                group.Add(Row(group, field, field.Get?.Invoke(value), body, editable, path, Has(inPrefab, field), writes));
            }
            _groups.Add(group);
        }

        // Parts, in the prefab's order.
        if (prefab?.Parts is { } parts)
            foreach (var (key, written) in parts)
            {
                if (!engine.Prefabs.TryGet(key, out var part)) continue;
                JsonObject? body = overrides?.Parts?.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) is { Value: { } over }
                    ? AsObject(over, part.Shorthand) : null;
                object? options = ReadPart(engine, part, written, body, ns);
                var inPrefab = AsObject(written, part.Shorthand);
                var group = new InspectorGroup(part.Id, OverrideSection.Part, key, $"part: {part.Type.Name}");
                foreach (var field in Metadata.Of(part.Type).Fields)
                {
                    object? value = options != null ? field.Get?.Invoke(options) : null;
                    group.Add(Row(group, field, value, body, editable: true, $"parts.{key}.{field.JsonName}", Has(inPrefab, field), writes));
                }
                _groups.Add(group);
            }
    }

    private static bool Has(JsonObject? body, FieldMetadata field) =>
        body != null && body.Any(kv => Overrides.SameField(kv.Key, field.JsonName));

    // `inPrefab`: the prefab, as merged from its files, writes the field; when it does not, the value is
    // the type's default whatever wrote the body around it.
    private InspectorRow Row(InspectorGroup group, FieldMetadata field, object? value, JsonObject? body, bool editable,
                             string path, bool inPrefab, IReadOnlyList<RecordWrite> writes)
    {
        JsonNode? overridden = null;
        bool isOverridden = false;
        if (body != null)
            foreach (var (name, node) in body)
                if (Overrides.SameField(name, field.JsonName))
                {
                    overridden = node;
                    isOverridden = true;
                }

        RecordWrite? setBy = null;
        string provenance;
        if (isOverridden) provenance = $"this placement ({Document!.Id})";
        else if (Placement == null || group.Section == null) provenance = "";   // nothing a placement writes: no file says it
        else if (!inPrefab) provenance = "default";
        else
        {
            for (int i = writes.Count - 1; i >= 0; i--)
                if (Writes(writes[i].Path, path)) { setBy = writes[i]; break; }
            provenance = setBy is { } w ? w.At + (w.Via.IsEmpty ? "" : $" (via base {w.Via})") : "default";
        }

        bool canEdit = editable && Placement != null && !field.Transient && field.Kind is not (ValueKind.Entity or ValueKind.Other);
        return new InspectorRow(group, field, value, isOverridden, overridden, provenance, setBy, canEdit);
    }

    // Whether a write at `written` ("components", "components.timer.interval", "" for a whole record)
    // set the value at `path`: it is the field, or holds it.
    private static bool Writes(string written, string path)
    {
        if (written.Length == 0) return true;
        var a = written.Split('.');
        var b = path.Split('.');
        if (a.Length > b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (!Overrides.SameField(a[i], b[i])) return false;
        return true;
    }

    // The key the prefab writes `type` under, any spelling of its id.
    private static string? KeyOf(Engine engine, JsonObject components, Type type, string ns)
    {
        foreach (var (key, _) in components)
            if (engine.Components.TryResolveComponent(key, ns, out var t, out _) && t == type) return key;
        return null;
    }

    private static JsonObject? BodyOf(Engine engine, JsonObject section, Type type, string ns)
    {
        foreach (var (key, body) in section)
            if (body is JsonObject fields && engine.Components.TryResolveComponent(key, ns, out var t, out _) && t == type) return fields;
        return null;
    }

    // A part body written as its shorthand (`"faction": "beasts"`) as the object it stands for.
    private static JsonObject? AsObject(JsonNode? body, string? shorthand) => body switch
    {
        JsonObject o => o,
        null => null,
        _ when shorthand != null => new JsonObject { [Metadata.Camel(shorthand)] = body.DeepClone() },
        _ => null,
    };

    // The part's options as it would read them: the prefab's body with the placement's merged over it.
    private static object? ReadPart(Engine engine, PrefabPartInfo part, JsonNode? written, JsonObject? over, string ns)
    {
        var merged = (JsonObject?)AsObject(written, part.Shorthand)?.DeepClone() ?? new JsonObject();
        if (over != null)
            foreach (var (name, value) in over)
            {
                foreach (var k in merged.Select(kv => kv.Key).Where(k => Overrides.SameField(k, name)).ToList()) merged.Remove(k);
                merged[name] = value?.DeepClone();
            }
        string? outer = RecordParseContext.Namespace;
        RecordParseContext.Namespace = ns;
        try
        {
            return merged.Deserialize(part.Type, engine.Records.Json) ?? Metadata.Of(part.Type).CreateDefault();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return Metadata.Of(part.Type).CreateDefault();
        }
        finally
        {
            RecordParseContext.Namespace = outer;
        }
    }

    // ---- Finding --------------------------------------------------------------------------------------

    // A row by "group.field": the group by the prefab's key or its id (`timer`, `sage:timer`, `body`), and
    // `components.` or `parts.` in front to say which when a component and a part share a name. Without
    // either, a row that can be edited wins over one that cannot: a part builds a component of its own
    // name (`mover` builds `sage:mover`), shown read-only, and `mover.seconds` means the part's (#228).
    public InspectorRow? Find(string path)
    {
        string? only = null;
        if (path.StartsWith("components.", StringComparison.OrdinalIgnoreCase)) { only = "c"; path = path["components.".Length..]; }
        else if (path.StartsWith("parts.", StringComparison.OrdinalIgnoreCase)) { only = "p"; path = path["parts.".Length..]; }
        int dot = path.LastIndexOf('.');
        if (dot <= 0) return null;
        string groupName = path[..dot], fieldName = path[(dot + 1)..];

        var engine = World.Engine;
        string ns = Placement?.Prefab.Id is { IsEmpty: false } p ? p.Namespace : "sage";
        Type? named = engine != null && engine.Components.TryResolveComponent(groupName, ns, out var t, out _) ? t : null;
        InspectorRow? readOnly = null;
        foreach (var group in _groups)
        {
            bool isPart = group.Section == OverrideSection.Part;
            if (only == "c" && isPart || only == "p" && !isPart) continue;
            bool match = string.Equals(group.Key, groupName, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(group.Id, groupName, StringComparison.OrdinalIgnoreCase)
                         || !isPart && named != null && group.Rows.Count > 0 && Matches(engine!, group, named, ns);
            if (!match) continue;
            var row = group.Rows.FirstOrDefault(r => Overrides.SameField(r.Field.JsonName, fieldName));
            if (row is { Editable: true }) return row;
            readOnly ??= row;
        }
        return readOnly;
    }

    private static bool Matches(Engine engine, InspectorGroup group, Type type, string ns) =>
        engine.Components.TryResolveComponent(group.Id, ns, out var t, out _) && t == type;

    // ---- Editing --------------------------------------------------------------------------------------

    // The row's field set to `value` (what a file would say) on this placement: a SetOverride.
    public bool Set(InspectorRow row, JsonNode? value, out string error)
    {
        if (!CanWrite(row, out error)) return false;
        return Document!.Execute(new SetOverride(Document, Placement!, row.Group.Section!.Value, row.Group.Key, row.Field.JsonName, value));
    }

    // The row's field back to its prefab's value: a ClearOverride. False when it was not overridden.
    public bool Revert(InspectorRow row, out string error)
    {
        if (!CanWrite(row, out error)) return false;
        if (!row.Overridden)
        {
            error = $"{row.Path} is not overridden";
            return false;
        }
        return Document!.Execute(new ClearOverride(Document, Placement!, row.Group.Section!.Value, row.Group.Key, row.Field.JsonName));
    }

    // A value typed in (InspectorValue's forms) for "group.field", or for one of the placement's own
    // fields (`at`, `yaw`, `name`, `relativeTo`, a SetPlacement).
    public bool TrySet(string path, string text, out string error)
    {
        if (Placement == null) return Fail(out error, NotPlaced());
        var engine = World.Engine!;
        string ns = Placement.Prefab.Id.IsEmpty ? EditDocument.GameNamespace(engine) : Placement.Prefab.Id.Namespace;

        if (!path.Contains('.'))
        {
            var field = PlacementField(path);
            if (field == null) return Fail(out error, $"'{path}' is not a field of a placement ({string.Join(", ", PlacementFieldNames)}) or component.field");
            // An empty frame is the document's own.
            if (field.JsonName == "relativeTo" && (text.Trim().Length == 0 || text.Trim().Equals("document", StringComparison.OrdinalIgnoreCase)))
                return SetPlacementField(field, null, out error);
            if (!InspectorValue.TryParse(field, text, out var node, out error, engine.Records, ns)) return false;
            object? typed;
            try { typed = node.Deserialize(field.Type, engine.Records.Json); }
            catch (JsonException ex) { return Fail(out error, ex.Message); }
            return SetPlacementField(field, typed, out error);
        }

        var row = Find(path);
        if (row == null) return Fail(out error, $"{AddPlacement.Label(Placement)} has no field '{path}' (ed_inspect lists them)");
        if (!InspectorValue.TryParse(row.Field, text, out var value, out error, engine.Records, ns))
        {
            error = $"{row.Path}: {error}";
            return false;
        }
        return Set(row, value, out error);
    }

    // "group.field" back to the prefab's value.
    public bool TryRevert(string path, out string error)
    {
        if (Placement == null) return Fail(out error, NotPlaced());
        if (!path.Contains('.')) return Fail(out error, $"'{path}': a placement's own fields have no prefab value to revert to");
        var row = Find(path);
        if (row == null) return Fail(out error, $"{AddPlacement.Label(Placement)} has no field '{path}'");
        return Revert(row, out error);
    }

    // The value written as a file would write it (the record store's dialect): what a widget's edit
    // becomes before it is an override.
    public JsonNode? ToNode(object? value) =>
        value == null || World.Engine is not { } engine ? null : new JsonFileEdit("", engine.Records.Json).ToNode(value);

    private static FieldMetadata? PlacementField(string name) =>
        PlacementFieldNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? Metadata.Of(typeof(Placement)).Field(name) : null;

    private bool SetPlacementField(FieldMetadata field, object? value, out string error)
    {
        error = "";
        if (value is System.Numerics.Vector3 scale && field.JsonName == "scale" && !(scale.X > 0f && scale.Y > 0f && scale.Z > 0f))
            return Fail(out error, "a scale must be above 0 on every axis");
        var fields = PlacementFields.Of(Placement!);
        fields = field.JsonName switch
        {
            "at" => fields with { At = (System.Numerics.Vector3)value! },
            "yaw" => fields with { Yaw = (float)value! },
            "pitch" => fields with { Pitch = (float)value! },
            "roll" => fields with { Roll = (float)value! },
            "scale" => fields with { Scale = (System.Numerics.Vector3)value! },
            "name" => fields with { Name = (string?)value ?? "" },
            _ => fields with { RelativeTo = (PlacementFrame?)value },
        };
        return Document!.Execute(new SetPlacement(Document, Placement!, fields));
    }

    private bool CanWrite(InspectorRow row, out string error)
    {
        error = "";
        if (Placement == null || Document == null) return Fail(out error, NotPlaced());
        if (!row.Editable || row.Group.Section == null)
            return Fail(out error, $"{row.Path} is not an override a placement can write" + (row.Group.Note is { } note ? $" ({note})" : ""));
        return true;
    }

    private string NotPlaced() => $"{World.Describe(Entity)} was not placed by the open document: there is nowhere to save an edit";

    private static bool Fail(out string error, string message)
    {
        error = message;
        return false;
    }
}

// One component or part on the inspected entity.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class InspectorGroup
{
    private readonly List<InspectorRow> _rows = new();

    internal InspectorGroup(string id, OverrideSection? section, string key, string? note)
    {
        Id = id;
        Section = section;
        Key = key;
        Note = note;
    }

    // The component's stable id, or the part's id.
    public string Id { get; }

    // Where its overrides go; null when it cannot be overridden (read-only).
    public OverrideSection? Section { get; }

    // The key the prefab writes it under (`timer`, `sage:timer`, `body`): where an override goes.
    public string Key { get; }

    // Why it is read-only, or what it is ("part: BodyPart").
    public string? Note { get; }

    public IReadOnlyList<InspectorRow> Rows => _rows;

    internal void Add(InspectorRow row) => _rows.Add(row);

    public override string ToString() => Section == OverrideSection.Part ? $"{Key} (part)" : Key;
}

// One field: its value, whether this placement overrides it, and who set it.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class InspectorRow
{
    internal InspectorRow(InspectorGroup group, FieldMetadata field, object? value, bool overridden, JsonNode? overrideValue,
                          string provenance, RecordWrite? setBy, bool editable)
    {
        Group = group;
        Field = field;
        Value = value;
        Overridden = overridden;
        OverrideValue = overrideValue;
        Provenance = provenance;
        SetBy = setBy;
        Editable = editable;
    }

    public InspectorGroup Group { get; }
    public FieldMetadata Field { get; }

    // "timer.interval", "body.radius": what ed_set and ed_revert take.
    public string Path => $"{Group.Key}.{Field.JsonName}";

    // The value the entity has (a component's) or the part reads (a part's), typed.
    public object? Value { get; }

    // Whether this placement overrides it, and with what (as its file says it).
    public bool Overridden { get; }
    public JsonNode? OverrideValue { get; }

    // "this placement (sandbox:yard)", the file and line that last wrote it ("game:data/yard.json:3:24"),
    // or "default". Empty for an entity the document did not place, and for a group no placement writes.
    public string Provenance { get; }

    // The prefab's write that set it, when one did and the placement does not override it.
    public RecordWrite? SetBy { get; }

    // Whether an edit can be an override: false for components the prefab does not name, Transform,
    // [Transient] fields, entities and opaque types, and anything the document did not place.
    public bool Editable { get; }

    public override string ToString() =>
        $"{Path} = {InspectorValue.Format(Value)}{(Overridden ? " *" : "")}{(Provenance.Length > 0 ? $"  <- {Provenance}" : "")}";
}
