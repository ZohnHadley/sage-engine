#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// A thing you can place (docs/design/05 §3.5, 09 §3.4, TODO F31). Before this the engine had no
// opinion about it, so the Sandbox invented `spawn` records and the Daggerfall importer had to
// generate game-specific JSON to place anything (engine review 2026-09-23, item 3).
//
// A prefab is a record like any other, which is where `base` inheritance, per-field patching, load
// order and hot reload come from for nothing (05 §3.5) — a `goblin_chief` can `base` a `goblin` and
// override one field of one component. Its body is two halves:
//
//   "components": component data by component id, applied as written (09 §3.1, issue #16): a bare
//                 name means the prefab's own namespace, then `sage` (ComponentSchema);
//   "parts":      named setups a plugin declared ([PrefabPart]), for the things that are not one component.
//
// The split is the honest one. A `SpriteRenderer` is data. "Make this a character" is a collider, a
// controller, an intent and ground state that have to agree, so it is a part `PhysicsModule` owns —
// and adding a feature means declaring a part, not editing this record.
//
//   { "type": "prefab", "id": "goblin", "name": "goblin",
//     "components": { "sprite_renderer": { "sheet": "sage:goblin", "size": [1.6, 1.9] } },
//     "tags": ["hostile"],
//     "parts": { "character": { "layer": "enemy" }, "melee": { "attack": "sage:claw" },
//                "attributes": {}, "effects": ["sage:tough_hide"] } }
[Record("prefab", Plugin = RegistrationOwners.Core, Reload = ReloadPolicy.Live)]   // instances follow an edit (PrefabReload, #287)
public sealed class PrefabRecord
{
    public string Name = "";              // what World.Describe calls it; empty = the record id
    public JsonObject? Components;
    public List<string> Tags = new();
    public JsonObject? Parts;

    // Whether a runtime `Spawn` of this prefab is kept by a save (phase 4i): true by default, so a dropped
    // item, a summon or an `ent_spawn` crate comes back; false for what is only for the eye (a cue, a
    // particle) or is rebuilt by something else.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i)
    [Property(Tooltip = "Whether an entity spawned from this prefab at runtime is saved; false for effects and other things nothing needs back")]
    public bool Persist = true;

    // Prefabs placed inside this one, parented to it and destroyed with it (phase 4i, F31). A prefab
    // that contains itself, or nests deeper than 8, is a load error.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i)
    [Property(Tooltip = "Prefabs placed inside this one: parented to it and destroyed with it")]
    public List<PrefabChild> Children = new();

    // How it looks from the far ring (issue #277): a streamed scene's placement of this prefab in a sector
    // past the full-detail ring is drawn as this — a low-detail mesh, or a box of `size` — instead of not at
    // all, so a tower or a town is on the horizon before you get there. Left out, nothing is drawn far away.
    [Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
    [Property(Tooltip = "How a streamed placement of this prefab is drawn from the far ring: a low-detail mesh or a box")]
    public FarLook? Far;

    // Set when content loading checked this body (PrefabChecks, issue #22): what is wrong with it was
    // said then, at its line, so a spawn says it again only at Debug rather than once per goblin.
    [System.Text.Json.Serialization.JsonIgnore] public bool CheckedAtLoad { get; set; }
}

// A prefab part, declared (REDESIGN §3.4, issue #17). The class *is* the part's options: its public
// fields are what a prefab may write under the part's key, so the inspector, a schema and `ent_types`
// can list them, where the old string-keyed delegates hid their options in private classes.
//
//   [PrefabPart("light", Plugin = "sage.gameplay.lights")]
//   public sealed class LightPart : IPrefabPart
//   {
//       public Vector3 Colour = Vector3.One;
//       public float Range = 8f;
//       public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new PointLight { … });
//   }
//
// Sage.Generators registers it for its plugin just before that plugin's Init, like a [Record]. A new
// instance is read from the prefab's JSON for every entity it is applied to, so Apply may treat its own
// fields as this entity's options and nothing else. What a part needs beyond them — the renderer, a
// cvar — it asks `ctx.Get<T>()` for at Apply time, under the same rule as `ModuleContext.Get`, rather
// than capturing a module's fields (box_mesh used to close over a `_renderer` set in Start).
public interface IPrefabPart
{
    void Apply(in PrefabPartContext ctx);
}

// What a part is applying to, and where to report trouble.
public readonly struct PrefabPartContext
{
    private readonly PrefabPartInfo _part;

    internal PrefabPartContext(PrefabPartInfo part, World world, Entity entity, JsonNode? options, string where)
    {
        _part = part;
        World = world;
        Entity = entity;
        Options = options;
        Where = where;
    }

    public World World { get; }
    public Entity Entity { get; }

    // The prefab's id, for messages ("sandbox:goblin").
    public string Where { get; }

    // What the prefab wrote, as written. Rarely needed: the part's own fields already hold it.
    public JsonNode? Options { get; }

    public string Part => _part.Id;

    // A service the part's plugin may use: one the host provides, or one from a plugin it depends on
    // (ModuleContext.Get's rule, 01 §4). Services from Start exist by the time any prefab is spawned.
    public T Get<T>() where T : class => World.Engine is { } engine
        ? engine.Modules.GetService<T>(_part.Owner)
        : throw new InvalidOperationException($"{Where}: prefab part '{Part}' asked for {typeof(T).Name} in a world with no engine");

    public void Error(string message) => Log.Error(LogCat.Records, $"{Where}: {Part}: {message}");
    public void Warn(string message) => Log.Warn(LogCat.Records, $"{Where}: {Part}: {message}");
}

// One registered part: its id, its options type, what it runs after and the plugin that registered it.
public sealed class PrefabPartInfo
{
    private readonly Action<PrefabPartInfo, World, Entity, JsonNode?, string, bool> _apply;

