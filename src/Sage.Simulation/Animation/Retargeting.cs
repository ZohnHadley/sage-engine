#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sage.Simulation;

// Animation retargeting (docs/design/12, issue #360): one clip library plays on humanoids of different
// rigs, so a mod can add a model without authoring its clips again.
//
// **The record.** A `skeleton_map` says which joint of a model (the target) follows which joint of a
// clip library's model (the source):
//
//   { "type": "skeleton_map", "id": "knight", "source": "models/mannequin.glb", "target": "models/knight.glb",
//     "bones": { "pelvis": "Hips", "chest": "Spine", "hand_l": "LeftHand", ... } }
//
// An animator whose model is the target plays a clip its own model has not got from the source, made
// for the target's skeleton the first time it is resolved and kept (AnimatorStepper.ResolveClips). The
// target's own clips win; several maps for one target are tried in id order.
//
// **The correction.** The two rigs need not share names, joint counts, order, bone lengths or the way
// their joints' local axes point: each mapped joint takes the source joint's model-space rotation
// *relative to its rest* (the T-pose offset: Δ = G_anim · G_rest⁻¹) and applies it over its own rest
// (G'_anim = Δ · G'_rest), then turns that back into a local rotation under its animated parent. Unmapped
// target joints keep their rest local transform, so they follow their parent (an extra twist joint);
// unmapped source joints still count, since the deltas are taken in model space. What must match is the
// rest *pose* (both T-poses, or both A-poses) and the model's facing (glTF: +Y up).
//
// Translation is carried for the topmost mapped joints only (the hips): the source's displacement from
// its rest in model space, times `scale` (0: the ratio of the two joints' rest heights, so a taller rig
// takes longer strides). Every other joint keeps its own bone lengths.
//
// The new clip is baked at `sampleRate` keys a second (linear), plus its last frame; its events are the
// source clip's (anim_events on the source model), copied again each time an animator resolves it.

// Which joint of a target model follows which joint of a source model's clips (see above).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[Record("skeleton_map", Plugin = RegistrationOwners.Core)]
public sealed class SkeletonMapRecord
{
    [Property(Tooltip = "The skinned model (.glb) whose clips are played: the clip library's rig")]
    [AssetKind("mesh")] public AssetPath Source;
    [Property(Tooltip = "The skinned model (.glb) that plays them")]
    [AssetKind("mesh")] public AssetPath Target;
    [Property(Tooltip = "Target joint name -> the source joint it follows (as the models name them)")]
    public Dictionary<string, string> Bones = new(StringComparer.Ordinal);
    [Property(Min = 0, Tooltip = "Root translation scale; 0 = the ratio of the two rigs' hip heights at rest")]
    public float Scale;
    [Property(Min = 1, Max = 240, Unit = "Hz", Tooltip = "Keys a second the retargeted clip is baked at")]
    public float SampleRate = ClipRetargeter.DefaultSampleRate;
}

