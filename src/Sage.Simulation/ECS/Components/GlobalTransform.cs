#nullable enable
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Simulation;

// Position + rotation + scale. Stored instead of a matrix because rendering interpolates poses
// (lerp position/scale, slerp rotation); interpolating matrices component-wise distorts rotations
// (docs/design/03 §3.6).
public struct Pose
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static Pose Identity => new() { Rotation = Quaternion.Identity, Scale = Vector3.One };

    public static Pose FromLocal(in Transform t) => new()
    {
        Position = t.LocalPosition,
        Rotation = t.LocalRotation,
        Scale = t.LocalScale,
    };

    // parent ∘ local. Non-uniform parent scale combined with rotation is approximated (no shear),
    // the usual game-engine trade-off.
    public static Pose Combine(in Pose parent, in Pose local) => new()
    {
        Position = parent.Position + Vector3.Transform(local.Position * parent.Scale, parent.Rotation),
        Rotation = Quaternion.Normalize(parent.Rotation * local.Rotation),
        Scale = parent.Scale * local.Scale,
    };

    public static Pose Lerp(in Pose from, in Pose to, float t) => new()
    {
        Position = Vector3.Lerp(from.Position, to.Position, t),
        Rotation = Quaternion.Slerp(from.Rotation, to.Rotation, t),
        Scale = Vector3.Lerp(from.Scale, to.Scale, t),
    };

    public readonly Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
}

// The entity's pose in world space, computed by transform propagation (03 §3.6): never write it
// from gameplay code; write Transform instead. Previous is the pose at the start of the current
// tick, so rendering can interpolate between the last two ticks with FrameTime.Alpha.
//
// Relative to the world origin for now; with large-world coordinates (TODO R6) it becomes relative
// to the world's origin sector.
// Never saved (issue #20): derived from Transform and the parent chain, and its Previous is a one-tick
// render snapshot — restoring it makes the first frame after a load lerp from a pose that never existed.
[Transient]
[Component("sage:global_transform")]
public struct GlobalTransform : IComponent
{
    public Pose Current;
    public Pose Previous;

    public static GlobalTransform At(in Pose pose) => new() { Current = pose, Previous = pose };

    public readonly Pose Interpolated(float alpha) => Pose.Lerp(Previous, Current, alpha);
}
