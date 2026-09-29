#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// What stands where (docs/design/15 §3, TODO F28/F31).
//
// **The thing an editor edits, and what a scene places** (issue #29). Levels are brushes and
// TrenchBroom owns those (15 §10a); prefabs say what a thing *is*; this says where the things are. There
// used to be two answers — the Sandbox's own `scene` with relative, lower-case `at`, and this one with
// absolute positions — and now there is one: a `placements` document and a `scene` (Scenes.cs) both hold
// a list of these, written in camel case like every other record file.
//
// It is deliberately small. A placement is not a prefab with extras: per-entity overrides are F31's
// remaining work and want the serializer's field metadata (09 §3.2), so this is the part that can be
// written honestly today.
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public sealed class Placement
{
    [Property(Tooltip = "What to place")]
    public RecordRef<PrefabRecord> Prefab;
    [Property(Unit = "m", Tooltip = "Where, in the frame `relativeTo` names: absolute metres by default")]
    public Vector3 At;                 // converted to the simulation's frame on spawn (R6)
    [Property(Unit = "deg", Tooltip = "Degrees about +Y; 0 faces -Z")]
    public float Yaw;                  // degrees about +Y, 0 facing -Z
    [Property(Tooltip = "Optional, so an outliner, ent_list and a wire can name it")]
    public string Name = "";
    [Property(Tooltip = "What `at` is measured from; left out, the document's or scene's own `relativeTo`")]
    public PlacementFrame? RelativeTo;
    [Property(Tooltip = "Entity I/O wires from this entity's outputs to other entities' inputs")]
    public List<Connection> Outputs = new();
}

// What a placement's `at` is measured from (issue #29).
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public enum PlacementFrame
{
    World,    // absolute metres
    Origin,   // metres from the document's or scene's `origin`
    Ground,   // from the `origin` across, and y is height above the terrain there (the origin's y without terrain)
}