    internal PrefabPartInfo(string id, Type type, string[] after, string? shorthand, string owner,
                            Action<PrefabPartInfo, World, Entity, JsonNode?, string, bool> apply)
    {
        Id = id;
        Type = type;
        After = after;
        Shorthand = shorthand;
        Owner = owner;
        _apply = apply;
    }

    public string Id { get; }
    public Type Type { get; }
    public IReadOnlyList<string> After { get; }
    public string? Shorthand { get; }
    public string Owner { get; }

    // `reported`: the load already checked these options (PrefabChecks), so a read failure is Debug.
    internal void Apply(World world, Entity entity, JsonNode? options, string where, bool reported = false) =>
        _apply(this, world, entity, options, where, reported);
}

// The parts the loaded plugins declare, and the one order they are applied in.
//
// **Order** (issue #17): a part runs after the parts its [PrefabPart] names in `After`, and otherwise in
// id order (ordinal). Not the order the prefab writes them — a `base` prefab's parts merge in ahead of
// the child's, so JSON order is an accident of inheritance — and not module registration order either,
// which changed whenever a game turned a plugin off. A named part that is not installed is no
// constraint; a cycle is an error when the order is first needed.
public sealed class PrefabRegistry
{
    private readonly Dictionary<string, PrefabPartInfo> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _optional = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PrefabPartInfo>? _ordered;

    // Closed when the first world exists (SageApp.CreateWorld): prefabs may have spawned without it.
    public RegistrationSeal Seal { get; } = new("prefab part", "prefabs may already have spawned without it");

    // Who registered each part (issue #12); set by the Engine.
    public RegistrationLedger? Ledger { get; set; }

    // Registers a declared part. Generated code calls this for each plugin's [PrefabPart] types; a test
    // or a tool may call it by hand. A second part with the same id is an error, naming both: the old
    // warn-and-replace let whichever plugin loaded last win without anyone choosing it.
    public void Register<T>() where T : class, IPrefabPart, new()
    {
        var declared = typeof(T).GetCustomAttribute<PrefabPartAttribute>(inherit: false)
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not declared with [PrefabPart(\"id\")]");
        string id = declared.Id;
        Seal.Check(id);
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"{typeof(T).Name}: a prefab part needs an id");
        if (_byId.TryGetValue(id, out var existing))
            throw new InvalidOperationException(
                $"Prefab part '{id}' is declared twice: {existing.Type.Name} (from {existing.Owner}) and " +
                $"{typeof(T).Name} (from {Ledger?.Owner ?? "host"}). Part ids are unique; rename one.");

        _byId[id] = new PrefabPartInfo(id, typeof(T), declared.After, declared.Shorthand, Ledger?.Owner ?? "host", ApplyAs<T>);
        _ordered = null;
        Ledger?.Record("prefab part", id);
    }

