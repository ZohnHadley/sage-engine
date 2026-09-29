#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Angles and directions, in one place (docs/design/03 §3.2). Input, movement, AI, sprites and the
// editor camera each used to carry their own copy of these helpers, and the copies disagreed: the
// sprite system read an entity's front as +Z while everything else read -Z, bridged by a lone `+PI`
// in the character controller (review #43). One convention now, stated once:
//
//   * +Y is up; an entity's front is its local -Z (TransformMath.Forward), as in MonoGame.
//   * Yaw is the rotation about +Y in radians. 0 faces -Z, and positive yaw turns counter-clockwise
//     seen from above (the sense of Quaternion.CreateFromYawPitchRoll), so +90° faces -X.
//   * Pitch is positive looking up.
//
// Everything that turns something — PawnIntent.Yaw, PlayerCommand.ViewYaw, AI steering, sprite
// direction groups, Transform.LocalRotation — means the same thing by "yaw".
public static class SageMath
{
    // Wraps to (-PI, PI]: the form to use for differences between angles.
    public static float WrapPi(float radians)
    {
        float a = MathF.IEEERemainder(radians, MathF.Tau);
        return a <= -MathF.PI ? a + MathF.Tau : a;
    }

    // Wraps to [0, TAU): the form to use before quantizing an angle into buckets.
    public static float WrapTau(float radians)
    {
        float a = radians % MathF.Tau;
        return a < 0 ? a + MathF.Tau : a;
    }

    // The horizontal direction a yaw faces (unit length).
    public static Vector3 ForwardFromYaw(float yaw) => new(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));

    // The rotation for a yaw, with no pitch or roll: what goes in Transform.LocalRotation.
    public static Quaternion RotationFromYaw(float yaw) => Quaternion.CreateFromYawPitchRoll(yaw, 0, 0);

    // The yaw whose forward points along `direction`, ignoring its height. Zero for a direction with
    // no horizontal part (straight up or down) and for a zero vector.
    public static float YawOf(Vector3 direction) => MathF.Atan2(-direction.X, -direction.Z);

    // The yaw an entity with this rotation faces: its local forward, in world space.
    public static float YawOf(Quaternion rotation) => YawOf(Vector3.Transform(TransformMath.Forward, rotation));

    // The yaw that looks from `from` toward `to`, ignoring height.
    public static float YawTo(Vector3 from, Vector3 to) => YawOf(to - from);

    // The pitch whose forward points along `direction`: positive looks **up**, matching
    // `CreateFromYawPitchRoll(yaw, pitch, 0)` applied to the local forward (−Z).
    public static float PitchOf(Vector3 direction)
    {
        float horizontal = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        return horizontal < 1e-6f && MathF.Abs(direction.Y) < 1e-6f ? 0f : MathF.Atan2(direction.Y, horizontal);
    }

    // The pitch that looks from `from` toward `to` — what an AI needs to aim a projectile at a body
    // rather than over its head (16 §3.4).
    public static float PitchTo(Vector3 from, Vector3 to) => PitchOf(to - from);

    // Distance in the XZ plane: the "how far away on foot" that AI and interaction want.
    public static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // Turns `yaw` toward `target` by at most `maxStep`, the short way round.
    public static float TurnToward(float yaw, float target, float maxStep)
    {
        float delta = WrapPi(target - yaw);
        if (MathF.Abs(delta) <= maxStep) return WrapPi(target);
        return WrapPi(yaw + MathF.Sign(delta) * maxStep);
    }

    // Is `target` within `coneDegrees` of where a yaw is facing? The cone is the full angle, so 200°
    // means 100° to either side (AI sight, interaction prompts).
    public static bool InCone(float yaw, Vector3 from, Vector3 target, float coneDegrees)
    {
        if (coneDegrees >= 360f) return true;
        float half = coneDegrees * 0.5f * MathF.PI / 180f;
        return MathF.Abs(WrapPi(YawTo(from, target) - yaw)) <= half;
    }
}
