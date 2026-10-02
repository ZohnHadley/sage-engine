#nullable enable
using System.Numerics;

namespace Sage.Simulation;

// Water (issue #262, docs/design/10 §3.6): a box of fluid that things float and swim in. The volume is
// data only: the 3D plugin's buoyancy system lifts and drags the dynamic bodies inside it, and the
// character controller swims in it (CharacterController.Swimming). It has no collider, so it never
// blocks, never answers a ray and costs the physics space nothing.
//
// The box is axis-aligned and centred on the entity's origin; its top face is the surface. Rotation is
// ignored: a river is several boxes, each with its own current.
[Component("sage:water_volume")]
public struct WaterVolume : IComponent
{
    [Property(Min = 0, Unit = "m", Tooltip = "Full extents of the box, centred on the entity; its top face is the surface")]
    public Vector3 Size;
    [Property(Min = 0, Unit = "1/s", Tooltip = "How fast the water slows what moves through it, relative to its current")]
    public float Drag;
    [Property(Min = 0, Tooltip = "Lift on a fully submerged body as a multiple of its weight: above 1 floats, below 1 sinks")]
    public float Buoyancy;
    [Property(Unit = "m/s", Tooltip = "The water's own flow: what it carries a floating or swimming thing along with")]
    public Vector3 Current;

    // The height of the surface for a volume whose entity is at `origin`.
    public readonly float SurfaceAt(Vector3 origin) => origin.Y + Size.Y * 0.5f;

    // Whether `point` is inside the volume's footprint (X and Z only) for a volume at `origin`.
    public readonly bool Covers(Vector3 origin, Vector3 point) =>
        System.MathF.Abs(point.X - origin.X) <= Size.X * 0.5f && System.MathF.Abs(point.Z - origin.Z) <= Size.Z * 0.5f;
}

// "water": { "size": [20, 3, 20], "drag": 2, "buoyancy": 2, "current": [0.5, 0, 0] }
//
// A pool, a lake or a stretch of river placed by hand: the entity sits at the box's centre, so a lake
// whose surface is at y = 0 and 3 m deep is placed at y = -1.5.
[PrefabPart("water", Plugin = "sage.physics3d")]
public sealed class WaterPart : IPrefabPart
{
    [Property(Min = 0, Unit = "m", Tooltip = "Full extents of the box, centred on the entity; its top face is the surface")]
    public Vector3 Size;
    [Property(Min = 0, Unit = "1/s", Tooltip = "How fast the water slows what moves through it")]
    public float Drag = 2f;
    [Property(Min = 0, Tooltip = "Lift on a fully submerged body as a multiple of its weight: 2 floats a crate half under")]
    public float Buoyancy = 2f;
    [Property(Unit = "m/s", Tooltip = "The water's flow")]
    public Vector3 Current;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Size.X <= 0f || Size.Y <= 0f || Size.Z <= 0f) { ctx.Error("a water volume needs a \"size\" with three positive extents"); return; }
        ctx.World.Add(ctx.Entity, new WaterVolume { Size = Size, Drag = Drag, Buoyancy = Buoyancy, Current = Current });
    }
}