[Record("placements", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public sealed class PlacementsRecord
{
    [Property(Unit = "m", Tooltip = "Absolute metres that `Origin` and `Ground` placements are measured from")]
    public Vector3 Origin;
    [Property(Tooltip = "What a placement's `at` is measured from when it does not say")]
    public PlacementFrame RelativeTo = PlacementFrame.World;
    [Property(Tooltip = "What stands where")]
    public List<Placement> Place = new();
}

// Marks an entity that came from a placements document, and which one — so an editor can tell what it
// is responsible for from what the game or a level spawned, and a reload can sweep only its own.
[Component("sage:from_placements")]
public struct FromPlacements : IComponent
{
    public RecordId Document;
}

[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public static class PlacementExtensions
{
    // Spawns a document's placements. Returns how many arrived; a prefab that does not exist is logged
    // by `Spawn` and skipped, because one bad line in a file should cost that line.
    public static int SpawnPlacements(this World world, RecordId document)
    {
        var engine = world.Engine!;
        if (!engine.Records.TryGet(document, out PlacementsRecord record))
        {
            Log.Error(LogCat.Editor, $"No placements record '{document}'");
            return 0;
        }
        return world.SpawnPlacements(document, record);
    }

    public static int SpawnPlacements(this World world, RecordId document, PlacementsRecord record)
    {
        int spawned = 0;
        foreach (var placement in record.Place)
        {
            var at = world.PlacementPosition(placement, record.Origin, record.RelativeTo);
            var entity = world.Spawn(placement.Prefab, at, placement.Yaw);
            if (entity.IsNull) continue;

            if (!string.IsNullOrEmpty(placement.Name)) entity.Name = placement.Name;
            PlacementWires.Attach(world, entity, placement);
            world.Add(entity, new FromPlacements { Document = document });
            spawned++;
        }

        Log.Info(LogCat.Editor, $"Placements '{document}': {spawned} of {record.Place.Count} placed in '{world.Name}'");
        return spawned;
    }

    // Where a placement stands, in the simulation's frame (issue #29): `at` measured from what its
    // `relativeTo` names (else the document's or scene's own), then converted from absolute metres to
    // the frame the world is using now (R6). Without that conversion, dying after travelling a hundred
    // kilometres would put the player back at the scene's numbers *in the current origin* — a place the
    // scene has never been.
    public static Vector3 PlacementPosition(this World world, Placement placement, Vector3 origin, PlacementFrame frame)
    {
        switch (placement.RelativeTo ?? frame)
        {
            case PlacementFrame.Origin:
                return world.Origin().ToOrigin(origin + placement.At);
            case PlacementFrame.Ground:
            {
                var position = world.Origin().ToOrigin(origin + (placement.At with { Y = 0f }));
                // Terrain is sage.streaming's: a game without it stands things on the origin's height.
                float ground = world.Resources.TryGet<Terrain>(out var terrain) && terrain != null
                    ? terrain.HeightAt(position.X, position.Z)
                    : position.Y;
                return position with { Y = ground + placement.At.Y };
            }
            default:
                return world.Origin().ToOrigin(placement.At);
        }
    }

    // The inverse, for a document being saved: a position in the simulation's frame, as `at` in `frame`.
    public static Vector3 PlacementAt(this World world, Vector3 position, Vector3 origin, PlacementFrame frame)
    {
        var absolute = world.Origin().ToAbsolute(position);
        switch (frame)
        {
            case PlacementFrame.Origin:
                return absolute - origin;
            case PlacementFrame.Ground:
            {
                float ground = world.Resources.TryGet<Terrain>(out var terrain) && terrain != null
                    ? terrain.HeightAt(position.X, position.Z)
                    : origin.Y;
                return new Vector3(absolute.X - origin.X, position.Y - ground, absolute.Z - origin.Z);
            }
            default:
                return absolute;
        }
    }

    // Reads the world back into a document: the inverse, so that what an editor saves is what a game
    // will load. Positions go back into the document's own frame (`origin` and `relativeTo`, kept from
    // the record it was opened from), which is what the file holds; a placement that named a frame of
    // its own comes back in the document's.
    public static PlacementsRecord ReadPlacements(this World world, RecordId document)
    {
        var record = new PlacementsRecord();
        if (world.Engine?.Records.TryGet(document, out PlacementsRecord opened) == true)
        {
            record.Origin = opened.Origin;
            record.RelativeTo = opened.RelativeTo;
        }

        foreach (var entity in world.Query<Transform, FromPlacements>().Entities)
        {
            if (entity.GetComponent<FromPlacements>().Document != document) continue;

            var transform = entity.GetComponent<Transform>();
            record.Place.Add(new Placement
            {
                Prefab = entity.TryGetComponent<FromPrefab>(out var from) ? from.Prefab : default,
                At = world.PlacementAt(transform.LocalPosition, record.Origin, record.RelativeTo),
                // + 0: a yaw of -0 is 0, and a file should not say "-0".
                Yaw = SageMath.YawOf(transform.LocalRotation) * 180f / MathF.PI + 0f,
                Name = entity.Name ?? "",
                Outputs = PlacementWires.Read(entity),
            });
        }

        return record;
    }

    // Destroys what a document put in the world, so it can be opened again or closed.
    public static int ClearPlacements(this World world, RecordId document)
    {
        int removed = 0;
        foreach (var entity in world.Query<FromPlacements>().Entities.ToEntityList())
            if (entity.GetComponent<FromPlacements>().Document == document)
            {
                world.Destroy(entity);
                removed++;
            }
        return removed;
    }
}

// A placement's `outputs` (issue #80): the entity I/O wiring a `.map` writes as `On…` keys, written in a
// scene or a placements document, so a game with no maps — or no C# — can wire a trigger to a camera.
// Each placed entity gets its own copies (a record is shared; what a wire resolved and how often it fired
// are one entity's). Targets are found by name when the wire fires (EntityIO's late binding), so a wire
// may name anything placed before or after it, or spawned later; the content check below says at load
// when an input does not exist, as a map's does.
internal static class PlacementWires
{
    public static void Attach(World world, Entity entity, Placement placement)
    {
        var outputs = placement.Outputs;
        if (outputs == null || outputs.Count == 0) return;

        int usable = 0;
        foreach (var wire in outputs) if (Usable(wire)) usable++;
        if (usable == 0) return;

        var wires = new Connection[usable];
        int n = 0;
        foreach (var wire in outputs) if (Usable(wire)) wires[n++] = wire.Copy();
        world.Add(entity, new IOConnections { Wires = wires });
    }

    // The editor's way back: an entity's wires as a placement writes them.
    public static List<Connection> Read(Entity entity)
    {
        var list = new List<Connection>();
        if (!entity.TryGetComponent<IOConnections>(out var io) || io.Wires == null) return list;
        foreach (var wire in io.Wires) list.Add(wire.Copy());
        return list;
    }

    // What the load check already reported is skipped at spawn rather than said again.
    private static bool Usable(Connection? wire) =>
        wire != null && wire.Output.Length > 0 && wire.Target.Length > 0 && wire.Input.Length > 0;

    // Content load: every wire names an output, a target and an input that exists (errors), and an output
    // some plugin fires (a warning: a game's own C# may fire outputs it never declared).
    public static void Check(Engine engine, Placement placement, string path, RecordCheck check)
    {
        var outputs = placement.Outputs;
        if (outputs == null) return;
        for (int i = 0; i < outputs.Count; i++)
        {
            var wire = outputs[i];
            string at = $"{path}.Outputs[{i}]";
            if (wire == null) { check.Error(at, "an empty wire"); continue; }
            if (wire.Output.Length == 0) check.Error(at, "a wire needs an \"output\" (the one of this entity's that sends it)");
            if (wire.Target.Length == 0) check.Error(at, "a wire needs a \"target\" (an entity's name, or !self / !activator / !caller)");
            if (wire.Input.Length == 0) check.Error(at, "a wire needs an \"input\"");
            else if (!engine.Inputs.Has(wire.Input))
                check.Error(at, $"'{wire.Input}' is not an input (see io_list; is the plugin that registers it on?)");
            if (wire.Output.Length > 0 && !engine.Outputs.Has(wire.Output))
                check.Warn(at, $"no plugin declares the output '{wire.Output}' (see io_list), so nothing may ever fire it");
        }
    }
}
