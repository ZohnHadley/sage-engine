#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Inverse kinematics on a SkeletonPose (docs/design/12 "As built (attachments and IK)", issue #120):
// pure math over a caller-owned pose, headless, allocation-free (test: SolversAllocateNothing), applied
// after the animation graph's blend (in Phase.Late, by AimIkSystem and Gameplay's FootIkSystem).
//
// Every solver reads pose.ModelSpace, so it must be current on entry (PoseSampler.ToModelSpace), writes
// only joint *rotations* in pose.Local (bone lengths never change), and leaves ModelSpace current again
// for the joints it moved and everything below them. Targets are in model space, which is the entity's
// own space (SkeletonPoses).
//
// Rotations compose as the engine's Pose does: a joint's model rotation is parent * local.

// Two-bone IK: a root, a middle and a tip joint (thigh, knee, ankle; shoulder, elbow, wrist) placed so
// the tip reaches a target, bending in the plane that holds the pole.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public static class TwoBoneIk
{
    private const float Epsilon = 1e-6f;

    // Rotates `root` and `mid` so `tip` lands on `target`; the chain bends toward `pole` (a model-space
    // point, typically in front of the knee or behind the elbow). A target out of reach is clamped: the
    // chain points straight at it, fully stretched (or folded, for one too close). `weight` blends the
    // result with the pose as it was (0 changes nothing). Returns true when the target was within reach.
    //
    // `mid` must be below `root` and `tip` below `mid` (usually each the other's child); ArgumentException
    // otherwise. Joints between them are carried along unchanged.
    //
    // `keepTipRotation` keeps the tip's model-space rotation as it was (a foot stays flat, a hand keeps
    // its grip angle) instead of letting it turn with the bone above it.
    public static bool Solve(SkeletonPose pose, int root, int mid, int tip, Vector3 target, Vector3 pole, float weight = 1f,
                             bool keepTipRotation = false)
    {
        ArgumentNullException.ThrowIfNull(pose);
        var skeleton = pose.Skeleton;
        int count = pose.JointCount;
        if ((uint)root >= (uint)count || (uint)mid >= (uint)count || (uint)tip >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(root), "A two-bone chain's joints must be joints of the pose's skeleton");
        if (mid == root || tip == mid || !skeleton.IsInBranch(mid, root) || !skeleton.IsInBranch(tip, mid))
            throw new ArgumentException($"Two-bone IK needs root → mid → tip down one branch; got {skeleton.NameOf(root)}, {skeleton.NameOf(mid)}, {skeleton.NameOf(tip)}");

        float w = float.IsNaN(weight) ? 0f : Math.Clamp(weight, 0f, 1f);
        if (w <= 0f) return IsReachable(pose, root, mid, tip, target);

        var model = pose.ModelSpace;
        Vector3 a = model[root].Translation, b = model[mid].Translation, c = model[tip].Translation;
        float lab = Vector3.Distance(a, b), lbc = Vector3.Distance(b, c);
        if (lab < Epsilon || lbc < Epsilon) return false;

        Vector3 toTarget = target - a;
        float distance = toTarget.Length();
        Vector3 dir;
        if (distance > Epsilon) dir = toTarget / distance;
        else
        {
            // On top of the root: any direction is as good as another; keep the current one.
            var ac = c - a;
            dir = ac.LengthSquared() > Epsilon ? Vector3.Normalize(ac) : Vector3.UnitY;
        }

        float maxReach = lab + lbc, minReach = MathF.Abs(lab - lbc);
        bool reachable = distance <= maxReach * (1f + 1e-5f) && distance >= minReach * (1f - 1e-5f);
        float d = Math.Clamp(distance, MathF.Max(minReach, Epsilon), maxReach);

        // The middle joint's new place: on the pole's side of the root-target line, at the angle the
        // triangle (lab, lbc, d) has at the root.
        float cosA = Math.Clamp((lab * lab + d * d - lbc * lbc) / (2f * lab * d), -1f, 1f);
        float sinA = MathF.Sqrt(MathF.Max(0f, 1f - cosA * cosA));
        Vector3 side = PoseMath.Perpendicular(pole - a, dir);
        if (side.LengthSquared() < Epsilon) side = PoseMath.Perpendicular(b - a, dir);
        if (side.LengthSquared() < Epsilon) side = PoseMath.AnyPerpendicular(dir);
        side = Vector3.Normalize(side);
        Vector3 newB = a + lab * (cosA * dir + sinA * side);
        Vector3 newC = a + d * dir;

        var local = pose.Local;
        Quaternion oldRoot = local[root].Rotation, oldMid = local[mid].Rotation;
        var tipInModel = keepTipRotation ? PoseMath.ModelRotation(pose, tip) : Quaternion.Identity;

        // The root turns the middle joint onto its place; the middle then turns the tip onto the target.
        var turnRoot = PoseMath.FromTo(b - a, newB - a);
        PoseMath.RotateInModelSpace(pose, root, turnRoot);
        Vector3 tipAfterRoot = a + Vector3.Transform(c - a, turnRoot);
        var turnMid = PoseMath.FromTo(tipAfterRoot - newB, newC - newB);
        PoseMath.RotateInModelSpace(pose, mid, turnMid);

        if (w < 1f)
        {
            local[root].Rotation = Quaternion.Normalize(Quaternion.Slerp(oldRoot, local[root].Rotation, w));
            local[mid].Rotation = Quaternion.Normalize(Quaternion.Slerp(oldMid, local[mid].Rotation, w));
        }
        if (keepTipRotation)
        {
            int tipParent = skeleton.Parents[tip];
            var parentRotation = tipParent < 0 ? Quaternion.Identity : PoseMath.ModelRotation(pose, tipParent);
            local[tip].Rotation = Quaternion.Normalize(Quaternion.Conjugate(parentRotation) * tipInModel);
        }
        PoseMath.UpdateModelSpaceFrom(pose, root);
        return reachable;
    }

    // Keeps the chain bending the way it bends now (the pole is the middle joint's current place).
    public static bool Solve(SkeletonPose pose, int root, int mid, int tip, Vector3 target)
    {
        ArgumentNullException.ThrowIfNull(pose);
        if ((uint)mid >= (uint)pose.JointCount) throw new ArgumentOutOfRangeException(nameof(mid));
        return Solve(pose, root, mid, tip, target, pose.ModelSpace[mid].Translation);
    }

    private static bool IsReachable(SkeletonPose pose, int root, int mid, int tip, Vector3 target)
    {
        var model = pose.ModelSpace;
        Vector3 a = model[root].Translation, b = model[mid].Translation, c = model[tip].Translation;
        float lab = Vector3.Distance(a, b), lbc = Vector3.Distance(b, c), distance = Vector3.Distance(a, target);
        return distance <= (lab + lbc) * (1f + 1e-5f) && distance >= MathF.Abs(lab - lbc) * (1f - 1e-5f);
    }
}