// Retargets a clip made for one skeleton onto another (see above). Content time: allocates the new clip.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public static class ClipRetargeter
{
    public const float DefaultSampleRate = 30f;

    // `bones` maps a target joint's name to the source joint it follows; names either skeleton has not
    // got are skipped. `scale` multiplies the root's displacement (0: the ratio of the topmost mapped
    // joints' rest heights, 1 when either is at the floor). The clip keeps `clip`'s name and duration.
    public static AnimationClip Retarget(AnimationClip clip, Skeleton source, Skeleton target, IReadOnlyDictionary<string, string> bones,
                                         float scale = 0f, float sampleRate = DefaultSampleRate)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(bones);
        if (!(sampleRate > 0) || !float.IsFinite(sampleRate)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (!(scale >= 0) || !float.IsFinite(scale)) throw new ArgumentOutOfRangeException(nameof(scale));

        int nt = target.JointCount;
        var sourceOf = new int[nt];
        for (int j = 0; j < nt; j++)
            sourceOf[j] = bones.TryGetValue(target.NameOf(j), out var name) && name != null ? source.IndexOf(name) : -1;

        // The topmost mapped joints carry translation.
        var root = new bool[nt];
        int firstRoot = -1;
        var parentsT = target.Parents;
        for (int j = 0; j < nt; j++)
        {
            if (sourceOf[j] < 0) continue;
            bool above = false;
            for (int p = parentsT[j]; p >= 0; p = parentsT[p])
                if (sourceOf[p] >= 0) { above = true; break; }
            if (above) continue;
            root[j] = true;
            if (firstRoot < 0) firstRoot = j;
        }

        using var sourcePose = new SkeletonPose(source);
        using var targetRest = new SkeletonPose(target);
        PoseSampler.ToModelSpace(target, targetRest);
        sourcePose.ResetToRest();
        PoseSampler.ToModelSpace(source, sourcePose);
        var sourceRestRot = GlobalRotations(source, sourcePose.Local);
        var sourceRestPos = Positions(sourcePose.ModelSpace);
        var targetRestRot = GlobalRotations(target, targetRest.Local);
        var targetRestPos = Positions(targetRest.ModelSpace);

        if (scale == 0f)
        {
            scale = 1f;
            if (firstRoot >= 0)
            {
                float s = sourceRestPos[sourceOf[firstRoot]].Y, t = targetRestPos[firstRoot].Y;
                if (MathF.Abs(s) > 1e-4f && MathF.Abs(t) > 1e-4f) scale = t / s;
            }
        }

        // Key times: every 1/sampleRate seconds, and the last frame.
        float duration = clip.Duration;
        int steps = Math.Max(1, (int)MathF.Ceiling(duration * sampleRate - 1e-4f));
        var times = new float[duration > 0 ? steps + 1 : 1];
        for (int k = 0; k < times.Length; k++) times[k] = k == times.Length - 1 && duration > 0 ? duration : k / sampleRate;

        var rotations = new Quaternion[nt][];
        var translations = new Vector3[nt][];
        for (int j = 0; j < nt; j++)
        {
            if (sourceOf[j] < 0) continue;
            rotations[j] = new Quaternion[times.Length];
            if (root[j]) translations[j] = new Vector3[times.Length];
        }

        var sourceRot = new Quaternion[source.JointCount];
        var targetRot = new Quaternion[nt];
        var restLocal = target.RestPose;
        var parentsS = source.Parents;
        for (int k = 0; k < times.Length; k++)
        {
            PoseSampler.Sample(clip, times[k], false, sourcePose);
            PoseSampler.ToModelSpace(source, sourcePose);
            var local = sourcePose.Local;
            for (int j = 0; j < sourceRot.Length; j++)
                sourceRot[j] = parentsS[j] < 0 ? local[j].Rotation : Quaternion.Normalize(sourceRot[parentsS[j]] * local[j].Rotation);

            for (int j = 0; j < nt; j++)
            {
                int p = parentsT[j];
                var parentRot = p < 0 ? Quaternion.Identity : targetRot[p];
                int s = sourceOf[j];
                if (s < 0)
                {
                    targetRot[j] = Quaternion.Normalize(parentRot * restLocal[j].Rotation);
                    continue;
                }
                var delta = sourceRot[s] * Quaternion.Inverse(sourceRestRot[s]);
                targetRot[j] = Quaternion.Normalize(delta * targetRestRot[j]);
                rotations[j][k] = Quaternion.Normalize(Quaternion.Inverse(parentRot) * targetRot[j]);
                if (!root[j]) continue;
                var m = sourcePose.ModelSpace[s];
                var moved = new Vector3(m.M41, m.M42, m.M43) - sourceRestPos[s];
                var at = targetRestPos[j] + moved * scale;
                // No mapped joint is above a root, so its parent stands at rest.
                if (p >= 0 && Matrix4x4.Invert(targetRest.ModelSpace[p], out var toParent)) at = Vector3.Transform(at, toParent);
                translations[j][k] = at;
            }
        }

        // Keep each rotation track on one hemisphere, so linear keys slerp the short way.
        foreach (var track in rotations)
        {
            if (track == null) continue;
            for (int k = 1; k < track.Length; k++)
                if (Quaternion.Dot(track[k - 1], track[k]) < 0) track[k] = -track[k];
        }

        var result = new AnimationClip(clip.Name, nt, duration);
        for (int j = 0; j < nt; j++)
        {
            if (rotations[j] is { } r) result.SetRotation(j, AnimationInterpolation.Linear, times, r);
            if (translations[j] is { } t) result.SetTranslation(j, AnimationInterpolation.Linear, times, t);
        }
        foreach (var e in clip.Events) result.AddEvent(e.Time, e.Name);
        return result;
    }

    private static Quaternion[] GlobalRotations(Skeleton skeleton, ReadOnlySpan<Pose> local)
    {
        var parents = skeleton.Parents;
        var result = new Quaternion[skeleton.JointCount];
        for (int j = 0; j < result.Length; j++)
            result[j] = parents[j] < 0 ? local[j].Rotation : Quaternion.Normalize(result[parents[j]] * local[j].Rotation);
        return result;
    }

    private static Vector3[] Positions(ReadOnlySpan<Matrix4x4> model)
    {
        var result = new Vector3[model.Length];
        for (int j = 0; j < result.Length; j++) result[j] = new Vector3(model[j].M41, model[j].M42, model[j].M43);
        return result;
    }
}

