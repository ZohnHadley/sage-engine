#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
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
//   "components": component data by type name, applied as written (09 §3.1);
//   "parts":      named setups a module registered, for the things that are not one component.
//
// The split is the honest one. A `SpriteRenderer` is data. "Make this a character" is a collider, a
// controller, an intent and ground state that have to agree, so it is a part `PhysicsModule` owns —
// and adding a feature means registering a part, not editing this record.
//
//   { "type": "prefab", "id": "goblin", "name": "goblin",
//     "components": { "SpriteRenderer": { "sheet": "sage:goblin", "size": [1.6, 1.9] } },
//     "tags": ["Hostile"],
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

// A named setup a module knows how to apply. `Name` is the key under "parts"; `options` is whatever
// was written there, which may be any JSON the part cares to read (an object, a list, or `{}`).
public interface IPrefabPart
{
    string Name { get; }
    void Apply(World world, Entity entity, JsonNode? options, string where);
}

// The parts every module has registered, in registration order — which is module dependency order
// (01 §3.1), so physics has made a character before gameplay hangs an attack on it.
public sealed class PrefabRegistry
{
    private readonly List<IPrefabPart> _parts = new();
    private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _optional = new(StringComparer.OrdinalIgnoreCase);

    // Closed when the first world exists (SageApp.CreateWorld): prefabs may have spawned without it.
    public RegistrationSeal Seal { get; } = new("prefab part", "prefabs may already have spawned without it");

    // Who registered each part (issue #12); set by the Engine.
    public RegistrationLedger? Ledger { get; set; }

    public void Register(IPrefabPart part)
    {
        Seal.Check(part.Name);
        if (_byName.TryGetValue(part.Name, out int existing))
        {
            string? previous = Ledger?.OwnerOf("prefab part", part.Name);
            Log.Warn(LogCat.Records, $"Prefab part '{part.Name}' registered twice; {_parts[existing].GetType().Name}" +
                $"{(previous != null ? $" (from {previous})" : "")} replaced by {part.GetType().Name}" +
                $"{(Ledger != null ? $" (from {Ledger.Owner})" : "")}");
            _parts[existing] = part;
            Ledger?.Record("prefab part", part.Name);
            return;
        }
        _byName[part.Name] = _parts.Count;
        _parts.Add(part);
        Ledger?.Record("prefab part", part.Name);
    }

    // A small helper so a module can register a part without declaring a type for it.
    public void Register(string name, Action<World, Entity, JsonNode?, string> apply) =>
        Register(new Lambda(name, apply));

    public IReadOnlyList<IPrefabPart> Parts => _parts;

    // A part that may legitimately be absent: one whose module is not loaded in this configuration.
    // A dedicated server has no renderer, so a prefab asking for a mesh should get no mesh, not an
    // error — but a *typo* still should. Declared by the half that is always present (R15).
    public void Optional(string name) => _optional.Add(name);

    public bool IsOptional(string name) => _optional.Contains(name);

    public IEnumerable<string> Names
    {
        get { foreach (var p in _parts) yield return p.Name; }
    }

    private sealed class Lambda : IPrefabPart
    {
        private readonly Action<World, Entity, JsonNode?, string> _apply;
        public Lambda(string name, Action<World, Entity, JsonNode?, string> apply) { Name = name; _apply = apply; }
        public string Name { get; }
        public void Apply(World world, Entity entity, JsonNode? options, string where) => _apply(world, entity, options, where);
    }
}

public static class PrefabExtensions
{
    // The one way to place a thing. Everything a prefab says is applied here, in one order, so a
    // creature is one call rather than eight in a particular sequence spread over five static classes.
    //
    // A prefab that doesn't exist costs the spawn and says so; a component or part that fails costs
    // itself and the rest of the entity still comes up (05 §8).
    public static Entity Spawn(this World world, RecordId prefab, Vector3 position = default, float yawDegrees = 0f)
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
            Build(world, entity, record, schema, engine, where);
        }
        finally
        {
            RecordParseContext.Namespace = outerNamespace;
        }
    }

    private static void Build(World world, Entity entity, PrefabRecord record, ComponentSchema schema,
                              Engine engine, string where)
    {
        if (record.Components != null)
        {
            foreach (var (name, fields) in record.Components)
            {
                if (!schema.TryComponent(name, out var type))
                {
                    Log.Error(LogCat.Records, $"{where}: no component type '{name}' (see `ent_types`)");
                    continue;
                }
                schema.Add(entity, type, fields, where);
            }
        }

        foreach (var name in record.Tags)
        {
            if (!schema.TryTag(name, out var tag)) { Log.Error(LogCat.Records, $"{where}: no tag '{name}'"); continue; }
            var tags = new Tags(tag);
            entity.AddTags(tags);
        }

        if (record.Parts == null) return;

        var written = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, options) in record.Parts) written[name] = options;

        // Parts run in the order their modules registered them, not the order they appear in the
        // file: "character" has to have built the capsule before "melee" gives it something to swing.
        foreach (var part in engine.Prefabs.Parts)
        {
            if (!written.Remove(part.Name, out var options)) continue;
            try
            {
                part.Apply(world, entity, options, where);
            }
            catch (Exception ex)
            {
                Log.Error(LogCat.Records, $"{where}: prefab part '{part.Name}' failed: {ex.Message}");
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
