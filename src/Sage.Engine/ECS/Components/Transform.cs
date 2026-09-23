#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Local position/rotation/scale (docs/design/03 §3.6). Relative to the parent, or — for root
// entities — to the entity's sector once SectorCoord/GlobalTransform arrive (migration step 4).
// Until then every entity is a root and Local* is the world pose.
//
// A struct's default is all zeros (zero rotation, zero scale — review item #3), so never create a
// Transform with `default` or `new Transform()`: use Transform.Identity / Transform.At(...).
// World.Create does this for you.
public struct Transform : IComponent
{
    public Vector3 LocalPosition;
    public Quaternion LocalRotation;
    public Vector3 LocalScale;

    public static Transform Identity => new() { LocalRotation = Quaternion.Identity, LocalScale = Vector3.One };

    public static Transform At(Vector3 position) => new()
    {
        LocalPosition = position,
        LocalRotation = Quaternion.Identity,
        LocalScale = Vector3.One,
    };

    [Transient]   // derived from the three above; sixteen floats of noise in a save (09 §3.3)
    public readonly Matrix4x4 LocalMatrix =>
        Matrix4x4.CreateScale(LocalScale) * Matrix4x4.CreateFromQuaternion(LocalRotation) * Matrix4x4.CreateTranslation(LocalPosition);
}

// Rotation helpers (components stay plain data; docs/design/03 §3.2). Local forward is -Z, up is +Y,
// the same right-handed convention as MonoGame.
public static class TransformMath
{
    public static readonly Vector3 Up = Vector3.UnitY;
    public static readonly Vector3 Forward = -Vector3.UnitZ;

    private const float MinDirectionLengthSquared = 1e-8f;

    // Rotates so the local Forward axis points at the target. Keeps the rotation unchanged if the
    // target is on top of the object (no defined direction).
    public static void LookAt(ref Transform t, Vector3 targetPosition)
    {
        Vector3 direction = targetPosition - t.LocalPosition;
        if (direction.LengthSquared() < MinDirectionLengthSquared)
            return;
        direction = Vector3.Normalize(direction);
        // CreateWorld (not CreateLookAt, which builds the inverse view matrix) maps Forward onto direction.
        Matrix4x4 world = Matrix4x4.CreateWorld(Vector3.Zero, direction, UpHintFor(direction));
        t.LocalRotation = Quaternion.CreateFromRotationMatrix(world);
    }

    // Spherical billboard: rotates so the local Forward axis faces the target (e.g. the camera).
    // Sprite billboarding moves to the renderer later (docs/design/06 §3.8); this stays for gameplay use.
    public static void Billboard(ref Transform t, Vector3 targetPosition)
    {
        Vector3 direction = targetPosition - t.LocalPosition;
        if (direction.LengthSquared() < MinDirectionLengthSquared)
            return;
        // CreateBillboard(objectPosition, cameraPosition, ...): object first, target second.
        Matrix4x4 billboard = Matrix4x4.CreateBillboard(t.LocalPosition, targetPosition, UpHintFor(direction), Forward);
        t.LocalRotation = Quaternion.CreateFromRotationMatrix(billboard);
    }

    // Up is parallel to a straight-up/down direction, which makes the cross products inside
    // CreateWorld/CreateBillboard collapse to zero (NaN rotation). Swap the hint there.
    private static Vector3 UpHintFor(Vector3 direction)
    {
        float alignment = Vector3.Dot(Vector3.Normalize(direction), Up);
        return MathF.Abs(alignment) > 0.999f ? Forward : Up;
    }
}
