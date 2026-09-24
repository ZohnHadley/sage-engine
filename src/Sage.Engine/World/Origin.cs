#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

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
        Rebased?.Invoke(offset);
        return offset;
    }

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

        if (world.Resources.TryGet<PhysicsSpace>(out var space)) space!.Rebase(offset);
        // The camera too: a rig rewrites it from the pawn next frame, but the *editor* camera holds its
        // own position and would otherwise be left a sector behind whatever it was looking at.
        if (world.Resources.TryGet<ActiveCamera>(out var camera) && camera != null) camera.Position += offset;

        Log.Info(LogCat.Streaming, $"Rebased {from} -> {sector} by {offset.X:F0},{offset.Z:F0} m ({moved} roots)");
        return offset;
    }
}
