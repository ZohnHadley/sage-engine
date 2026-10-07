#nullable enable
using System;
using System.Collections;
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
            // One the prefab does not name but this placement's overrides do: added to this one (#368).
            bool added = false;
            if (key == null && Placement != null && overrides?.Components is { } own && KeyOf(engine, own, type, ns) is { } ownKey)
            {
                key = ownKey;
                added = true;
            }
            bool editable = key != null && type != typeof(Transform);
            string? note = Placement == null ? null
                : type == typeof(Transform) ? "where it stands is the placement's `at` and `yaw`"
                : added ? "added by this placement"
                : key == null ? "not in the prefab's components: set by a part or the engine"
                : null;
            var group = new InspectorGroup(id, editable ? OverrideSection.Component : null, key ?? id, note) { AddedByPlacement = added };
            JsonObject? body = key != null && overrides?.Components is { } section ? BodyOf(engine, section, type, ns) : null;
            var inPrefab = key != null && !added ? prefab!.Components![key] as JsonObject : null;
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
        var row = new InspectorRow(group, field, value, isOverridden, overridden, provenance, setBy, canEdit);
        AddChildren(row, 1);
        return row;
    }

    // How deep nested objects and lists are shown: deeper than this is a row of its own JSON.
    private const int MaxDepth = 8;

    // A list's elements, a map's entries and an object's fields, as rows under the field's (#368). Each
    // is edited by rewriting the top-level field with the change made inside it: one override, as a
    // file would write it, so a list is replaced whole and an object's fields keep their siblings.
    private static void AddChildren(InspectorRow row, int depth)
    {
        if (depth > MaxDepth || row.Value is not { } value) return;
        switch (row.Field.Kind)
        {
            case ValueKind.List when value is IList list && row.Field.Item is { } item:
                for (int i = 0; i < list.Count; i++)
                    Child(row, item, list[i], $"[{i}]", new RecordPath.Segment(null, i), i, null, depth);
                break;
            case ValueKind.Map when value is IDictionary map && row.Field.Item is { } entry:
                foreach (DictionaryEntry e in map)
                {
                    string key = Convert.ToString(e.Key, System.Globalization.CultureInfo.InvariantCulture) ?? "";
                    Child(row, entry, e.Value, key, new RecordPath.Segment(key, 0), -1, key, depth);
                }
                break;
            case ValueKind.Object:
                var fields = row.Field.Fields.Count > 0 ? row.Field.Fields : Metadata.Of(value.GetType()).Fields;
                foreach (var field in fields)
                    if (field.Get != null)
                        Child(row, field, field.Get(value), field.JsonName, new RecordPath.Segment(field.JsonName, 0), -1, null, depth);
                break;
        }
    }

    private static void Child(InspectorRow parent, FieldMetadata field, object? value, string name, RecordPath.Segment segment,
                              int index, string? key, int depth)
    {
        bool canEdit = parent.Editable && !field.Transient && field.Kind is not (ValueKind.Entity or ValueKind.Other);
        var row = new InspectorRow(parent, field, value, name, segment, index, key, canEdit);
        parent.AddChild(row);
        AddChildren(row, depth + 1);
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
    // After the field, a path into it (#368): `inventory.items[1].count`, `stats.map.strength`.
    public InspectorRow? Find(string path)
    {
        string? only = null;
        if (path.StartsWith("components.", StringComparison.OrdinalIgnoreCase)) { only = "c"; path = path["components.".Length..]; }
        else if (path.StartsWith("parts.", StringComparison.OrdinalIgnoreCase)) { only = "p"; path = path["parts.".Length..]; }
        int dot = path.IndexOf('.');
        if (dot <= 0) return null;
        string groupName = path[..dot];
        var inside = RecordPath.Parse(path[(dot + 1)..]);
        if (inside is not { Count: > 0 } || inside[0].Name is not { } fieldName) return null;

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
            if (row is { Editable: true }) return Descend(row, inside);
            readOnly ??= row;
        }
        return readOnly == null ? null : Descend(readOnly, inside);
    }

    private static InspectorRow? Descend(InspectorRow row, List<RecordPath.Segment> path)
    {
        InspectorRow? at = row;
        for (int i = 1; i < path.Count && at != null; i++)
        {
            var segment = path[i];
            at = at.Children.FirstOrDefault(c => segment.Name == null
                ? c.Index == segment.Index
                : c.Key != null ? string.Equals(c.Key, segment.Name, StringComparison.OrdinalIgnoreCase)
                : c.Index < 0 && Overrides.SameField(c.Name, segment.Name));
        }
        return at;
    }

    private static bool Matches(Engine engine, InspectorGroup group, Type type, string ns) =>
        engine.Components.TryResolveComponent(group.Id, ns, out var t, out _) && t == type;

    // ---- Editing --------------------------------------------------------------------------------------

    // The row's field set to `value` (what a file would say) on this placement: a SetOverride.
    // A row inside a field (an element, an entry, a nested field) rewrites the whole field with the change
    // made in it: one SetOverride, so a drag over a nested number merges as a top-level one does.
    public bool Set(InspectorRow row, JsonNode? value, out string error)
    {
        if (!CanWrite(row, out error)) return false;
        if (row.Parent == null) return WriteTop(row, value);
        var root = Current(row.Top);
        if (!RecordPath.Set(root, Inside(row), value?.DeepClone(), out _, out _))
            return Fail(out error, $"{row.Path} cannot be written");
        return WriteTop(row.Top, Detach(root));
    }

    // A new element at the end of a list, or an entry of a map (`key` is then its name): the value
    // given (what a file would say), or the element type's default. One edit of its own (#368).
    public bool AddItem(InspectorRow container, JsonNode? value, out string error, string? key = null)
    {
        if (!CanWrite(container, out error)) return false;
        if (container.Field.Kind is not (ValueKind.List or ValueKind.Map) || container.Field.Item is not { } item)
            return Fail(out error, $"{container.Path} is not a list or a map");
        if (value == null && !TryDefault(item, out value))
            return Fail(out error, $"a {item.TypeName} has no default to add: give a value");
        var root = Current(container.Top);
        var path = Inside(container);
        var node = RecordPath.Get(root, path);
        if (container.Field.Kind == ValueKind.List)
        {
            if (node is not JsonArray array)
            {
                array = new JsonArray();
                if (!RecordPath.Set(root, path, array, out _, out _)) return Fail(out error, $"{container.Path} cannot be written");
            }
            array.Add(value.DeepClone());
        }
        else
        {
            if (string.IsNullOrEmpty(key)) return Fail(out error, $"an entry of {container.Path} needs a key");
            if (node is not JsonObject map)
            {
                map = new JsonObject();
                if (!RecordPath.Set(root, path, map, out _, out _)) return Fail(out error, $"{container.Path} cannot be written");
            }
            if (map.Any(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)))
                return Fail(out error, $"{container.Path} already has '{key}'");
            map[key] = value.DeepClone();
        }
        return Gesture(container.Top, Detach(root));
    }

    // A list's element or a map's entry taken out.
    public bool RemoveItem(InspectorRow item, out string error)
    {
        if (!CanWrite(item, out error)) return false;
        if (item.Parent is not { Field.Kind: ValueKind.List or ValueKind.Map })
            return Fail(out error, $"{item.Path} is not an element of a list or a map");
        var root = Current(item.Top);
        if (!RecordPath.Remove(root, Inside(item))) return Fail(out error, $"{item.Path} is not there");
        return Gesture(item.Top, Detach(root));
    }

    // A list's element moved to `to` (clamped to the list), the others closing up around it.
    public bool MoveItem(InspectorRow item, int to, out string error)
    {
        if (!CanWrite(item, out error)) return false;
        if (item.Parent is not { Field.Kind: ValueKind.List } parent || item.Index < 0)
            return Fail(out error, $"{item.Path} is not an element of a list");
        var root = Current(item.Top);
        if (RecordPath.Get(root, Inside(parent)) is not JsonArray array || item.Index >= array.Count)
            return Fail(out error, $"{item.Path} is not there");
        to = Math.Clamp(to, 0, array.Count - 1);
        if (to == item.Index) return Fail(out error, $"{item.Path} is already there");
        var node = array[item.Index];
        array.RemoveAt(item.Index);
        array.Insert(to, node);
        return Gesture(item.Top, Detach(root));
    }

    // The row's field back to its prefab's value: a ClearOverride. False when it was not overridden. A
    // row inside a field reverts the field: the override is the field's, whole.
    public bool Revert(InspectorRow row, out string error)
    {
        row = row.Top;
        if (!CanWrite(row, out error)) return false;
        if (!row.Overridden)
        {
            error = $"{row.Path} is not overridden";
            return false;
        }
        return Document!.Execute(new ClearOverride(Document, Placement!, row.Group.Section!.Value, row.Group.Key, row.Field.JsonName));
    }

    // ---- Components the prefab does not name (#368) ----------------------------------------------------

    // Components this placement could add: every registered one the entity does not have, but the
    // transform (the placement's own) and [Transient] ones (nothing a file writes).
    public IEnumerable<string> AddableComponents()
    {
        if (Placement == null || World.Engine is not { } engine || !World.IsAlive(Entity)) return Array.Empty<string>();
        var has = engine.Components.ComponentsOf(Entity).Select(c => c.Value.GetType()).ToHashSet();
        return engine.Components.ComponentIds
            .Where(id => engine.Components.TryComponent(id, out var type) && Addable(type) && !has.Contains(type))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool Addable(Type type) =>
        type != typeof(Transform) && !type.IsDefined(typeof(TransientAttribute), false);

    // A component added to this placement alone: an empty body under `overrides.components`, which the
    // spawn merges into its copy of the prefab as a component of its defaults; its fields are then set
    // like any other. Refused for one the entity already has (the prefab's, or a part's: a second copy
    // would fight it).
    public bool AddComponent(string component, out string error)
    {
        if (Placement == null || Document == null || World.Engine is not { } engine) return Fail(out error, NotPlaced());
        string ns = Placement.Prefab.Id.IsEmpty ? EditDocument.GameNamespace(engine) : Placement.Prefab.Id.Namespace;
        if (!engine.Components.TryResolveComponent(component, ns, out var type, out _))
            return Fail(out error, $"'{component}' is not a component");
        string id = engine.Components.IdOf(type) ?? component;
        if (!Addable(type)) return Fail(out error, $"{id} is not one a placement can add");
        if (engine.Components.ComponentsOf(Entity).Any(c => c.Value.GetType() == type))
            return Fail(out error, $"{AddPlacement.Label(Placement)} already has {id}");
        error = "";
        Document.History.EndMerge();
        bool done = Document.Execute(new SetOverrideBody(Document, Placement, OverrideSection.Component, id, new JsonObject()));
        Document.History.EndMerge();
        return done;
    }

    // A component this placement added taken away again, with its overrides.
    public bool RemoveComponent(InspectorGroup group, out string error)
    {
        if (Placement == null || Document == null) return Fail(out error, NotPlaced());
        if (!group.AddedByPlacement) return Fail(out error, $"{group.Key} is the prefab's, not added by this placement");
        error = "";
        Document.History.EndMerge();
        bool done = Document.Execute(new SetOverrideBody(Document, Placement, OverrideSection.Component, group.Key, null));
        Document.History.EndMerge();
        return done;
    }

    // The group a component is shown in, by its key or any id that names it.
    public InspectorGroup? Group(string component)
    {
        var engine = World.Engine;
        string ns = Placement?.Prefab.Id is { IsEmpty: false } p ? p.Namespace : "sage";
        Type? named = engine != null && engine.Components.TryResolveComponent(component, ns, out var t, out _) ? t : null;
        return _groups.FirstOrDefault(g => g.Section != OverrideSection.Part
                                           && (string.Equals(g.Key, component, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(g.Id, component, StringComparison.OrdinalIgnoreCase)
                                               || named != null && Matches(engine!, g, named, ns)));
    }

    // ---- Typed (the console) -----------------------------------------------------------------------------

    // ed_add: an element at the end of the list at `path` (a value, or the element's default); for a map,
    // the first word is the entry's key.
    public bool TryAddItem(string path, string text, out string error)
    {
        if (Placement == null) return Fail(out error, NotPlaced());
        if (Find(path) is not { } row) return Fail(out error, $"{AddPlacement.Label(Placement)} has no field '{path}'");
        if (row.Field.Item is not { } item || row.Field.Kind is not (ValueKind.List or ValueKind.Map))
            return Fail(out error, $"{row.Path} is not a list or a map");
        text = text.Trim();
        string? key = null;
        if (row.Field.Kind == ValueKind.Map)
        {
            int space = text.IndexOf(' ');
            key = space < 0 ? text : text[..space];
            text = space < 0 ? "" : text[(space + 1)..].Trim();
        }
        JsonNode? value = null;
        if (text.Length > 0 && !InspectorValue.TryParse(item, text, out value, out error, World.Engine!.Records, Namespace()))
            return Fail(out error, $"{row.Path}: {error}");
        return AddItem(row, value, out error, key);
    }

    // ed_remove: the element or entry at `path`.
    public bool TryRemoveItem(string path, out string error)
    {
        if (Placement == null) return Fail(out error, NotPlaced());
        if (Find(path) is not { } row) return Fail(out error, $"{AddPlacement.Label(Placement)} has no field '{path}'");
        return RemoveItem(row, out error);
    }

    // ed_reorder: the element at `path` moved to index `to`.
    public bool TryMoveItem(string path, int to, out string error)
    {
        if (Placement == null) return Fail(out error, NotPlaced());
        if (Find(path) is not { } row) return Fail(out error, $"{AddPlacement.Label(Placement)} has no field '{path}'");
        return MoveItem(row, to, out error);
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

    private string Namespace() =>
        Placement!.Prefab.Id.IsEmpty ? EditDocument.GameNamespace(World.Engine!) : Placement.Prefab.Id.Namespace;

    private bool WriteTop(InspectorRow top, JsonNode? value) =>
        Document!.Execute(new SetOverride(Document, Placement!, top.Group.Section!.Value, top.Group.Key, top.Field.JsonName, value));

    // A change of a list's shape is an edit of its own: not merged into a drag before it or after it.
    private bool Gesture(InspectorRow top, JsonNode? value)
    {
        Document!.History.EndMerge();
        bool done = WriteTop(top, value);
        Document.History.EndMerge();
        return done;
    }

    // The field's value as a file would write it, under a holder so a path into it can be walked and
    // changed (RecordPath works on an object): `Inside` is the row's path in that holder.
    private const string Holder = "value";

    private JsonObject Current(InspectorRow top)
    {
        var node = ToNode(top.Value)?.DeepClone();
        node ??= top.Field.Kind switch
        {
            ValueKind.List => new JsonArray(),
            ValueKind.Map or ValueKind.Object => new JsonObject(),
            _ => null,
        };
        return new JsonObject { [Holder] = node };
    }

    private static List<RecordPath.Segment> Inside(InspectorRow row)
    {
        var path = new List<RecordPath.Segment> { new(Holder, 0) };
        path.AddRange(row.Segments);
        return path;
    }

    private static JsonNode? Detach(JsonObject holder)
    {
        var node = holder[Holder];
        holder.Remove(Holder);
        return node;
    }

    // What a new element is when none is given: the element type's default, as a file would write it.
    private bool TryDefault(FieldMetadata item, [NotNullWhen(true)] out JsonNode? value)
    {
        value = null;
        object? made = null;
        try
        {
            made = item.Type == typeof(string) ? ""
                : item.Type.IsValueType ? Activator.CreateInstance(item.Type)
                : Metadata.Of(item.Type).CreateDefault() ?? (item.Type.IsAbstract || item.Type.IsInterface ? null : Activator.CreateInstance(item.Type));
        }
        catch (Exception ex) when (ex is MissingMethodException or System.Reflection.TargetInvocationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        if (made == null) return false;
        try { value = ToNode(made); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return false; }
        return value != null;
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

    // A component the prefab does not name that this placement's overrides add (#368): removable.
    public bool AddedByPlacement { get; internal init; }

    public IReadOnlyList<InspectorRow> Rows => _rows;

    internal void Add(InspectorRow row) => _rows.Add(row);

    public override string ToString() => Section == OverrideSection.Part ? $"{Key} (part)" : Key;
}

// One field: its value, whether this placement overrides it, and who set it. A list, a map or a nested
// object has rows of its own under it (`Children`, #368): its elements, entries or fields, each edited by
// rewriting the field it is in, so whether it is overridden and who set it are that field's.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class InspectorRow
{
    private readonly List<InspectorRow> _children = new();

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
        Name = field.JsonName;
        Index = -1;
        Segments = Array.Empty<RecordPath.Segment>();
    }

    // A row inside `parent`'s value: an element (`index`), an entry (`key`) or a field of an object.
    internal InspectorRow(InspectorRow parent, FieldMetadata field, object? value, string name, RecordPath.Segment segment,
                          int index, string? key, bool editable)
        : this(parent.Group, field, value, parent.Overridden, null, parent.Provenance, parent.SetBy, editable)
    {
        Parent = parent;
        Name = name;
        Index = index;
        Key = key;
        Segments = parent.Segments.Append(segment).ToArray();
    }

    public InspectorGroup Group { get; }

    // The field's metadata; for an element of a list or a value of a map, the element's (`Item`).
    public FieldMetadata Field { get; }

    // The row this one is inside (null for a component's or a part's own field), and the one at the top:
    // the field an edit of this row overrides.
    public InspectorRow? Parent { get; }
    public InspectorRow Top => Parent?.Top ?? this;
    public IReadOnlyList<InspectorRow> Children => _children;

    // What it is called under its parent: the field's name, "[2]" for an element, the key of an entry.
    public string Name { get; }

    // An element's index in its list (-1 when it is not one), an entry's key in its map (null when not).
    public int Index { get; }
    public string? Key { get; }

    // Its path inside the top row's value.
    internal IReadOnlyList<RecordPath.Segment> Segments { get; }

    // "timer.interval", "body.radius", "inventory.items[1].count": what ed_set and ed_revert take.
    public string Path => Parent == null ? $"{Group.Key}.{Field.JsonName}"
        : Index >= 0 ? $"{Parent.Path}[{Index}]"
        : Key != null && Key.IndexOfAny(new[] { '.', '[', ']', ' ' }) >= 0 ? $"{Parent.Path}['{Key}']"
        : $"{Parent.Path}.{Name}";

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

    internal void AddChild(InspectorRow row) => _children.Add(row);

    public override string ToString() =>
        $"{Path} = {Shown()}{(Overridden && Parent == null ? " *" : "")}{(Provenance.Length > 0 && Parent == null ? $"  <- {Provenance}" : "")}";

    private string Shown() => Field.Kind switch
    {
        ValueKind.List when Value is ICollection list => $"[{list.Count}]",
        ValueKind.Map when Value is ICollection map => $"{{{map.Count}}}",
        ValueKind.Object when Value != null => "{...}",
        _ => InspectorValue.Format(Value),
    };
}
