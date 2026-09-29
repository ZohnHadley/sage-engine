#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

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
}

// What a placement's `at` is measured from (issue #29).
public enum PlacementFrame
{
    World,    // absolute metres
    Origin,   // metres from the document's or scene's `origin`
    Ground,   // from the `origin` across, and y is height above the terrain there (the origin's y without terrain)
}

[Record("placements", Plugin = RegistrationOwners.Core)]
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

            if (!string.IsNullOrEmpty(placement.Name)) entity.Name = new EntityName(placement.Name);
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
                Name = entity.TryGetComponent<EntityName>(out var name) ? name.value : "",
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
