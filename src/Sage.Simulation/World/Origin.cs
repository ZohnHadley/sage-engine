#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Large-world coordinates (docs/design/03 §3.6, 14 §3; TODO R6).
//
// **The problem.** A float has about seven digits. At 10 km from the origin the gap between
// representable positions is roughly a millimetre; at 100 km it is centimetres, and a character
// controller that moves in millimetres starts to jitter, a physics contact starts to buzz, and a
// rotation built from a far-away position loses its last digits. A Daggerfall-like is hundreds of
// kilometres across, so absolute float positions cannot be the thing the simulation runs on.
//
// **The answer, which is Daggerfall Unity's and UE5's:** keep the world in **sectors** of 1024 m, and
// run the simulation in the local frame of *one* of them — the **origin sector**. Everything in memory
// (a `Transform`, a `GlobalTransform`, a Bepu body, a camera) is relative to that origin, so the
// numbers stay small no matter how far the player has walked. When they walk far enough, the origin
// moves and **everything shifts at once**, between ticks, by exactly one sector multiple.
//
// **What this costs the rest of the engine: almost nothing.** Positions are still floats in the same
// components, and code that does not care about absolute position does not change — which is the
// point of rebasing rather than promoting everything to `double`. The two things that do care are
// asked to convert: terrain (which is keyed by absolute sector) and saves (which must survive a
// different origin next run).
public sealed class Origin
{
    // Where the simulation currently is. Everything in memory is relative to this sector's corner.
    public SectorCoord Sector { get; private set; }

    // How far the origin has moved in total, for anything that wants to know that it did.
    public int Rebases { get; private set; }

    public event Action<Vector3>? Rebased;   // the offset applied to everything, in metres

    // **Horizontal only.** A sector offset has no Y (`SectorCoord.Origin`), so heights never change
    // when the origin moves — which is what makes a cached ground height (the Sandbox's hoppers keep
    // one) safe across a rebase, and is worth stating because it is load-bearing.
    //
    // Absolute position of a point given in origin space.
    public Vector3 ToAbsolute(Vector3 originSpace) => originSpace + Sector.Origin(Terrain.SectorSize);

    // And back: where an absolute point sits in the frame the simulation is running in.
    public Vector3 ToOrigin(Vector3 absolute) => absolute - Sector.Origin(Terrain.SectorSize);

    // The sector an origin-space point is in, absolutely.
    public SectorCoord SectorOf(Vector3 originSpace)
    {
        var absolute = ToAbsolute(originSpace);
        return Terrain.SectorOf(absolute.X, absolute.Z);
    }

    // Moves the origin. Returns the offset that must be applied to everything; `World.Rebase` is what
    // actually applies it, because it is the thing that can reach every entity.
    internal Vector3 MoveTo(SectorCoord sector)
    {
        var offset = Sector.Origin(Terrain.SectorSize) - sector.Origin(Terrain.SectorSize);
        Sector = sector;
        Rebases++;
        return offset;
    }

    // Raised by `world.Rebase` **after** everything has moved, not by `MoveTo` while it is happening:
    // a subscriber that read the world from inside the shift would see an origin saying the new sector
    // and every position still meaning the old one.
    internal void Announce(Vector3 offset) => Rebased?.Invoke(offset);

    public override string ToString() => $"origin {Sector}";
}

public static class OriginExtensions
{
    public static Origin Origin(this World world) => world.Resources.Get<Origin>();

    // Moves the simulation's frame of reference to `sector` and shifts **everything** into it: every
    // root transform, both poses of every global transform, every physics body and static, and the
    // camera. One offset, applied once, between ticks.
    //
    // `Previous` matters as much as `Current`: rendering interpolates between them, so an origin move
    // that shifted only the current pose would draw every entity streaking a kilometre across the
    // screen for one frame (03 §3.6).
    public static Vector3 Rebase(this World world, SectorCoord sector)
    {
        var origin = world.Origin();
        if (origin.Sector == sector) return Vector3.Zero;

        var from = origin.Sector;
        var offset = origin.MoveTo(sector);

        int moved = 0;
        foreach (var entity in world.Query<Transform>().Entities)
        {
            // Children move with their parents, because a child's transform is relative to one.
            if (!entity.Parent.IsNull) continue;
            world.Get<Transform>(entity).LocalPosition += offset;
            moved++;
        }

        foreach (var entity in world.Query<GlobalTransform>().Entities)
        {
            ref var global = ref world.Get<GlobalTransform>(entity);
            global.Current.Position += offset;
            global.Previous.Position += offset;
        }

        if (world.Resources.TryGet<IPhysicsWorld>(out var space)) space!.Rebase(offset);

        // Debug shapes drawn with a duration (a cast's arc, a swing's sweep) outlive the tick that
        // queued them, and they hold positions in the old frame. Dropping them costs a second of
        // visualisation once per kilometre travelled; keeping them draws a sector-wide lie.
        if (world.Resources.TryGet<DebugDraw>(out var debug)) debug!.Clear();
        // The camera too: a rig rewrites it from the pawn next frame, but the *editor* camera holds its
        // own position and would otherwise be left a sector behind whatever it was looking at.
        if (world.Resources.TryGet<ActiveCamera>(out var camera) && camera != null) camera.Position += offset;
        // A rig's pose is display-rate state it rewrites next frame, but until then it is a position in
        // the old frame, and so are the views the director resolved from it (issue #76).
        foreach (var (poses, _) in world.Query<CameraPose>().Chunks)
        {
            var span = poses.Span;
            for (int i = 0; i < span.Length; i++) span[i].Position += offset;
        }
        if (world.Resources.TryGet<CameraViews>(out var views) && views != null) views.Rebase(offset);

        // Last, with the world consistent: anything holding a position of its own (the editor's free
        // camera, a game's cached waypoint) moves here.
        origin.Announce(offset);

        Log.Info(LogCat.Streaming, $"Rebased {from} -> {sector} by {offset.X:F0},{offset.Z:F0} m ({moved} roots)");
        return offset;
    }
}