// The records' side: finding a clip for an animator's model through the skeleton_map records that
// target it, retargeting it once per (map, source clip, target skeleton), and checking the records.
internal static class SkeletonMaps
{
    // Per record (a reload makes new ones, so the old entries go with them): retargeted clips by source
    // clip and target skeleton.
    private static readonly ConditionalWeakTable<SkeletonMapRecord, Dictionary<(AnimationClip, Skeleton), AnimationClip>> Baked = new();

    // The clip `name` for `model` (whose own set, `set`, has not got it) from the first map that targets
    // the model and whose source has it; null when none does. Content time (an animator's clips being
    // resolved): it may read the source's file the first time.
    public static AnimationClip? Find(World world, AssetPath model, AnimationSet set, string name)
    {
        if (model.IsEmpty || !world.Resources.TryGet<RecordStore>(out var records) || records == null
            || records.TypeNameOf(typeof(SkeletonMapRecord)) == null || !world.Resources.TryGet<GltfAnimationReader>(out var reader) || reader == null)
            return null;
        foreach (var map in records.All<SkeletonMapRecord>())
        {
            if (map.Target != model || map.Source.IsEmpty || map.Bones == null) continue;
            if (reader.Load(map.Source) is not { } source || source.FindClip(name) is not { } clip) continue;
            var cache = Baked.GetValue(map, static _ => new Dictionary<(AnimationClip, Skeleton), AnimationClip>());
            AnimationClip? made;
            lock (cache)
            {
                if (!cache.TryGetValue((clip, set.Skeleton), out made))
                {
                    WarnMissing(map, source, set);
                    float rate = map.SampleRate > 0 && float.IsFinite(map.SampleRate) ? map.SampleRate : ClipRetargeter.DefaultSampleRate;
                    float scale = map.Scale >= 0 && float.IsFinite(map.Scale) ? map.Scale : 0f;
                    made = ClipRetargeter.Retarget(clip, source.Skeleton, set.Skeleton, map.Bones, scale, rate);
                    cache.Add((clip, set.Skeleton), made);
                }
            }
            // The source's events as they are now (anim_events may have been reloaded since).
            made.SetRecordEvents(new List<ClipEvent>(clip.Events));
            return made;
        }
        return null;
    }

    private static void WarnMissing(SkeletonMapRecord map, AnimationSet source, AnimationSet target)
    {
        foreach (var (to, from) in map.Bones)
        {
            if (target.Skeleton.IndexOf(to) < 0)
                Log.Once(LogCat.Animation, LogLevel.Warn, $"skeleton-map:{map.Target}:{to}",
                    $"skeleton_map {map.Source} -> {map.Target}: {target.Source} has no joint '{to}'; it is skipped");
            if (from == null || source.Skeleton.IndexOf(from) < 0)
                Log.Once(LogCat.Animation, LogLevel.Warn, $"skeleton-map:{map.Source}:{from}",
                    $"skeleton_map {map.Source} -> {map.Target}: {source.Source} has no joint '{from}'; '{to}' stays at rest");
        }
    }

    internal static void Check(SkeletonMapRecord record, RecordCheck check)
    {
        if (record.Source.IsEmpty) check.Error("source", "names no model: the clips played come from the source model");
        if (record.Target.IsEmpty) check.Error("target", "names no model: the target is the model that plays the source's clips");
        else if (record.Target == record.Source) check.Error("target", "is the source: a model plays its own clips without a map");
        if (!(record.Scale >= 0) || !float.IsFinite(record.Scale)) check.Error("scale", $"{record.Scale} is not a scale (0 for the rigs' height ratio)");
        if (!(record.SampleRate >= 1) || !float.IsFinite(record.SampleRate)) check.Error("sampleRate", $"{record.SampleRate} is not a rate in keys a second");
        if (record.Bones == null || record.Bones.Count == 0)
        {
            check.Error("bones", "maps no joints: name each target joint and the source joint it follows");
            return;
        }
        foreach (var (to, from) in record.Bones)
        {
            if (string.IsNullOrWhiteSpace(to)) check.Error("bones", "a target joint needs a name");
            else if (string.IsNullOrWhiteSpace(from)) check.Error($"bones.{to}", "names no source joint");
        }
    }
}
