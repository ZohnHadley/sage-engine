#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Friflo.Engine.ECS;

namespace sage_engine;

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
[Record("prefab", Plugin = RegistrationOwners.Core)]
public sealed class PrefabRecord
{
    public string Name = "";              // what World.Describe calls it; empty = the record id
    public JsonObject? Components;
    public List<string> Tags = new();
    public JsonObject? Parts;
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

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PrefabPartAttribute : Attribute
{
    public PrefabPartAttribute(string id) { Id = id; }

    // The key a prefab writes under "parts". One id, one part: a second is an error (issue #17).
    public string Id { get; }

    // The plugin that registers it; inferred when the assembly has one [Plugin] (SAGE0011).
    public string? Plugin { get; set; }

    // Parts that must have been applied first, by id: `pickup` checks for a sprite and a collider and
    // only adds its own when there are none. Parts otherwise run in id order (see PrefabRegistry).
    public string[] After { get; set; } = Array.Empty<string>();

    // A field a bare value fills: `"faction": "beasts"` is `"faction": { "id": "beasts" }` when this is
    // "Id". A part that takes one value should not make every prefab write an object for it.
    public string? Shorthand { get; set; }
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
    private readonly Action<PrefabPartInfo, World, Entity, JsonNode?, string> _apply;

    internal PrefabPartInfo(string id, Type type, string[] after, string? shorthand, string owner,
                            Action<PrefabPartInfo, World, Entity, JsonNode?, string> apply)
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

    internal void Apply(World world, Entity entity, JsonNode? options, string where) =>
        _apply(this, world, entity, options, where);
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

    public IEnumerable<string> Names
    {
        get { foreach (var p in Parts) yield return p.Id; }
    }

    private static void ApplyAs<T>(PrefabPartInfo info, World world, Entity entity, JsonNode? options, string where)
        where T : class, IPrefabPart, new()
    {
        var part = Read<T>(world, options, info, where);
        part.Apply(new PrefabPartContext(info, world, entity, options, where));
    }

    // Reads a part's options as T. An absent or empty body gives the defaults, which is what `{}` means:
    // "yes, this part, nothing to say about it". A bare value fills the declared shorthand field.
    private static T Read<T>(World world, JsonNode? options, PrefabPartInfo info, string where) where T : class, new()
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
            Log.Error(LogCat.Records, $"{where}: prefab part '{info.Id}': {ex.Message}");
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

public static class PrefabExtensions
{
    // The one way to place a thing. Everything a prefab says is applied here, in one order, so a
    // creature is one call rather than eight in a particular sequence spread over five static classes.
    //
    // A prefab that doesn't exist costs the spawn and says so; a component or part that fails costs
    // itself and the rest of the entity still comes up (05 §8).
    public static Entity Spawn(this World world, RecordId prefab, Vector3 position = default, float yawDegrees = 0f) =>
        Spawn(world, prefab, position, yawDegrees, keys: null, where: null);

    // The same, with per-entity values from a map (`"light.range" "12"`): the keys PrefabKeys offers for
    // this prefab, applied to a copy of it for this one entity (issue #18). `where` names the map line.
    public static Entity Spawn(this World world, RecordId prefab, Vector3 position, float yawDegrees,
                               IReadOnlyDictionary<string, string>? keys, string? where)
    {
        var engine = world.Engine;
        if (engine is null)
        {
            Assert.Ensure(false, $"Spawn({prefab}): this world has no engine, so there are no prefabs");
            return default;
        }

        if (!engine.Records.TryGet(prefab, out PrefabRecord record))
        {
            Log.Error(LogCat.Records, $"Spawn: no prefab '{prefab}'");
            return default;
        }

        if (keys != null && keys.Count > 0)
            record = PrefabKeys.Apply(engine, prefab, record, keys, where ?? prefab.ToString());

        var placed = Transform.At(position);
        placed.LocalRotation = SageMath.RotationFromYaw(yawDegrees * MathF.PI / 180f);

        // Placed *before* the body goes on, because parts read the transform: `character` seeds the
        // pawn's yaw from it, and seeding that from an identity rotation would spin every creature to
        // face -Z on its first tick, throwing away the direction it was placed facing (review #43).
        var entity = world.Create(placed, string.IsNullOrEmpty(record.Name) ? prefab.Name : record.Name);
        world.Add(entity, new FromPrefab { Prefab = prefab });   // so a save can rebuild it (F27)
        Populate(world, entity, record, prefab);

        // And again after, because placement beats the template: a prefab may write a Transform for a
        // scale it always wants, but it does not get to say where this one went.
        ref var transform = ref world.Get<Transform>(entity);
        transform.LocalPosition = placed.LocalPosition;
        transform.LocalRotation = placed.LocalRotation;
        return entity;
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
                    Log.Error(LogCat.Records, $"{where}: {error}");
                    continue;
                }
                schema.Add(entity, type, fields, where);
            }
        }

        foreach (var name in record.Tags)
        {
            if (!schema.TryResolveTag(name, ns, out var tag, out string? error)) { Log.Error(LogCat.Records, $"{where}: {error}"); continue; }
            var tags = new Tags(tag);
            entity.AddTags(tags);
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
                part.Apply(world, entity, options, where);
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
                Log.Error(LogCat.Records, $"{where}: no prefab part '{name}' (have: {string.Join(", ", engine.Prefabs.Names)})");
        }
    }
}
