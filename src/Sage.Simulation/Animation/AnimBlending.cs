#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Sync markers (issue #358, docs/design/12): a looping blend whose `sync` names clip events lines its
// clips up at those events instead of at the same fraction of their length, so a walk↔run blend plants
// each foot when the other clip does even though the walk's left foot falls 10% in and the run's 60%.
//
// The state's phase (Animator layer Phase, 0..1) is read as a place among the markers: with n markers a
// clip's pass is n segments, marker k to marker k+1 (the last back round to the first), and phase φ is
// segment ⌊φ·n⌋, a fraction φ·n − ⌊φ·n⌋ of the way along it. Each clip turns that into its own time,
// counting its markers from its first event named `sync[0]` — so the clips need the same markers in
// the same order, and a clip without the first one plays by normalised time. How fast the phase moves
// is unchanged (the blend's weighted duration), so the clips' passes still take as long as before.
// Allocation-free: the markers are read from the clip's events each time.
internal static class SyncMarkers
{
    // Where `clip` is, as a fraction of its length, when the state's phase is `phase`: `phase` itself
    // for a state that does not sync or a clip with no markers.
    public static float ClipPhase(AnimGraphRecord.State state, AnimationClip clip, float phase)
    {
        if (state.Sync is not { } names || !(clip.Duration > 0f) || !float.IsFinite(phase)) return phase;
        if (!Count(clip, names, out int n, out int start)) return phase;
        float duration = clip.Duration;
        float wrapped = phase - MathF.Floor(phase);
        float m = wrapped * n;
        int k = Math.Min((int)m, n - 1);
        float u = m - k;
        float a = MarkerTime(clip, names, start + k, n, duration);
        float b = MarkerTime(clip, names, start + k + 1, n, duration);
        float t = a + u * (b - a);
        t %= duration;
        if (t < 0f) t += duration;
        return t / duration;
    }

    // How many sync markers `clip` has, and where the first named names[0] is among them. False when it
    // has none of the first name (the clip plays by normalised time).
    public static bool Count(AnimationClip clip, string[] names, out int count, out int start)
    {
        count = 0;
        start = -1;
        var events = clip.Events;
        for (int i = 0; i < events.Count; i++)
        {
            if (!IsMarker(events[i], names, clip.Duration)) continue;
            if (start < 0 && string.Equals(events[i].Name, names[0], StringComparison.Ordinal)) start = count;
            count++;
        }
        return start >= 0;
    }

    // The `ordinal`-th marker's time (ordinals past the count go round into later passes).
    private static float MarkerTime(AnimationClip clip, string[] names, int ordinal, int count, float duration)
    {
        int pass = ordinal / count;
        int want = ordinal - pass * count;
        var events = clip.Events;
        for (int i = 0, seen = 0; i < events.Count; i++)
        {
            if (!IsMarker(events[i], names, duration)) continue;
            if (seen++ == want) return events[i].Time + pass * duration;
        }
        return pass * duration;
    }

    private static bool IsMarker(in ClipEvent ev, string[] names, float duration)
    {
        if (!(ev.Time >= 0f) || ev.Time >= duration) return false;
        for (int i = 0; i < names.Length; i++)
            if (string.Equals(ev.Name, names[i], StringComparison.Ordinal)) return true;
        return false;
    }

    // What a synced blend's clips are missing, for the warning when an animator resolves its graph: null
    // when every clip has the markers alike.
    public static string? Problem(AnimGraphRecord.State state, AnimationClip?[] clips, string[] clipNames)
    {
        if (state.Sync is not { } names) return null;
        int expected = -1;
        for (int i = 0; i < state.PointClips.Length; i++)
        {
            int c = state.PointClips[i];
            if (c >= clips.Length || clips[c] is not { } clip) continue;
            if (!Count(clip, names, out int n, out _))
                return $"clip '{clipNames[c]}' has no '{names[0]}' event, so it plays by normalised time";
            if (expected >= 0 && n != expected)
                return $"clip '{clipNames[c]}' has {n} sync markers where the others have {expected}, so its feet will not line up";
            expected = n;
        }
        return null;
    }
}

// Additive layers (issue #358): a pose made into a difference from a reference pose (each clip's first
// frame), then that difference added over another pose at a weight. Pure, allocation-free.
internal static class AdditivePose
{
    // `pose` becomes its difference from `reference`, joint by joint: the travel, the rotation that takes
    // reference to pose (reference · delta = pose), and the scale ratio.
    public static void MakeDelta(Span<Pose> pose, ReadOnlySpan<Pose> reference)
    {
        int n = Math.Min(pose.Length, reference.Length);
        for (int j = 0; j < n; j++)
        {
            ref var p = ref pose[j];
            ref readonly var r = ref reference[j];
            p.Position -= r.Position;
            p.Rotation = Quaternion.Normalize(Quaternion.Conjugate(Quaternion.Normalize(r.Rotation)) * p.Rotation);
            p.Scale = new Vector3(Ratio(p.Scale.X, r.Scale.X), Ratio(p.Scale.Y, r.Scale.Y), Ratio(p.Scale.Z, r.Scale.Z));
        }
    }

    private static float Ratio(float value, float of) => of != 0f && float.IsFinite(of) ? value / of : 1f;

    // Adds `delta` (a MakeDelta pose) over `pose`, in place, at `weight` (times mask[j] when there is a
    // mask): 0 leaves pose, 1 adds all of it.
    public static void Add(SkeletonPose pose, SkeletonPose delta, float weight, JointMask? mask)
    {
        float w = float.IsNaN(weight) ? 0f : Math.Clamp(weight, 0f, 1f);
        var lp = pose.Local;
        var ld = delta.Local;
        int n = Math.Min(lp.Length, ld.Length);
        for (int j = 0; j < n; j++)
        {
            float wj = mask == null ? w : mask.Weights.Length > j ? w * mask.Weights[j] : 0f;
            if (wj <= 0f) continue;
            ref var p = ref lp[j];
            ref readonly var d = ref ld[j];
            var rotation = wj >= 1f ? d.Rotation : Quaternion.Slerp(Quaternion.Identity, d.Rotation, wj);
            p.Position += d.Position * wj;
            p.Rotation = Quaternion.Normalize(p.Rotation * rotation);
            p.Scale *= Vector3.Lerp(Vector3.One, d.Scale, wj);
        }
    }
}
