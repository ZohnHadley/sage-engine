#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// The experimental id and link of the animator's hand-over to a ragdoll and back (issue #244, phase 4k,
// docs/MAKING_A_GAME.md §10b): Animators.Suspend/Resume/IsSuspended, Animators.PlayFrom and the
// Animator's Suspended field. Phase 4k is still building on them (#246 goes ragdoll, #247 gets up).
internal static class AnimationRagdollApi
{
    internal const string Experimental = "SAGE0134";
    internal const string Url = AnimationApi.Url;
}

// Suspending an animator and fading out of a pose (issue #244, docs/design/12 "As built (suspend and fade
// from a snapshot)").
//
// **The contract for whoever suspends an animator** (a ragdoll, a cut-scene's own pose player): while it
// is suspended AnimatorSystem steps nothing (params, clocks, transitions, triggers, events, aim params)
// and samples nothing, and it never writes the pose; the pose stays registered in SkeletonPoses (so
// skinning, attachments and hitboxes read it). The suspender owns `Animators.TryGetPose(...)`'s pose
// meanwhile: it rewrites pose.Local **every tick**, by the end of Phase.Animation, and calls
// PoseSampler.ToModelSpace, as any pose source does (SkeletonPoses' contract), since IK in Phase.Late
// adjusts it in place and would otherwise compound. The one exception: a suspended animator that has no
// sample yet (a load, a model that has just been read) is sampled once where its state stands, so it is
// not drawn at rest before the suspender's first write.
public static partial class Animators
{
    // Freezes the animator (see above). Saved: a load keeps it suspended. False when there is no animator.
    [Experimental(AnimationRagdollApi.Experimental, UrlFormat = AnimationRagdollApi.Url)]
    public static bool Suspend(World world, Entity entity)
    {
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return false;
        world.Get<Animator>(entity).Suspended = true;
        return true;
    }

    // Steps on from where it was frozen: its states, times, fades and params are what they were when it
    // was suspended. The body's velocity (the Speed param of an entity with no character controller) is
    // measured afresh from the next tick, not across the suspension. False when there is no animator.
    [Experimental(AnimationRagdollApi.Experimental, UrlFormat = AnimationRagdollApi.Url)]
    public static bool Resume(World world, Entity entity)
    {
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return false;
        ref var a = ref world.Get<Animator>(entity);
        Unfreeze(world, entity, ref a);
        return true;
    }

    [Experimental(AnimationRagdollApi.Experimental, UrlFormat = AnimationRagdollApi.Url)]
    public static bool IsSuspended(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.TryGet<Animator>(entity, out var a) && a.Suspended;

    // Enters `state` on a layer (the base by default) cross-fading from `snapshot` — a pose of the
    // animator's skeleton, such as the one a ragdoll left — over `fade` seconds along the state's ease,
    // rather than from the state it was in. Resumes a suspended animator. The snapshot is copied at once
    // (into a pose the world's animators pool per skeleton, handed back when the fade ends), so the
    // caller may reuse or dispose its own.
    //
    // Saved as the state's name; the snapshot is not saved, so a load mid-fade shows the state directly.
    // A fade of 0, or an animator with no pose yet (before its first tick), enters the state at once.
    // A transition taken during the fade fades from the state entered here, dropping the snapshot.
    // False when there is no animator, no such layer or no such state; ArgumentException when the
    // snapshot is not a pose of the animator's skeleton (its joint count differs).
    [Experimental(AnimationRagdollApi.Experimental, UrlFormat = AnimationRagdollApi.Url)]
    public static bool PlayFrom(World world, Entity entity, SkeletonPose snapshot, string state, float fade, string? layer = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        if (entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return false;
        ref var a = ref world.Get<Animator>(entity);
        if (Compiled(world, a.Graph) is not { } g) return false;
        AnimatorStepper.Resolve(entity, ref a, g);
        int l = g.LayerIndex(layer);
        if (l < 0) return false;
        var graphLayer = g.Layers[l];
        int to = graphLayer.IndexOf(state);
        if (to < 0) return false;

        AnimatorPoses? poses = null;
        AnimatorPoses.Instance? instance = null;
        if (world.Resources.TryGet<AnimatorPoses>(out poses) && poses != null) instance = poses.Find(a.Slot, entity);
        var own = instance?.Pose;
        if (own != null && own.JointCount != snapshot.JointCount)
            throw new ArgumentException($"A pose of {snapshot.JointCount} joints is not one of {World.Describe(entity)}'s skeleton ({own.JointCount} joints)", nameof(snapshot));

        Unfreeze(world, entity, ref a);
        ref var s = ref a.Layers![l];
        AnimatorStepper.EndFade(ref s);
        s.State = graphLayer.Names[to];
        s.Index = to;
        s.Time = 0f;
        s.Phase = 0f;
        s.Entered = true;
        if (!(fade > 0f) || !float.IsFinite(fade) || own == null || instance == null || poses == null) return true;

        if (instance.Snapshots.Length < g.Layers.Length) Array.Resize(ref instance.Snapshots, g.Layers.Length);   // once per animator
        var copy = instance.Snapshots[l] ??= poses.RentSnapshot(own.Skeleton);
        if (!ReferenceEquals(copy, snapshot)) copy.CopyFrom(snapshot);
        s.FromSnapshot = true;
        s.Fade = 0f;
        s.FadeDuration = fade;
        s.FadeEase = graphLayer.States[to].Ease;
        return true;
    }

    private static void Unfreeze(World world, Entity entity, ref Animator a)
    {
        if (!a.Suspended) return;
        a.Suspended = false;
        if (world.Resources.TryGet<AnimatorPoses>(out var poses) && poses?.Find(a.Slot, entity) is { } instance)
            instance.HasLastPosition = false;
    }
}
