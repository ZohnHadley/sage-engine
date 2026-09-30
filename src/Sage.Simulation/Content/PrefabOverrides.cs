#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// Per-placement overrides and nested prefabs (F31's remaining half, REDESIGN §4.5, phase 4i issue 1).
//
// **Overrides.** A placement says "a goblin, but this one has 40 health and a smaller capsule" without a
// prefab of its own for it:
//
//   { "prefab": "goblin", "at": [3, 0, 4],
//     "overrides": { "components": { "health": { "max": 40 } },
//                    "parts":      { "body": { "radius": 0.3 } } } }
//
// Each body is merged field by field into a *copy* of the prefab for that one entity — the same merge a
// `base` prefab's body gets from its child — so a part's options can be overridden, not only components,
// and that is how a capsule is resized: the part still builds the collider, from the placement's numbers.
// A `.map`'s per-entity keys (`"light.range" "12"`, PrefabKeys) are overrides of exactly this shape, read
// from the map's text; the two cannot drift, because both are applied here.
//
// A bare record id inside an override means one in the *prefab's* namespace, as it does in the body the
// override merges into (a scene in `game:` placing `sage:scripted_camera` writes ids as that prefab does).
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i): overrides are what 4i-5's diff-against-the-prefab builds on
public sealed class PrefabOverrides
{
    [Property(Tooltip = "Component bodies by id, merged field by field into the prefab's")]
    public JsonObject? Components;
    [Property(Tooltip = "Part options by part id, merged field by field into the prefab's")]
    public JsonObject? Parts;

    // Set when content loading checked these bodies (Scenes' placement check): what is wrong was said at
    // its line then, so a spawn says it again only at Debug.
    internal bool CheckedAtLoad;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => (Components == null || Components.Count == 0) && (Parts == null || Parts.Count == 0);

    public PrefabOverrides Clone() => new()
    {
        Components = (JsonObject?)Components?.DeepClone(),
        Parts = (JsonObject?)Parts?.DeepClone(),
        CheckedAtLoad = CheckedAtLoad,
    };
}

// One prefab placed inside another (a lamp on a cart, a torch in a sconce): spawned with its parent,
// parented to it with World.SetParent at `at`/`yaw` in the parent's frame, and destroyed with it.
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i)
public sealed class PrefabChild
{
    [Property(Tooltip = "What to place")]
    public RecordRef<PrefabRecord> Prefab;
    [Property(Unit = "m", Tooltip = "Where, in the parent's frame")]
    public Vector3 At;
    [Property(Unit = "deg", Tooltip = "Degrees about +Y, relative to the parent")]
    public float Yaw;
    [Property(Tooltip = "Optional, so ent_list and a wire can name it")]
    public string Name = "";
    [Property(Tooltip = "Changes to the child's prefab for this child alone")]
    public PrefabOverrides? Overrides;
}

// What was overridden on this entity, kept so `ent_dump` can mark it and an editor can write the
// placement back with its overrides (ReadPlacements). Not saved: the content that placed the entity
// says it again (4i-3 re-places content before laying a save over it).
[Transient]
[Component("sage:prefab_overridden")]
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i)
public struct PrefabOverridden : IComponent
{
    public PrefabOverrides? Overrides;
}

// Placed by its parent's prefab (`children`): destroyed when the parent is. A child the game parents by
// hand (an attachment, a level's mesh) is not tagged and outlives its parent, as it always did.
[Tag("sage:from_parent_prefab")]
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i)
public struct FromParentPrefab : ITag { }

internal static class PrefabOverriding
{
    // How deep prefabs may nest: a room of carts of lamps is three. Deeper is almost certainly a mistake,
    // and a limit is what keeps a cycle the load check did not see (hot reload mid-edit) from recursing.
    public const int MaxDepth = 8;

    // The prefab with `overrides` merged into a copy of it; the prefab itself when there are none.
    public static PrefabRecord Apply(Engine engine, RecordId id, PrefabRecord prefab, PrefabOverrides? overrides)
    {
        if (overrides == null || overrides.IsEmpty) return prefab;

        var copy = new PrefabRecord
        {
            Name = prefab.Name,
            Components = (JsonObject?)prefab.Components?.DeepClone(),
            Tags = new List<string>(prefab.Tags),
            Parts = (JsonObject?)prefab.Parts?.DeepClone(),
            Children = prefab.Children,   // read, never written: the children's own overrides are theirs
            CheckedAtLoad = prefab.CheckedAtLoad && overrides.CheckedAtLoad,
        };

        if (overrides.Components is { Count: > 0 })
        {
            copy.Components ??= new JsonObject();
            foreach (var (name, body) in overrides.Components)
                MergeSection(copy.Components, ComponentKey(engine, copy.Components, name, id.Namespace), body, shorthand: null);
        }

        if (overrides.Parts is { Count: > 0 })
        {
            copy.Parts ??= new JsonObject();
            foreach (var (name, body) in overrides.Parts)
            {
                string key = copy.Parts.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
                string? shorthand = engine.Prefabs.TryGet(key, out var part) ? part.Shorthand : null;
                MergeSection(copy.Parts, key, body, shorthand);
            }
        }
        return copy;
    }

