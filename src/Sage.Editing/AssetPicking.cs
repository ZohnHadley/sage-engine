#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// What an asset picked in the browser does (issue #366, docs/design/15 §11): dropped on a field it is set
// there, as one undoable command of whatever document has the field; dropped in the viewport it is placed.
// The Assets panel's drag and drop and the `ed_asset_*` commands both come here, so a test drives the same.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public static class AssetPicking
{
    // What a model nothing else places is placed as: a mesh renderer and nothing more
    // (engine_content/data/editor.json), with the model as an override of its `mesh`.
    public static readonly RecordId StaticMesh = new("sage", "static_mesh");

    // The asset into the open record's field at `path` ("params.Albedo", "normalMap"): a SetRecordValue on
    // the record's own history, so Ctrl+Z in the Records panel takes it back. A field the record type
    // declares as an asset of another kind refuses it; a field declared as something other than an asset
    // or a string refuses any; a field it does not declare (a material's `params`, which are by name) takes it.
    public static bool ToRecord(RecordDocument record, string path, VirtualPath asset, out string error)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (RecordPath.Parse(path) is not { Count: > 0 })
            return Fail(out error, $"'{path}' is not a path into {record.Type} {record.Id}");
        if (AssetKinds.Of(asset) == null) return Fail(out error, $"{asset} is not an asset the editor knows the kind of");
        var field = record.MetaAt(path);
        string? kind = field?.AssetKind;
        if (field != null && !TakesAPath(field))
            return Fail(out error, $"{record.Type} {record.Id}: '{path}' is a {field.TypeName}, not an asset");
        if (!AssetKinds.Fits(kind, asset))
            return Fail(out error, $"{asset} is {Article(AssetKinds.Of(asset))}, and '{path}' takes {Article(kind)}");
        error = "";
        // A pick is a gesture of its own: not merged into a drag on the same field before or after it.
        record.History.EndMerge();
        bool set = record.Set(path, JsonValue.Create(asset.Value));
        record.History.EndMerge();
        return set || Fail(out error, $"cannot set '{path}' in {record.Type} {record.Id}");
    }

    // The asset into an [AssetKind] field of a placement ("mesh_renderer.mesh", as `ed_set` names one): a
    // SetOverride on the placements document, so `ed_undo` takes it back.
    public static bool ToPlacement(EditDocument document, Placement placement, string path, VirtualPath asset, out string error)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entity = document.EntityOf(placement);
        if (entity.IsNull) return Fail(out error, $"{AddPlacement.Label(placement)} is not in the world");
        var model = InspectorModel.Of(document, document.World, entity);
        if (model.Find(path) is not { } row) return Fail(out error, $"{AddPlacement.Label(placement)} has no field '{path}' (ed_inspect lists them)");
        return ToRow(model, row, asset, out error);
    }

    // The asset into one inspector row: what a drop on the Inspector's field does.
    public static bool ToRow(InspectorModel model, InspectorRow row, VirtualPath asset, out string error)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(row);
        if (AssetKinds.Of(asset) == null) return Fail(out error, $"{asset} is not an asset the editor knows the kind of");
        if (row.Field.Kind != ValueKind.AssetPath)
            return Fail(out error, $"{row.Path} is a {row.Field.TypeName}, not an asset");
        if (!AssetKinds.Fits(row.Field.AssetKind, asset))
            return Fail(out error, $"{asset} is {Article(AssetKinds.Of(asset))}, and {row.Path} takes {Article(row.Field.AssetKind)}");
        model.Document?.History.EndMerge();
        bool set = model.Set(row, JsonValue.Create(asset.Value), out error);
        model.Document?.History.EndMerge();
        return set;
    }

    // The prefab a drop of `asset` in the viewport places: the first prefab (by id) whose record names it,
    // so dropping the goblin's model places the goblin; else, for a model, StaticMesh. Empty when neither.
    public static RecordId PrefabFor(Engine engine, VirtualPath asset)
    {
        var records = engine.Records;
        foreach (var id in records.Ids("prefab").Where(i => i != StaticMesh).OrderBy(i => i.ToString(), StringComparer.Ordinal))
            if (records.RawJson("prefab", id) is { } raw && Names(raw, asset.Value)) return id;
        return AssetKinds.Of(asset) == AssetKinds.Mesh && records.Exists("prefab", StaticMesh) ? StaticMesh : default;
    }

    // Places `asset` at `at` as one AddPlacement (one undo): PrefabFor's prefab, with the model as an
    // override of its mesh when that prefab is StaticMesh. Null, with why, when nothing places it.
    public static Placement? Place(EditDocument document, VirtualPath asset, Vector3 at, float yaw, out string error)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsOpen) { Fail(out error, "no document open (doc_new or doc_open)"); return null; }
        var prefab = PrefabFor(document.Engine, asset);
        if (prefab.IsEmpty)
        {
            Fail(out error, $"no prefab names {asset}, and only a model can be placed on its own: make a prefab that uses it");
            return null;
        }
        string stem = System.IO.Path.GetFileNameWithoutExtension(asset.Value);
        var placement = new Placement
        {
            Prefab = prefab, At = at, Yaw = yaw,
            Name = Placing.UniqueName(document, prefab, prefab == StaticMesh ? stem : ""),
        };
        if (prefab == StaticMesh)
            placement.Overrides = new PrefabOverrides { Components = new JsonObject { ["mesh_renderer"] = new JsonObject { ["mesh"] = asset.Value } } };
        error = "";
        return document.Execute(new AddPlacement(document, placement)) ? placement : null;
    }

    // A drop in the viewport: the asset where the pointer's ray meets the world (Placing.Surface).
    public static Placement? PlaceAt(EditDocument document, VirtualPath asset, in EditorRay ray, out string error)
    {
        if (Placing.Surface(document.World, ray) is not { } point)
        {
            Fail(out error, "the pointer is not over the ground");
            return null;
        }
        return Place(document, asset, point, 0f, out error);
    }

    // The asset slots of an open record, for the Records panel's preview strip (a material's maps): every
    // field the type declares as an asset at the top level, set or not, then every other value in the
    // record that is an asset's path (a material's `params`). A material with no `params.Albedo` gets that
    // slot too, empty: the picture a lit material is drawn with is where a picked texture usually goes.
    public static IReadOnlyList<AssetSlot> Slots(RecordDocument record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var slots = new List<AssetSlot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in record.Meta?.Fields ?? (IReadOnlyList<FieldMetadata>)Array.Empty<FieldMetadata>())
        {
            if (field.Kind != ValueKind.AssetPath || !seen.Add(field.JsonName)) continue;
            slots.Add(new AssetSlot(field.JsonName, field.JsonName, field.AssetKind, PathIn(record.Get(field.JsonName))));
        }
        Walk(record.Working, "");
        if (record.Type == "material" && !seen.Contains("params.Albedo"))
            slots.Add(new AssetSlot("params.Albedo", "Albedo", AssetKinds.Texture, null));
        return slots;

        void Walk(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (name, child) in o) Walk(child, RecordDocument.ChildPath(path, name));
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]");
                    break;
                default:
                    if (PathIn(node) is { } asset && AssetKinds.Of(asset) is { } kind && seen.Add(path))
                        slots.Add(new AssetSlot(path, path.StartsWith("params.", StringComparison.OrdinalIgnoreCase) ? path["params.".Length..] : path, kind, asset));
                    break;
            }
        }
    }

    // A field a path can be written in: an asset or a string, free JSON, or a type whose converter reads
    // its own forms (a material's parameter: a number, a list of them, or a texture's path).
    private static bool TakesAPath(FieldMetadata field) =>
        field.Kind is ValueKind.AssetPath or ValueKind.String or ValueKind.Other or ValueKind.Json
        || field.Kind == ValueKind.Object && field.Type.IsDefined(typeof(System.Text.Json.Serialization.JsonConverterAttribute), inherit: true);

    private static VirtualPath? PathIn(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0 && AssetBrowser.TryParse(text, out var path) && AssetKinds.Of(path) != null
            ? path : null;

    // Whether any string in `node` is the path (any case, as the VFS reads it).
    internal static bool Names(JsonNode? node, string path) => node switch
    {
        JsonObject o => o.Any(kv => Names(kv.Value, path)),
        JsonArray a => a.Any(n => Names(n, path)),
        JsonValue v => v.TryGetValue<string>(out var s) && Same(s, path),
        _ => false,
    };

    internal static bool Same(string text, string path) =>
        string.Equals(text.Replace('\\', '/').Trim().Trim('/'), path, StringComparison.OrdinalIgnoreCase);

    private static string Article(string? kind) =>
        kind == null ? "any asset" : ("aeiou".Contains(kind[0]) ? "an " : "a ") + kind;

    private static bool Fail(out string error, string message)
    {
        error = message;
        return false;
    }
}

// One place in a record an asset goes (AssetPicking.Slots): its path in the record ("normalMap",
// "params.Albedo"), a label, the kind it takes (null: any), and what it names now (null: nothing).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10)
public sealed record AssetSlot(string Path, string Label, string? Kind, VirtualPath? Current);
