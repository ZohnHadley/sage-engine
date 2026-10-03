#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Spawners (issue #281): Source's env_entity_maker and point_template, HL1's env_spawn-style makers. A
// `Spawn` input places a prefab, or a whole template of placements with their wires, where the spawner
// stands; a wave of headcrabs, a crate from a dispenser, a bridge of planks wired to each other.
//
//   component  "sage:spawner": { "spawned": 0, "limit": 3, "disabled": false }
//   part       "spawner": { "prefab": "crate", "place": [ … ], "limit": 3, "uniqueNames": true, "startDisabled": false }
//   prefab     sage:logic_spawner — an entity with nothing but a spawner; a placement says what it spawns
//
//   inputs   Spawn, Enable, Disable
//   output   OnSpawned   it spawned; the activator is the first thing it spawned (so a wire to !activator
//                        reaches it), and the value how many times it has spawned
//
// **The template.** `place` is a list of placements as a scene writes them — prefab, at, yaw, name,
// outputs, overrides — measured from the spawner (metres in its frame, turned as it is, `yaw` added to
// its heading); `prefab` on its own is one more, at the spawner. Each spawn places all of them.
//
// **Names.** As written, a second spawn has two entities of each name, and a wire by name reaches one of
// them. With `uniqueNames`, every spawn's names get `#n` (the spawn's number: "plank#2"), and a wire in
// the template whose target names another member of it is pointed at that spawn's member: point_template's
// name fix-up, so each copy of a wired group is wired to itself.
//
// **Saved.** What it spawned is a runtime spawn with an identity of its own, saved with its overrides and
// wires (KeptPlacement); the spawner's count is saved, so `limit` holds across a load. The template is
// content, in a [Transient] component its part puts back.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Component("sage:spawner")]
public struct LogicSpawner : IComponent
{
    [Property(Min = 0, Tooltip = "How many times it has spawned")]
    public int Spawned;
    [Property(Min = 0, Tooltip = "How many times it may spawn; 0 = no limit")]
    public int Limit;
    [Property(Tooltip = "Ignores Spawn until Enable")]
    public bool Disabled;
}

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[Transient]
[Component("sage:spawner_template")]
public struct LogicSpawnerTemplate : IComponent
{
    public Placement[]? Place;
    public bool UniqueNames;
}

// "spawner": { "prefab": "crate", "place": [ { "prefab": "plank", "at": [0, 0, 2], "name": "plank" } ], "limit": 0, "uniqueNames": false }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
[PrefabPart("spawner", Plugin = RegistrationOwners.Core, Shorthand = nameof(Prefab))]
public sealed class LogicSpawnerPart : IPrefabPart
{
    [Property(Tooltip = "A prefab Spawn places at the spawner (beside the `place` template, if any)")]
    public RecordRef<PrefabRecord> Prefab;
    [Property(Tooltip = "The template: placements Spawn places, measured from the spawner and turned as it is, with their wires")]
    public List<Placement> Place = new();
    [Property(Min = 0, Tooltip = "How many times it may spawn; 0 = no limit")]
    public int Limit;
    [Property(Tooltip = "Each spawn's names get #n, and the template's wires between its members follow (point_template's fix-up)")]
    public bool UniqueNames;
    [Property(Tooltip = "Starts disabled: Spawn does nothing until Enable")]
    public bool StartDisabled;

    public void Apply(in PrefabPartContext ctx)
    {
        var place = new List<Placement>(Place.Count + 1);
        if (!Prefab.IsEmpty) place.Add(new Placement { Prefab = Prefab });
        foreach (var placement in Place)
        {
            if (placement == null || placement.Prefab.IsEmpty) { ctx.Warn("a template placement with no prefab is skipped"); continue; }
            if (placement.RelativeTo is { } frame && frame != PlacementFrame.World)
                ctx.Warn($"a template placement's relativeTo ({frame}) is ignored: it is measured from the spawner");
            place.Add(placement);
        }
        if (place.Count == 0) ctx.Warn("it has no prefab and no template: Spawn will do nothing");
        if (Limit < 0) ctx.Warn($"limit {Limit} is not a count; 0 (no limit) is used");
        ctx.World.Add(ctx.Entity, new LogicSpawner { Limit = Math.Max(0, Limit), Disabled = StartDisabled });
        ctx.World.Add(ctx.Entity, new LogicSpawnerTemplate { Place = place.ToArray(), UniqueNames = UniqueNames });
    }
}

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // logic entities (#91)
public static class Spawners
{
    public const string SpawnInput = "Spawn";
    public const string OnSpawned = "OnSpawned";

    internal static void Register(Engine engine)
    {
        engine.Inputs.Register<LogicSpawner>(SpawnInput, static (World world, in IOContext io) => Spawn(world, io.Self));
        engine.Inputs.Register<LogicSpawner>("Enable", static (World world, in IOContext io) => world.Get<LogicSpawner>(io.Self).Disabled = false);
        engine.Inputs.Register<LogicSpawner>("Disable", static (World world, in IOContext io) => world.Get<LogicSpawner>(io.Self).Disabled = true);
        engine.Outputs.Declare(OnSpawned, "This spawner spawned (sage:spawner); the activator is the first thing it spawned, the value how many times it has.");
    }

    // One spawn of the template, where the spawner stands; the first entity spawned, or the null entity
    // when it is disabled, used up or has nothing to spawn. For C#; the Spawn input ends up here.
    public static Entity Spawn(World world, Entity spawner)
    {
        ref var state = ref world.Get<LogicSpawner>(spawner);
        if (state.Disabled) return default;
        if (state.Limit > 0 && state.Spawned >= state.Limit)
        {
            Log.Debug(LogCat.Events, $"Spawn at {World.Describe(spawner)}: it has spawned its limit ({state.Limit})");
            return default;
        }
        if (!world.TryGet<LogicSpawnerTemplate>(spawner, out var template) || template.Place is not { Length: > 0 } place) return default;

        int number = ++state.Spawned;
        var where = world.TryGet<GlobalTransform>(spawner, out var global) ? global.Current : default;
        var rotation = where.Rotation == default ? Quaternion.Identity : where.Rotation;
        float heading = SageMath.YawOf(rotation) * 180f / MathF.PI;
        string suffix = template.UniqueNames ? "#" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

        Entity first = default;
        foreach (var placement in place)
        {
            var at = where.Position + Vector3.Transform(placement.At, rotation);
            var entity = world.Spawn(placement.Prefab.Id, at, heading + placement.Yaw, placement.Overrides, $"spawner {World.Describe(spawner)}");
            if (entity.IsNull) continue;
            if (placement.Name.Length > 0) entity.Name = placement.Name + suffix;
            PlacementWires.Attach(world, entity, placement);
            if (suffix.Length > 0 && world.TryGet<IOConnections>(entity, out var io) && io.Wires != null)
                foreach (var wire in io.Wires)
                    if (Member(place, wire.Target)) wire.Target += suffix;
            if (first.IsNull) first = entity;
        }
        // The spawner may have gone with what it spawned (a `then`); its state is written already.
        if (world.IsAlive(spawner)) world.FireOutput(spawner, OnSpawned, first, number);
        return first;
    }

    private static bool Member(Placement[] place, string name)
    {
        if (name.Length == 0) return false;
        foreach (var placement in place)
            if (string.Equals(placement.Name, name, StringComparison.Ordinal)) return true;
        return false;
    }
}