    // The key the prefab writes a component under, when it writes the same one another way
    // (`health` and `sage:health` are one component): the override lands on it rather than beside it.
    private static string ComponentKey(Engine engine, JsonObject components, string name, string ns)
    {
        if (!engine.Components.TryResolveComponent(name, ns, out var type, out _)) return name;
        foreach (var (existing, _) in components)
            if (engine.Components.TryResolveComponent(existing, ns, out var other, out _) && other == type)
                return existing;
        return name;
    }

    // One section's body merged into the copy: objects field by field, anything else replaced. A bare
    // shorthand value (`"faction": "beasts"`) becomes the object it stands for before an object merges in.
    private static void MergeSection(JsonObject section, string key, JsonNode? patch, string? shorthand)
    {
        if (patch is not JsonObject fields)
        {
            section[key] = patch?.DeepClone();
            return;
        }
        if (section[key] is not JsonObject target)
        {
            target = new JsonObject();
            if (section[key] is { } bare && shorthand != null) target[Metadata.Camel(shorthand)] = bare.DeepClone();
            section[key] = target;
        }
        MergeObject(target, fields);
    }

    private static void MergeObject(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            var same = target.Select(kv => kv.Key).Where(k => SameField(k, name)).ToList();
            if (value is JsonObject inner && same.Count == 1 && target[same[0]] is JsonObject existing)
            {
                MergeObject(existing, inner);
                continue;
            }
            // Replace whichever spelling the prefab used, so the value read is this one.
            foreach (var k in same) target.Remove(k);
            target[name] = value?.DeepClone();
        }
    }

    // Record fields read case-insensitively; `baseY`, `BaseY` and `base_y` are one field to a merge.
    internal static bool SameField(string a, string b) =>
        string.Equals(a.Replace("_", ""), b.Replace("_", ""), StringComparison.OrdinalIgnoreCase);

    // ---- The load check ------------------------------------------------------------------------------

    // An override's bodies, checked as a prefab's own are (PrefabChecks), at `path` in the record that
    // writes them; bare ids in the prefab's namespace, as spawning reads them.
    public static void Check(Engine engine, PrefabOverrides? overrides, string prefabNamespace, string path, RecordCheck check)
    {
        if (overrides == null) return;
        overrides.CheckedAtLoad = true;
        string? outer = RecordParseContext.Namespace;
        RecordParseContext.Namespace = prefabNamespace;
        try
        {
            PrefabChecks.CheckBody(engine, overrides.Components, null, overrides.Parts, prefabNamespace, path, check);
        }
        finally
        {
            RecordParseContext.Namespace = outer;
        }
    }

    // A prefab whose children contain it, however far down, would spawn for ever; one nested deeper than
    // MaxDepth is almost certainly one too. Both are load errors, at the prefab's `children`.
    public static void CheckNesting(PrefabRecord prefab, RecordCheck check)
    {
        if (prefab.Children.Count == 0) return;
        var chain = new List<RecordId> { check.Id };
        int deepest = 0;
        string? cycle = null;
        Walk(prefab, 1);
        if (cycle != null)
            check.Error("Children", $"contains itself: {cycle}");
        else if (deepest > MaxDepth)
            check.Error("Children", $"nests prefabs {deepest} deep; the limit is {MaxDepth}");

        void Walk(PrefabRecord record, int depth)
        {
            if (cycle != null) return;
            deepest = Math.Max(deepest, depth);
            if (depth > MaxDepth + 1) return;
            foreach (var child in record.Children)
            {
                var id = child.Prefab.Id;
                if (id.IsEmpty) continue;
                if (id == check.Id)
                {
                    cycle = string.Join(" -> ", chain.Append(id));
                    return;
                }
                // A cycle that does not pass through this prefab is reported by the prefabs in it.
                if (chain.Contains(id) || !check.TryGet(id, out PrefabRecord? inner) || inner == null) continue;
                chain.Add(id);
                Walk(inner, depth + 1);
                chain.RemoveAt(chain.Count - 1);
                if (cycle != null) return;
            }
        }
    }

    // A child's own placement, checked where the parent writes it: a prefab, and its overrides.
    public static void CheckChildren(Engine engine, PrefabRecord prefab, RecordCheck check)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < prefab.Children.Count; i++)
        {
            var child = prefab.Children[i];
            string path = $"Children[{i}]";
            if (child == null) { check.Error(path, "an empty child"); continue; }
            if (child.Prefab.Id.IsEmpty) { check.Error(path, "a child needs a \"prefab\""); continue; }
            // A child's persistent id is its parent's plus its name (4i-3): two of one name would be one id.
            if (!string.IsNullOrEmpty(child.Name) && !names.Add(child.Name))
                check.Error(path + ".Name", $"another child is already named '{child.Name}'; a save could not tell them apart");
            Check(engine, child.Overrides, child.Prefab.Id.Namespace, path + ".Overrides", check);
        }
        CheckNesting(prefab, check);
    }
}
