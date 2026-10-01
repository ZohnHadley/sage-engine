#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// Cells: content that goes dormant with its state (phase 4g issue 4g-1; the 4g plan's decision 2, design
// 14 §3 dormancy).
//
// **A cell** is a unit of content that is live or dormant: today a whole scene (`scene:<id>`), and with
// 4g-3 a streamed sector of an exterior scene (`sector:<scene>:<x>,<z>`). It is a *source* in 4i's sense
// (ContentBaseline): the ids it placed, its tombstones, and now its state.
//
// - **Going dormant** (`ContentBaseline.Forget`, from `Scenes.Clear`, a placements document closed, a
//   level unloaded): every live entity the source placed is written as a save entry, the same entry a save
//   writes, into the world's dormant store. So are the **runtime spawns that belong to it** — what was
//   dropped, summoned, fired or `ent_spawn`ed there (`InCell`) — and those are then destroyed with it,
//   rather than staying in the world and showing up in the next scene at the same coordinates.
// - **Waking** (`ContentBaseline.Finish`, when the source is placed again): the state is laid onto what
//   the source placed, by id, and the runtime spawns are spawned again — the same path a load's first pass
//   takes (`SaveSystem.Restore`).
// - **Absolute positions.** A dormant cell keeps the sector its positions are relative to (`Frame`). Waking
//   it, or saving it, converts them to the frame then in use, so a rebase in between moves nothing. The
//   state a load holds for a source not placed yet (a level waiting for its ground) is a dormant cell too,
//   which fixes 4i's latent "pending state is origin-relative" bug.
//
// Not here: what decides a runtime spawn's cell beyond the world's scene (4g-3 gives a streamed scene's
// spawns their sector, and moves an entity to the next one when it crosses an edge), and anything that
// moves between cells (doors, travel and off-screen agents: 4g-5 and 4g-6).

// The cell a runtime spawn belongs to (4g-1): it goes dormant with that cell and comes back with it. Given
// by `World.MakePersistent` (so by `world.Spawn` of a prefab, a dropped item and a projectile) when the
// world has a cell to give; saved, so a load keeps it. An entity without one belongs to no cell and stays
// in the world whatever is cleared — the player never has one put to sleep (Cells.Sleeps).
[Component("sage:cell")]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public struct InCell : IComponent
{
    [Property(Tooltip = "The source this runtime spawn belongs to: `scene:<id>`, or a streamed sector (4g-3)")]
    public string Source;
}

// One source's state while it is not in the world: save entries (as a save writes them) whose positions
// are relative to `Frame`, the sector that was the origin when they were taken.
internal sealed class DormantCell
{
    public DormantCell(SectorCoord frame) => Frame = frame;

    public SectorCoord Frame { get; private set; }
    public List<JsonObject> Entities { get; } = new();

    // An entry taken in another frame, brought into this one.
    public void Add(JsonObject saved, SectorCoord frame)
    {
        if (frame != Frame) Cells.Shift(saved, Cells.Offset(frame, Frame));
        Entities.Add(saved);
    }

    // Every entry into `frame`, which is then the cell's.
    public void MoveTo(SectorCoord frame)
    {
        if (frame == Frame) return;
        var offset = Cells.Offset(Frame, frame);
        foreach (var saved in Entities) Cells.Shift(saved, offset);
        Frame = frame;
    }
}

internal static class Cells
{
    // The cell a runtime spawn made now belongs to: the world's scene. (4g-3: in a streamed scene, the
    // sector the entity stands in.) Null when the world has no scene, and then it belongs to none.
    public static string? Current(World world) =>
        world.Resources.TryGet<ActiveScene>(out var scene) && scene is { Id.IsEmpty: false }
            ? ContentIds.SceneSource(scene.Id)
            : null;

    // A runtime spawn joins the world's current cell (World.MakePersistent).
    public static void Join(World world, Entity entity)
    {
        if (world.Has<InCell>(entity) || Current(world) is not { } cell) return;
        world.Add(entity, new InCell { Source = cell });
    }

    // Whether an entity of this cell goes to sleep with it. The player, and the camera that follows it,
    // are never a cell's to take: a game's rules may have spawned the player with `world.Spawn`, which
    // gave it the cell it was made in.
    public static bool Sleeps(World world, Entity entity) =>
        !entity.Tags.Has<PlayerControlled>() && !entity.Tags.Has<PlayerCamera>() && entity != Scenes.Player(world);

    // The runtime spawns of a cell that go to sleep with it: roots only (a prefab's children go with
    // their parent), disabled placeholders included.
    public static List<Entity> Members(World world, string source)
    {
        var members = new List<Entity>();
        foreach (var entity in world.PersistentIncludingDisabled())
            if (entity.Parent.IsNull && entity.TryGetComponent<InCell>(out var cell)
                && string.Equals(cell.Source, source, StringComparison.Ordinal) && Sleeps(world, entity))
                members.Add(entity);
        return members;
    }

    // What to add to a position in frame `from` to have it in frame `to` (horizontal only, as a rebase).
    public static Vector3 Offset(SectorCoord from, SectorCoord to) =>
        from.Origin(Terrain.SectorSize) - to.Origin(Terrain.SectorSize);

    // A saved entry's position moved by `offset`: only a root's, because a child's transform is relative
    // to its parent (the same rule World.Rebase follows). The transform is always written in full (4i-5),
    // its `LocalPosition` as `[x, y, z]`.
    public static void Shift(JsonObject saved, Vector3 offset)
    {
        if (offset == Vector3.Zero || !IsRoot(saved)) return;
        if (saved["components"] is not JsonObject components
            || components[SaveSerializer.TransformId] is not JsonObject entry
            || entry["data"] is not JsonObject data) return;
        foreach (var (name, value) in data)
        {
            if (!string.Equals(name, nameof(Transform.LocalPosition), StringComparison.OrdinalIgnoreCase)) continue;
            if (value is not JsonArray { Count: 3 } xyz) return;
            xyz[0] = Read(xyz[0]) + offset.X;
            xyz[2] = Read(xyz[2]) + offset.Z;
            return;
        }
    }

    private static float Read(JsonNode? node) => node is JsonValue v && v.TryGetValue(out float f) ? f : 0f;

    private static bool IsRoot(JsonObject saved)
    {
        if (saved.ContainsKey("parent")) return false;
        if (saved["tags"] is JsonArray tags)
            foreach (var tag in tags)
                if ((string?)tag == FromParentPrefabId) return false;
        return true;
    }

    internal const string FromParentPrefabId = "sage:from_parent_prefab";
    internal const string PlayerControlledId = "sage:player_controlled";
    internal const string PlayerCameraId = "sage:player_camera";
    internal const string CellId = "sage:cell";
}