    // The parts in the order Populate applies them.
    public IReadOnlyList<PrefabPartInfo> Parts => _ordered ??= Order();

    public bool TryGet(string id, out PrefabPartInfo part) => _byId.TryGetValue(id, out part!);

    // A part that may legitimately be absent: one whose module is not loaded in this configuration.
    // A dedicated server has no renderer, so a prefab asking for a mesh should get no mesh, not an
    // error — but a *typo* still should. Declared by the half that is always present (R15).
    public void Optional(string name) => _optional.Add(name);

    public bool IsOptional(string name) => _optional.Contains(name);

    // Every part declared optional, sorted: a schema names them even when it cannot describe them (#21).
    public IEnumerable<string> OptionalNames => _optional.OrderBy(n => n, StringComparer.Ordinal);

    public IEnumerable<string> Names
    {
        get { foreach (var p in Parts) yield return p.Id; }
    }

    private static void ApplyAs<T>(PrefabPartInfo info, World world, Entity entity, JsonNode? options, string where, bool reported)
        where T : class, IPrefabPart, new()
    {
        var part = Read<T>(world, options, info, where, reported);
        part.Apply(new PrefabPartContext(info, world, entity, options, where));
    }

    // Reads a part's options as T. An absent or empty body gives the defaults, which is what `{}` means:
    // "yes, this part, nothing to say about it". A bare value fills the declared shorthand field.
    private static T Read<T>(World world, JsonNode? options, PrefabPartInfo info, string where, bool reported) where T : class, new()
    {
        if (options is null) return new T();
        var json = world.Engine?.Records.Json;
        if (json is null) return new T();
        if (options is not JsonObject && info.Shorthand != null)
            options = new JsonObject { [info.Shorthand] = options.DeepClone() };
        try
        {
            return options.Deserialize<T>(json) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            string message = $"{where}: prefab part '{info.Id}': {ex.Message}";
            if (reported) Log.Debug(LogCat.Records, message);
            else Log.Error(LogCat.Records, message);
            return new T();
        }
    }

    // Stable topological order: `After` edges between installed parts, ties by id.
    private List<PrefabPartInfo> Order()
    {
        var incoming = _byId.Values.ToDictionary(p => p, _ => 0);
        var edges = _byId.Values.ToDictionary(p => p, _ => new List<PrefabPartInfo>());
        foreach (var part in _byId.Values)
            foreach (string first in part.After)
                if (_byId.TryGetValue(first, out var before) && before != part)
                {
                    edges[before].Add(part);
                    incoming[part]++;
                }

        var ready = new SortedSet<PrefabPartInfo>(incoming.Where(kv => kv.Value == 0).Select(kv => kv.Key),
            Comparer<PrefabPartInfo>.Create((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id)));
        var result = new List<PrefabPartInfo>(_byId.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            result.Add(next);
            foreach (var to in edges[next])
                if (--incoming[to] == 0) ready.Add(to);
        }
        if (result.Count != _byId.Count)
            throw new InvalidOperationException("Prefab parts name each other in `After` in a cycle: " +
                string.Join(", ", incoming.Where(kv => kv.Value > 0).Select(kv => kv.Key.Id).OrderBy(id => id, StringComparer.Ordinal)));
        return result;
    }
}

// A prefab's body, checked when content loads rather than when the first one spawns (issue #22):
// every component by id and every field it writes, every tag, every part by id and its options, and
// every record and asset they name — each error at its file:line:column. A prefab that fails a check
// still loads, and spawns without what was wrong with it, which is what spawning always did; the
// difference is that the error arrives with the content, not with the first goblin.
internal static class PrefabChecks
{
    public static void Check(Engine engine, PrefabRecord prefab, RecordCheck check)
    {
        prefab.CheckedAtLoad = true;
        CheckBody(engine, prefab.Components, prefab.Tags, prefab.Parts, check.Id.Namespace, "", check);
        PrefabOverriding.CheckChildren(engine, prefab, check);   // phase 4i: children, their overrides, nesting
    }

