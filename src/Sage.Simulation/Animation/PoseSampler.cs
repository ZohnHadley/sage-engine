#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Headless pose sampling (docs/design/12 §3, issue #116): a clip at a time into a pose, two poses
// blended, and a pose's local transforms into model space. Pure functions over caller-owned poses, so
// they run in a system, on a job or in a test alike, and allocate nothing (test: SamplingAllocatesNothing).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public static class PoseSampler
{
    // The time a clip is sampled at: wrapped into [0, Duration) when it loops, clamped to [0, Duration]
    // when it does not (a one-shot holds its last frame). NaN reads as 0.
    public static float ClipTime(AnimationClip clip, float time, bool loop)
    {
        ArgumentNullException.ThrowIfNull(clip);
        float duration = clip.Duration;
        if (float.IsNaN(time) || duration <= 0) return 0;
        if (!loop) return Math.Clamp(time, 0, duration);
        if (float.IsInfinity(time)) return 0;
        float t = time % duration;
        return t < 0 ? t + duration : t;
    }

    // Writes `clip` at `time` into `pose`: every joint starts from the skeleton's rest transform and takes
    // whichever channels the clip has for it. Joints past the clip's own count keep their rest transform.
    public static void Sample(AnimationClip clip, float time, bool loop, SkeletonPose pose)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(pose);
        float t = ClipTime(clip, time, loop);
        var local = pose.Local;
        var rest = pose.Skeleton.RestPose;
        rest.CopyTo(local);
        int joints = Math.Min(clip.JointCount, local.Length);
        for (int j = 0; j < joints; j++)
            clip.SampleJoint(j, t, ref local[j]);
    }

    // Blends `b` into `a`, in place: each joint moves `weight` of the way from a to b (times mask[j]
    // when there is a mask; null blends every joint). weight 0 leaves a, 1 copies b.
    public static void Blend(SkeletonPose a, SkeletonPose b, float weight, JointMask? mask) => Blend(a, b, weight, mask, a);

    // The same, into `result` (which may be a or b).
    public static void Blend(SkeletonPose a, SkeletonPose b, float weight, JointMask? mask, SkeletonPose result)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(result);
        if (a.JointCount != b.JointCount || a.JointCount != result.JointCount)
            throw new ArgumentException($"Poses of {a.JointCount}, {b.JointCount} and {result.JointCount} joints cannot be blended");
        if (mask != null && mask.Weights.Length != a.JointCount)
            throw new ArgumentException($"A mask of {mask.Weights.Length} joints cannot blend poses of {a.JointCount}");
        float w = float.IsNaN(weight) ? 0 : Math.Clamp(weight, 0f, 1f);
        var la = a.Local;
        var lb = b.Local;
        var lr = result.Local;
        for (int j = 0; j < lr.Length; j++)
        {
            float wj = mask == null ? w : w * mask.Weights[j];
            if (wj <= 0) { lr[j] = la[j]; continue; }
            if (wj >= 1) { lr[j] = lb[j]; continue; }
            ref readonly var pa = ref la[j];
            ref readonly var pb = ref lb[j];
            lr[j] = new Pose
            {
                Position = Vector3.Lerp(pa.Position, pb.Position, wj),
                Rotation = Quaternion.Normalize(Quaternion.Slerp(pa.Rotation, pb.Rotation, wj)),
                Scale = Vector3.Lerp(pa.Scale, pb.Scale, wj),
            };
        }
    }

    // Fills pose.ModelSpace from pose.Local: a root's matrix is its own, a child's is its local matrix
    // times its parent's (row vectors: child first). One pass, because parents come first.
    public static void ToModelSpace(Skeleton skeleton, SkeletonPose pose)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        if (pose.JointCount != skeleton.JointCount)
            throw new ArgumentException($"A pose of {pose.JointCount} joints is not one of this skeleton's {skeleton.JointCount}");
        var parents = skeleton.Parents;
        var local = pose.Local;
        var model = pose.ModelSpace;
        for (int j = 0; j < model.Length; j++)
        {
            var m = Compose(in local[j]);
            int parent = parents[j];
            model[j] = parent < 0 ? m : m * model[parent];
        }
    }

    // The same for `firstJoint` and every joint after it only: what IK calls after moving a joint, since
    // parents come first, a joint before `firstJoint` is untouched and a later one finds its parent's
    // matrix already current (issue #120). ModelSpace must already be current before `firstJoint`.
    public static void ToModelSpace(Skeleton skeleton, SkeletonPose pose, int firstJoint)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        if (pose.JointCount != skeleton.JointCount)
            throw new ArgumentException($"A pose of {pose.JointCount} joints is not one of this skeleton's {skeleton.JointCount}");
        var parents = skeleton.Parents;
        var local = pose.Local;
        var model = pose.ModelSpace;
        for (int j = Math.Max(firstJoint, 0); j < model.Length; j++)
        {
            var m = Compose(in local[j]);
            int parent = parents[j];
            model[j] = parent < 0 ? m : m * model[parent];
        }
    }

    // Scale, then rotate, then translate, written out: the three-product form costs two full matrix
    // multiplies per joint.
    internal static Matrix4x4 Compose(in Pose p)
    {
        var m = Matrix4x4.CreateFromQuaternion(p.Rotation);
        var s = p.Scale;
        m.M11 *= s.X; m.M12 *= s.X; m.M13 *= s.X;
        m.M21 *= s.Y; m.M22 *= s.Y; m.M23 *= s.Y;
        m.M31 *= s.Z; m.M32 *= s.Z; m.M33 *= s.Z;
        m.M41 = p.Position.X; m.M42 = p.Position.Y; m.M43 = p.Position.Z;
        return m;
    }
}