// One joint of an aim chain: how much of the aim it takes and how far it may turn. Angles in radians.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public readonly struct AimJoint
{
    public AimJoint(int joint, float weight, float maxPitch, float maxYaw)
    {
        Joint = joint;
        Weight = weight;
        MaxPitch = maxPitch;
        MaxYaw = maxYaw;
    }

    public int Joint { get; }

    // Its share of the aim: 0.3 turns this joint 30% of the way (before its limits). A chain's weights
    // usually add up to 1, so the head ends up looking where it was told.
    public float Weight { get; }

    // How far this joint alone may pitch and yaw, either way (radians, >= 0).
    public float MaxPitch { get; }
    public float MaxYaw { get; }
}

// An aim chain (look-at, aiming a weapon): the spine, neck and head each take a share of a pitch and a
// yaw, within their own limits, so the body turns toward `aim_pitch`/`aim_yaw` rather than the head
// snapping round alone.
//
// Pitch is about the model's +X (right), positive looking up; yaw about its +Y (up), positive turning
// left (SageMath's yaw). Both are relative to the character's forward, -Z.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public static class AimChainIk
{
    // `chain` lists the joints from the hips up (each an ancestor of the next, or at least never below a
    // later one); `weight` blends the whole aim in (0 changes nothing). Each joint turns by its share,
    // clamped to its limits, and whatever sits above it turns with it, so the last joint ends facing the
    // sum of the shares: pitched about the model's right, then yawed about its up.
    public static void Solve(SkeletonPose pose, ReadOnlySpan<AimJoint> chain, float pitch, float yaw, float weight = 1f)
    {
        ArgumentNullException.ThrowIfNull(pose);
        float w = float.IsNaN(weight) ? 0f : Math.Clamp(weight, 0f, 1f);
        if (w <= 0f || chain.IsEmpty) return;
        if (!float.IsFinite(pitch)) pitch = 0f;
        if (!float.IsFinite(yaw)) yaw = 0f;

        int count = pose.JointCount;
        int first = int.MaxValue;
        float pitchSoFar = 0f, yawSoFar = 0f;
        var before = Quaternion.Identity;   // the aim the joints so far have turned everything above them by
        foreach (var link in chain)
        {
            if ((uint)link.Joint >= (uint)count) throw new ArgumentOutOfRangeException(nameof(chain), $"Aim joint {link.Joint} is not a joint of the pose's skeleton");
            float share = float.IsFinite(link.Weight) ? link.Weight * w : 0f;
            float maxPitch = MathF.Abs(link.MaxPitch), maxYaw = MathF.Abs(link.MaxYaw);
            pitchSoFar += Math.Clamp(pitch * share, -maxPitch, maxPitch);
            yawSoFar += Math.Clamp(yaw * share, -maxYaw, maxYaw);

            // The total aim up to this joint, and the part of it this joint adds to what its ancestors in
            // the chain already turned it by.
            var total = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawSoFar) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitchSoFar);
            var delta = Quaternion.Normalize(total * Quaternion.Conjugate(before));
            PoseMath.RotateInModelSpace(pose, link.Joint, delta);
            before = total;
            if (link.Joint < first) first = link.Joint;
        }
        PoseMath.UpdateModelSpaceFrom(pose, first);
    }
}