    // A body's halves wherever they are written: a prefab's own (`prefix` empty) or a placement's
    // overrides (`Place[2].Overrides`), with bare ids and component names in `ns`.
    public static void CheckBody(Engine engine, JsonObject? components, IReadOnlyList<string>? tags, JsonObject? parts,
                                 string ns, string prefix, RecordCheck check)
    {
        var schema = engine.Components;
        var json = check.Json;
        if (prefix.Length > 0 && !prefix.EndsWith('.')) prefix += ".";

        if (components != null)
            foreach (var (name, fields) in components)
            {
                string path = JsonMembers.Child(prefix + "Components", name);
                if (!schema.TryResolveComponent(name, ns, out var type, out string? error)) { check.Error(path, error); continue; }
                if (fields is null) continue;
                if (!check.CheckFields(fields, type, path)) continue;
                if (Read(fields, type, json, path, check) is { } value) check.CheckValues(value, path);
            }

        if (tags != null)
            for (int i = 0; i < tags.Count; i++)
                if (!schema.TryResolveTag(tags[i], ns, out _, out string? error))
                    check.Error($"{prefix}Tags[{i}]", error);

        if (parts == null) return;
        foreach (var (name, options) in parts)
        {
            string path = JsonMembers.Child(prefix + "Parts", name);
            if (!engine.Prefabs.TryGet(name, out var part))
            {
                // A part only another host has (the client's `audio` on a server) is not a mistake.
                if (!engine.Prefabs.IsOptional(name))
                    check.Error(path, $"no prefab part '{name}'" + Spelling.Suggest(name, engine.Prefabs.Names) +
                                      $" (have: {string.Join(", ", engine.Prefabs.Names)})");
                continue;
            }
            if (options is null) continue;

            // A bare value fills the part's shorthand field ("faction": "beasts"), and is checked there.
            if (options is not JsonObject && part.Shorthand != null)
            {
                var member = part.Type.GetField(part.Shorthand);
                var value = Read(new JsonObject { [part.Shorthand] = options.DeepClone() }, part.Type, json, path, check);
                if (value != null && member != null) check.CheckValues(member.GetValue(value), path);
                continue;
            }
            if (!check.CheckFields(options, part.Type, path)) continue;
            if (Read(options, part.Type, json, path, check) is { } read) check.CheckValues(read, path);
        }
    }

    // The type a part's body is read as: the part's options, or its shorthand field's type when the
    // body is a bare value ("effects": ["x"]).
    public static Type? BodyType(PrefabRegistry parts, string key, JsonNode? body)
    {
        if (!parts.TryGet(key, out var part)) return null;
        if (body is JsonObject || part.Shorthand == null) return part.Type;
        return part.Type.GetField(part.Shorthand)?.FieldType;
    }

    private static object? Read(JsonNode node, Type type, System.Text.Json.JsonSerializerOptions json, string path, RecordCheck check)
    {
        try
        {
            return node.Deserialize(type, json);
        }
        catch (JsonException ex)
        {
            // The exception's path is inside the body; the body is at `path` in the record.
            string inner = ex.Path is { Length: > 1 } p && p.StartsWith('$') ? p[1..] : "";
            check.Error(path + inner, $"{JsonMembers.Display(path)}: {WithoutPosition(ex.Message)}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or FormatException)
        {
            check.Error(path, $"{JsonMembers.Display(path)}: {ex.Message}");
        }
        return null;
    }

    private static string WithoutPosition(string message)
    {
        int at = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (at < 0) at = message.IndexOf(" LineNumber: ", StringComparison.Ordinal);
        return at < 0 ? message : message[..at];
    }
}

public static class PrefabExtensions
{
    // The one way to place a thing. Everything a prefab says is applied here, in one order, so a
    // creature is one call rather than eight in a particular sequence spread over five static classes.
    //
    // A prefab that doesn't exist costs the spawn and says so; a component or part that fails costs
    // itself and the rest of the entity still comes up (05 §8).
    public static Entity Spawn(this World world, RecordId prefab, Vector3 position = default, float yawDegrees = 0f) =>
        Spawn(world, prefab, position, yawDegrees, keys: null, where: null);

