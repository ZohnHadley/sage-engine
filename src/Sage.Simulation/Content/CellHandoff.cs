#nullable enable
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// Taking entities out of a cell as it goes dormant, and putting them back somewhere else (phase 4g issue
// 4g-6; the 4g plan's decision 3). The seam the off-screen simulation (Sage.Gameplay's Offscreen.cs) is
// built on, and generic: anything that keeps entities of its own while their cell sleeps uses it.
//
// - **A handoff** (`ICellHandoff`) is told, as a cell goes dormant and before its state is written, which
//   roots it is taking with it. One it wants it `Release`s: the entity leaves the world as save entries,
//   tombstoned in the content that placed it (so placing that again does not place it twice), and does not
//   sleep in the cell. The rest sleep there as they always did (Cells.cs).
// - **Back** (`Restore`) where it is now, in any cell that is live: spawned from its entries through the
//   load's own path, a runtime spawn of that cell (`sage:cell`), never a second time.
// - `IsLive` and `Generation` say, without allocating, whether a scene's sector is in the world and whether
//   any cell came back since a caller last looked.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public interface ICellHandoff
{
    // `cell` is going dormant with these roots (what it placed and its runtime spawns; never the player).
    // Release the ones to keep with CellContent.Release; the rest sleep in the cell. Called outside every
    // query loop, at a tick boundary or from a scene change.
    void Sleeping(World world, string cell, IReadOnlyList<Entity> roots);
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public static class CellContent
{
    // Told about every cell of this world that goes dormant, until removed.
    public static void AddHandoff(World world, ICellHandoff handoff) => ContentIds.Baseline(world).AddHandoff(handoff);

    public static void RemoveHandoff(World world, ICellHandoff handoff)
    {
        if (world.Resources.TryGet<ContentBaseline>(out var baseline) && baseline != null) baseline.RemoveHandoff(handoff);
    }

    // Takes a root out of the world: its save entries (the root's first, then the children its prefab
    // placed), with positions in the frame the world is in now. Content that placed it has it as a
    // tombstone from now on. It is given a persistent id if it had none.
    public static List<JsonObject> Release(World world, Entity root)
    {
        if (world.TryGet<Persistent>(root, out var persistent) && !persistent.Id.IsEmpty)
            ContentIds.Baseline(world).Release(root, persistent.Id);
        else
        {
            if (world.Has<Persistent>(root)) world.Remove<Persistent>(root);
            world.Add(root, new Persistent { Id = PersistentId.New() });
        }
        var saved = world.Engine is { } engine ? engine.Saves.Capture(world, new[] { root }) : new List<JsonObject>();
        SleepingHandles.Asleep(world, root);   // what refers to it finds it again once it is restored (4m-4)
        world.Destroy(root);
        return saved;
    }

    // Spawns released entries again at `position` (origin space), a runtime spawn of `cell`. Null when an
    // entity with the root's id is in the world already, or it could not be rebuilt.
    public static Entity Restore(World world, IReadOnlyList<JsonObject> saved, Vector3 position, string cell)
    {
        if (world.Engine is not { } engine) return default;
        var root = engine.Saves.Revive(world, saved, position, $"cell {cell}");
        if (root.IsNull) return root;
        // Content placed it again in between (a scene placed whole places everything, then removes its
        // dead): it is that content's no longer.
        if (world.TryGet<Persistent>(root, out var persistent)) ContentIds.Baseline(world).Release(root, persistent.Id);
        if (world.Has<InCell>(root)) world.Get<InCell>(root).Source = cell;
        else world.Add(root, new InCell { Source = cell });
        world.FlushCommands();
        return root;
    }

    // The scene the world is in; empty for none.
    public static RecordId SceneOf(World world) =>
        world.Resources.TryGet<ActiveScene>(out var state) && state != null ? state.Id : default;

    // Whether `scene`'s content at `sector` (absolute) is in the world: the scene is the world's and, when
    // it streams, that sector is placed. Allocates nothing.
    public static bool IsLive(World world, RecordId scene, SectorCoord sector)
    {
        if (scene.IsEmpty || !world.Resources.TryGet<ActiveScene>(out var state) || state == null || state.Id != scene) return false;
        return state.Streamed is not { } streamed || streamed.IsPlaced(sector);
    }

    // The cell `scene`'s content at `sector` belongs to: the sector's (`sector:<scene>:<x>,<z>`) when the scene
    // streams in this world, else the scene's (`scene:<id>`).
    public static string CellAt(World world, RecordId scene, SectorCoord sector) =>
        world.Records().TryGet(scene, out SceneRecord record) && record.Streamed && world.Resources.TryGet<SectorRing>(out _)
            ? ContentIds.SectorSource(scene, sector)
            : ContentIds.SceneSource(scene);

    // Goes up every time a cell is placed and its state laid back: compare it with the last value seen to
    // learn that one came back.
    public static int Generation(World world) =>
        world.Resources.TryGet<ContentBaseline>(out var baseline) && baseline != null ? baseline.Generation : 0;
}