// The pieces the solvers share.
internal static class PoseMath
{
    // A joint's rotation in model space: its local rotation under every ancestor's.
    public static Quaternion ModelRotation(SkeletonPose pose, int joint)
    {
        var parents = pose.Skeleton.Parents;
        var local = pose.Local;
        var q = Quaternion.Identity;
        for (int j = joint; j >= 0; j = parents[j]) q = local[j].Rotation * q;
        return q;
    }

    // Turns `joint` (and so everything below it) by `rotation`, given in model space, about the joint.
    public static void RotateInModelSpace(SkeletonPose pose, int joint, Quaternion rotation)
    {
        int parent = pose.Skeleton.Parents[joint];
        var parentRotation = parent < 0 ? Quaternion.Identity : ModelRotation(pose, parent);
        ref var local = ref pose.Local[joint];
        local.Rotation = Quaternion.Normalize(Quaternion.Conjugate(parentRotation) * rotation * parentRotation * local.Rotation);
    }

    // ToModelSpace for `first` and every joint after it. Parents come first, so a joint before `first`
    // is untouched and a joint after it finds its parent's matrix already updated.
    public static void UpdateModelSpaceFrom(SkeletonPose pose, int first) => PoseSampler.ToModelSpace(pose.Skeleton, pose, first);

    // The shortest rotation taking direction `from` onto direction `to` (identity for a zero vector).
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float lf = from.Length(), lt = to.Length();
        if (lf < 1e-8f || lt < 1e-8f) return Quaternion.Identity;
        from /= lf;
        to /= lt;
        float dot = Vector3.Dot(from, to);
        if (dot >= 1f - 1e-7f) return Quaternion.Identity;
        if (dot <= -1f + 1e-7f) return Quaternion.CreateFromAxisAngle(Vector3.Normalize(AnyPerpendicular(from)), MathF.PI);
        var axis = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(axis, 1f + dot));
    }

    // `v` without its component along the unit vector `axis`.
    public static Vector3 Perpendicular(Vector3 v, Vector3 axis) => v - axis * Vector3.Dot(v, axis);

    public static Vector3 AnyPerpendicular(Vector3 v)
    {
        var other = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        return Vector3.Cross(v, other);
    }

    // A pose from a matrix (scale, rotation, translation); identity rotation when it has no clean one.
    public static Pose ToPose(in Matrix4x4 m)
    {
        if (Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation))
            return new Pose { Position = translation, Rotation = Quaternion.Normalize(rotation), Scale = scale };
        return new Pose { Position = m.Translation, Rotation = Quaternion.Identity, Scale = Vector3.One };
    }
}