    // The spawns that give the entity its own identity afterwards (a scene's placements, a saved entity on
    // load) or that are rebuilt by whatever asked for them (a level's entities, the player camera): no
    // runtime PersistentId, so nothing is saved twice.
    internal static Entity SpawnWithoutId(this World world, RecordId prefab, Vector3 position = default, float yawDegrees = 0f,
                                          IReadOnlyDictionary<string, string>? keys = null, string? where = null) =>
        SpawnKeyed(world, prefab, position, yawDegrees, keys, where, persist: false);

    internal static Entity SpawnWithoutId(this World world, RecordId prefab, Vector3 position, float yawDegrees,
                                          PrefabOverrides? overrides, string? where) =>
        SpawnPlaced(world, prefab, position, yawDegrees, overrides, where, persist: false);

    // A saved entity on load, where the save says it stood (4i-5): its position and its whole rotation,
    // and the overrides it was placed with when no content places it any more (#279, KeptPlacement).
    internal static Entity SpawnWithoutId(this World world, RecordId prefab, in Transform placed, PrefabOverrides? overrides = null)
    {
        var at = Transform.At(placed.LocalPosition);
        at.LocalRotation = placed.LocalRotation;
        var entity = SpawnTree(world, prefab, at, overrides, where: null, default, 0);
        if (!entity.IsNull) SnapGlobals(world, entity);
        return entity;
    }

    // A placement (a document's, a scene's, a streamed sector's) at `position` (the simulation's frame):
    // its whole rotation and its scale (issue #367), its overrides, and no runtime id of its own.
    internal static Entity SpawnPlacementWithoutId(this World world, Placement placement, Vector3 position, string? where)
    {
        var placed = Transform.At(position);
        placed.LocalRotation = placement.PlacementRotation();
        placed.LocalScale = placement.Scale;
        var entity = SpawnTree(world, placement.Prefab.Id, placed, placement.Overrides, where, default, 0);
        if (!entity.IsNull) SnapGlobals(world, entity);
        return entity;
    }

    // The same, with per-entity values from a map (`"light.range" "12"`): the keys PrefabKeys offers for
    // this prefab, read as overrides of this one entity (issue #18; since phase 4i the same overrides a
    // placement writes). `where` names the map line.
    public static Entity Spawn(this World world, RecordId prefab, Vector3 position, float yawDegrees,
                               IReadOnlyDictionary<string, string>? keys, string? where) =>
        SpawnKeyed(world, prefab, position, yawDegrees, keys, where, persist: true);

    private static Entity SpawnKeyed(World world, RecordId prefab, Vector3 position, float yawDegrees,
                                     IReadOnlyDictionary<string, string>? keys, string? where, bool persist)
    {
        PrefabOverrides? overrides = null;
        if (keys != null && keys.Count > 0 && world.Engine is { } engine && engine.Records.TryGet(prefab, out PrefabRecord record))
            overrides = PrefabKeys.Overrides(engine, prefab, record, keys, where ?? prefab.ToString());
        return SpawnPlaced(world, prefab, position, yawDegrees, overrides, where, persist);
    }

    // The same, with a placement's overrides (phase 4i, F31): component and part bodies merged into a
    // copy of the prefab for this one entity. The prefab's `children` are spawned with it, parented to it.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // saves you can trust (phase 4i)
    public static Entity Spawn(this World world, RecordId prefab, Vector3 position, float yawDegrees,
                               PrefabOverrides? overrides, string? where = null) =>
        SpawnPlaced(world, prefab, position, yawDegrees, overrides, where, persist: true);

    private static Entity SpawnPlaced(World world, RecordId prefab, Vector3 position, float yawDegrees,
                                      PrefabOverrides? overrides, string? where, bool persist)
    {
        var placed = Transform.At(position);
        placed.LocalRotation = SageMath.RotationFromYaw(yawDegrees * MathF.PI / 180f);
        var entity = SpawnTree(world, prefab, placed, overrides, where, default, 0, persist);
        if (entity.IsNull) return entity;
        SnapGlobals(world, entity);
        // Its prefab's children get ids derived from the root's (4i-3), so a save keeps their state and a
        // load finds them under the respawned parent rather than spawning them a second time.
        if (entity.ChildCount > 0 && world.TryGet(entity, out Persistent root)) ContentIds.Assign(world, entity, root.Id);
        return entity;
    }

