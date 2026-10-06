#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// What a state takes out of its clips' root joint and gives the body instead (issue #357): the feet stay
// where the clip put them because the body travels exactly as far as the clip's root did.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public enum RootMotionMode
{
    // The clip plays in place: whatever its root does stays in the pose.
    None,
    // The root's turn about up (its yaw) turns the body: a turn on the spot.
    Rotation,
    // The root's travel across the ground (and its height, with rootMotionY) moves the body: a walk, a lunge.
    Translation,
    // Both: the travel is in the frame the root faced, so a curved walk curves the body.
    Full,
}

// Root motion (issue #357, docs/design/12): each tick the animator works out how far the base layer's
// root joint travelled and turned in the clips it played — a blend's clips weighted as the blend weighs
// them, the state being left and the one entered weighted by the cross-fade — and takes that out of the
// pose (the root is held where each clip started) so the body can carry it instead:
//
//   - the turn goes into PawnIntent.Yaw (a character's controller faces it next tick), else straight
//     into the Transform's rotation;
//   - the travel goes to the character controller when the entity has one (taken with
//     Animators.TryTakeRootMotion in the next tick's PrePhysics, so it collides and steps like any move),
//     else straight onto the Transform (a kinematic mover).
//
// Extraction runs on the logic step (every tick, whatever the animation LOD) so the distance never
// depends on the camera; it allocates nothing.
internal static class RootMotion
{
    // How far `clip`'s root moved and turned from clip time `a` to `b` (both in [0, Duration]): the
    // travel in the parent's frame (Full: in the frame the root faced at `a`), the turn in radians about up.
    public static void Segment(AnimationClip clip, int joint, AnimGraphRecord.State state, float a, float b, out Vector3 travel, out float turn)
    {
        travel = default;
        turn = 0f;
        var mode = state.RootMotion;
        bool rotates = mode is RootMotionMode.Rotation or RootMotionMode.Full;
        bool moves = mode is RootMotionMode.Translation or RootMotionMode.Full;
        float yawA = 0f;
        if (rotates && clip.TrySampleRotation(joint, a, out var qa) && clip.TrySampleRotation(joint, b, out var qb))
        {
            yawA = YawOf(qa);
            turn = SageMath.WrapPi(YawOf(qb) - yawA);
        }
        if (!moves || !clip.TrySampleTranslation(joint, a, out var pa) || !clip.TrySampleTranslation(joint, b, out var pb)) return;
        var d = pb - pa;
        if (!state.RootMotionY) d.Y = 0f;
        // Full: the body has turned by as much as the root has since the clip's start (the pose holds the
        // root at its starting yaw), so the travel is put in the body's frame by taking that turn back off.
        float turned = mode == RootMotionMode.Full && yawA != 0f ? SageMath.WrapPi(yawA - StartYaw(clip, joint, state)) : 0f;
        travel = turned != 0f ? Vector3.Transform(d, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -turned)) : d;
    }

    // The root's yaw where the clip starts (its end, played backwards).
    private static float StartYaw(AnimationClip clip, int joint, AnimGraphRecord.State state) =>
        clip.TrySampleRotation(joint, state.Speed >= 0f ? 0f : clip.Duration, out var q0) ? YawOf(q0) : 0f;

    // A state's motion going from phase `before` to `after` (a loop that went round: before..end, then
    // start..after), weighted over a blend's clips. Adds weight × it to `travel` and `turn`.
    public static void State(AnimGraphRecord.State state, float before, float after, float weight, AnimatorParam[] values,
                             AnimatorPoses.Instance instance, ref Vector3 travel, ref float turn)
    {
        if (state.RootMotion == RootMotionMode.None || !(weight > 0f)) return;
        var clips = instance.Clips;
        int joint = instance.RootJoint;
        if (joint < 0) return;
        Span<float> weights = stackalloc float[Animators.MaxBlendPoints];
        int n = AnimatorStepper.Weights(state, values, weights);
        for (int i = 0; i < n; i++)
        {
            int c = state.Clip >= 0 ? state.Clip : state.PointClips[i];
            float w = weights[i] * weight;
            if (w <= 0f || c >= clips.Length || clips[c] is not { } clip || !(clip.Duration > 0f)) continue;
            float d = clip.Duration;
            // A synced blend (issue #358) plays each clip where its markers put it, so each wraps on its own.
            float b0 = SyncMarkers.ClipPhase(state, clip, before), a0 = SyncMarkers.ClipPhase(state, clip, after);
            bool wrapped = state.Loop && (state.Speed >= 0f ? a0 < b0 : a0 > b0);
            Vector3 t;
            float r;
            if (!wrapped) Segment(clip, joint, state, b0 * d, a0 * d, out t, out r);
            else
            {
                // The pass's end first, then the next pass's start, in the frame the first left it facing.
                float end = state.Speed >= 0f ? d : 0f, start = d - end;
                Segment(clip, joint, state, b0 * d, end, out var t1, out var r1);
                Segment(clip, joint, state, start, a0 * d, out var t2, out var r2);
                t = t1 + (r1 != 0f ? Vector3.Transform(t2, Quaternion.CreateFromAxisAngle(Vector3.UnitY, r1)) : t2);
                r = r1 + r2;
            }
            travel += t * w;
            turn += r * w;
        }
    }

    // Holds the root joint where `clip` started, on the parts the state takes as root motion, so the
    // pose plays in place while the body moves: its ground travel (and height, with rootMotionY) and its
    // turn about up.
    public static void Strip(AnimationClip clip, int joint, AnimGraphRecord.State state, float time, SkeletonPose pose)
    {
        var mode = state.RootMotion;
        if (mode == RootMotionMode.None || (uint)joint >= (uint)pose.JointCount) return;
        ref var local = ref pose.Local[joint];
        float start = state.Speed >= 0f ? 0f : clip.Duration;
        if (mode is RootMotionMode.Translation or RootMotionMode.Full && clip.TrySampleTranslation(joint, start, out var p0))
        {
            local.Position.X = p0.X;
            local.Position.Z = p0.Z;
            if (state.RootMotionY) local.Position.Y = p0.Y;
        }
        if (mode is RootMotionMode.Rotation or RootMotionMode.Full && clip.TrySampleRotation(joint, start, out var q0)
            && clip.TrySampleRotation(joint, time, out var q))
        {
            float yaw = YawOf(q0) - YawOf(q);
            local.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) * q);
        }
    }

    // The twist of `q` about up, in radians (swing-twist: the same for twist·swing and swing·twist).
    internal static float YawOf(Quaternion q)
    {
        if (q.W < 0f) q = -q;
        return 2f * MathF.Atan2(q.Y, q.W);
    }
}

public static partial class Animators
{
    // The travel the entity's animator took out of its clips last tick for a character controller to
    // make (issue #357): in the entity's parent space (its rotation and scale applied), metres; `vertical`
    // when the state's rootMotionY says the height is the clip's, not gravity's. Taken once: a second call
    // in the same tick, or a call after the animator stopped taking root motion, returns false. An entity
    // without a CharacterController is moved by the animator itself and has nothing to take.
    public static bool TryTakeRootMotion(World world, Entity entity, out Vector3 travel, out bool vertical)
    {
        travel = default;
        vertical = false;
        if (entity.IsNull || !world.IsAlive(entity) || !world.TryGet<Animator>(entity, out var a)) return false;
        if (!world.Resources.TryGet<AnimatorPoses>(out var poses) || poses == null) return false;
        if (poses.Find(a.Slot, entity) is not { RootPending: true } instance) return false;
        instance.RootPending = false;
        if (instance.RootTick < world.Tick - 1) return false;          // stale: the animator has not run since
        travel = instance.RootTravel;
        vertical = instance.RootVertical;
        return true;
    }
}
