#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Simulation;

// What stands where (docs/design/15 §3, TODO F28/F31).
//
// **The thing an editor edits.** Levels are brushes and TrenchBroom owns those (15 §10a); prefabs say
// what a thing *is*; this says where the things are. Until now every game invented its own answer — the
// Sandbox has a `scene` record of its own — which is fine until something other than the game wants to
// read or write it, and an editor is exactly that something.
//
// It is deliberately four fields. A placement is not a prefab with extras: per-entity overrides are
// F31's remaining work and want the serializer's field metadata (09 §3.2), so this is the part that can
// be written honestly today.
public sealed class Placement
{
    public RecordRef<PrefabRecord> Prefab;
    public Vector3 At;                 // absolute metres, like a scene's (R6: converted on spawn)
    public float Yaw;                  // degrees about +Y, 0 facing -Z
    public string Name = "";           // optional, so an outliner and a wire can name it
}

[Record("placements", Plugin = RegistrationOwners.Core)]
public sealed class PlacementsRecord
{
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
            var at = world.Origin().ToOrigin(placement.At);
            var entity = world.Spawn(placement.Prefab, at, placement.Yaw);
            if (entity.IsNull) continue;

            if (!string.IsNullOrEmpty(placement.Name)) entity.Name = new EntityName(placement.Name);
            world.Add(entity, new FromPlacements { Document = document });
            spawned++;
        }

        Log.Info(LogCat.Editor, $"Placements '{document}': {spawned} of {record.Place.Count} placed in '{world.Name}'");
        return spawned;
    }

    // Reads the world back into a document: the inverse, so that what an editor saves is what a game
    // will load. Positions go back to absolute metres, which is what the file holds.
    public static PlacementsRecord ReadPlacements(this World world, RecordId document)
    {
        var record = new PlacementsRecord();

        foreach (var entity in world.Query<Transform, FromPlacements>().Entities)
        {
            if (entity.GetComponent<FromPlacements>().Document != document) continue;

            var transform = entity.GetComponent<Transform>();
            record.Place.Add(new Placement
            {
                Prefab = entity.TryGetComponent<FromPrefab>(out var from) ? from.Prefab : default,
                At = world.Origin().ToAbsolute(transform.LocalPosition),
                Yaw = SageMath.YawOf(transform.LocalRotation) * 180f / MathF.PI,
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