    // One prefab and its children. `parent` is null for the root; a child is placed in its frame.
    private static Entity SpawnTree(World world, RecordId prefab, in Transform placed, PrefabOverrides? overrides,
                                    string? where, Entity parent, int depth, bool persist = false)
    {
        var engine = world.Engine;
        if (engine is null)
        {
            Assert.Ensure(false, $"Spawn({prefab}): this world has no engine, so there are no prefabs");
            return default;
        }

        if (!engine.Records.TryGet(prefab, out PrefabRecord record))
        {
            Log.Error(LogCat.Records, $"Spawn: no prefab '{prefab}'" + (where != null ? $" ({where})" : ""));
            return default;
        }

        var asAuthored = record;
        record = PrefabOverriding.Apply(engine, prefab, record, overrides);

        // Placed *before* the body goes on, because parts read the transform: `character` seeds the
        // pawn's yaw from it, and seeding that from an identity rotation would spin every creature to
        // face -Z on its first tick, throwing away the direction it was placed facing (review #43).
        // A placement's scale (#367) multiplies the prefab's own (a Transform it writes; none, or a zero
        // one, is 1), so the entity starts at 1 and is scaled once the body is on.
        var scale = placed.LocalScale;
        bool scaled = scale != Vector3.One && scale != Vector3.Zero;
        var start = placed;
        if (scaled) start.LocalScale = Vector3.One;
        var entity = world.Create(start, string.IsNullOrEmpty(record.Name) ? prefab.Name : record.Name);
        world.Add(entity, new FromPrefab { Prefab = prefab });   // so a save can rebuild it (F27)
        if (!parent.IsNull)
        {
            world.SetParent(entity, parent);
            entity.AddTag<FromParentPrefab>();   // dies with it (World.Destroy)
        }
        if (overrides is { IsEmpty: false })
            world.Add(entity, new PrefabOverridden { Overrides = overrides.Clone() });   // for ent_dump and the editor
        Populate(world, entity, record, prefab);

        // And again after, because placement beats the template: a prefab may write a Transform for a
        // scale it always wants, but it does not get to say where this one went.
        ref var transform = ref world.Get<Transform>(entity);
        transform.LocalPosition = placed.LocalPosition;
        transform.LocalRotation = placed.LocalRotation;
        if (scaled) transform.LocalScale = (transform.LocalScale == Vector3.Zero ? Vector3.One : transform.LocalScale) * scale;

        // What it was spawned as, for a save to diff it against (4i-5): before its children, a name or
        // an id are added, none of which is the prefab's.
        engine.Saves.NoteSpawned(world, entity, prefab, asAuthored, record, overrides);

        // Only the root of a runtime spawn: a child is re-spawned by its parent on load, so a *random* id of
        // its own would duplicate it (FromParentPrefab, #161); SpawnPlaced derives the children's from this.
        // After the baseline, because the id comes with the cell it was made in (4g-1, InCell), which is not
        // the prefab's: in the baseline it would never be written, and a load would lose it.
        if (persist && parent.IsNull)
        {
            if (record.Persist) world.MakePersistent(entity);
            else entity.AddTag<Unsaved>();   // no save names it, so a load takes it away (4m-4)
        }

        if (record.Children.Count == 0) return entity;
        if (depth >= PrefabOverriding.MaxDepth)
        {
            // The load check says this at the prefab's line; this is the guard for content it did not see.
            Log.Error(LogCat.Records, $"{prefab}: prefabs nest deeper than {PrefabOverriding.MaxDepth}; its children are not spawned");
            return entity;
        }
        for (int i = 0; i < record.Children.Count; i++)
        {
            var child = record.Children[i];
            if (child == null || child.Prefab.Id.IsEmpty) continue;
            var local = Transform.At(child.At);
            local.LocalRotation = SageMath.RotationFromYaw(child.Yaw * MathF.PI / 180f);
            var spawned = SpawnTree(world, child.Prefab.Id, local, child.Overrides, $"{prefab}: child {child.Prefab.Id}", entity, depth + 1);
            if (spawned.IsNull) continue;
            if (!string.IsNullOrEmpty(child.Name)) spawned.Name = child.Name;
            // Its place in the parent, from which its persistent id is derived (4i-3, ContentIds.Assign).
            world.Add(spawned, new PrefabChildKey { Key = PrefabChildKeys.Of(i, child) });
        }
        return entity;
    }

    // A new tree's global poses, both halves, from its transforms: a child starts where its parent put it
    // rather than interpolating in from the parent's origin on the first frame.
    private static void SnapGlobals(World world, Entity entity)
    {
        if (!world.Has<GlobalTransform>(entity)) return;
        var local = Pose.FromLocal(world.Get<Transform>(entity));
        var pose = entity.Parent.IsNull || !world.Has<GlobalTransform>(entity.Parent)
            ? local
            : Pose.Combine(world.Get<GlobalTransform>(entity.Parent).Current, local);
        world.Get<GlobalTransform>(entity) = GlobalTransform.At(pose);
        foreach (var child in entity.ChildEntities) SnapGlobals(world, child);
    }

    // Applies a prefab to an entity that already exists. Split out because a map load (F27) places an
    // entity with its saved id and then wants the prefab's body on it.
    //
    // `id` is the prefab's own, and it matters: a bare record id written inside a component or a part
    // ("attack": "claw") means one in the prefab's namespace, the same rule record fields follow
    // (05 §3.5). The record pipeline qualifies its own fields at merge time (review #56) but cannot
    // see inside these bodies, because their shape isn't known until the component type is.
    public static void Populate(this World world, Entity entity, PrefabRecord record, RecordId id)
    {
        var engine = world.Engine;
        if (engine is null) return;
        var schema = engine.Components;
        string where = id.ToString();

        string? outerNamespace = RecordParseContext.Namespace;
        RecordParseContext.Namespace = id.Namespace;
        try
        {
            Build(world, entity, record, schema, engine, id.Namespace, where);
        }
        finally
        {
            RecordParseContext.Namespace = outerNamespace;
        }
    }

    private static void Build(World world, Entity entity, PrefabRecord record, ComponentSchema schema,
                              Engine engine, string ns, string where)
    {
        if (record.Components != null)
        {
            foreach (var (name, fields) in record.Components)
            {
                // By id, or a bare name in the prefab's namespace and then `sage` (ComponentSchema,
                // issue #16). An old C# type name is an error that names the id to write instead.
                if (!schema.TryResolveComponent(name, ns, out var type, out string? error))
                {
                    Said(record, $"{where}: {error}");
                    continue;
                }
                schema.Add(entity, type, fields, where, record.CheckedAtLoad);
            }
        }

        foreach (var name in record.Tags)
        {
            if (!schema.TryResolveTag(name, ns, out var tag, out string? error)) { Said(record, $"{where}: {error}"); continue; }
            schema.AddTag(entity, tag);
        }

        if (record.Parts == null) return;

        var written = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, options) in record.Parts) written[name] = options;

        // Parts run in the registry's order, not the order they appear in the file: "effects" needs the
        // attributes they modify, and "pickup" only adds a sprite and a collider when nothing did
        // (PrefabRegistry says how the order is decided).
        foreach (var part in engine.Prefabs.Parts)
        {
            if (!written.Remove(part.Id, out var options)) continue;
            try
            {
                part.Apply(world, entity, options, where, record.CheckedAtLoad);
            }
            catch (Exception ex)
            {
                Log.Error(LogCat.Records, $"{where}: prefab part '{part.Id}' failed: {ex.Message}");
            }
        }

        // Whatever is left was never registered: a typo, or a module that isn't loaded. A part
        // declared optional is the second case on purpose — a headless run has no renderer, and a
        // prefab asking for a mesh there should quietly get no mesh.
        foreach (var name in written.Keys)
        {
            if (engine.Prefabs.IsOptional(name))
                Log.Debug(LogCat.Records, $"{where}: prefab part '{name}' is not installed here; skipped");
            else
                Said(record, $"{where}: no prefab part '{name}' (have: {string.Join(", ", engine.Prefabs.Names)})");
        }
    }

    // A problem with the body: an error, unless the load already reported it where it is written.
    private static void Said(PrefabRecord record, string message)
    {
        if (record.CheckedAtLoad) Log.Debug(LogCat.Records, message);
        else Log.Error(LogCat.Records, message);
    }
}
