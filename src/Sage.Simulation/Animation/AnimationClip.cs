#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// How a channel gets from one key to the next: glTF's three (STEP, LINEAR, CUBICSPLINE).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public enum AnimationInterpolation
{
    // Holds each key's value until the next key.
    Step,
    // Lerps translation and scale, slerps rotation.
    Linear,
    // Hermite, with an in and an out tangent per key (glTF's layout: in, value, out).
    CubicSpline,
}

// A named moment in a clip: "the blow lands here", "the magazine comes out here". Filled from
// `anim_events` records (issue #119) or by code (AddEvent); the animator raises one each time its time is
// crossed (docs/design/12 "As built (animation events)").
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public readonly struct ClipEvent
{
    public ClipEvent(float time, string name) { Time = time; Name = name; }

    public float Time { get; }
    public string Name { get; }
}

// A clip (docs/design/12 §3, issue #116): per-joint translation, rotation and scale channels, each with
// its own keys and interpolation, and a duration. Made for one skeleton (joint indices are that
// skeleton's), usually by GltfAnimationReader. A joint with no channel of a kind keeps its rest value
// when sampled (PoseSampler.Sample).
//
// Whether a clip loops is not the clip's: glTF has no such flag, and the same walk is looped by a
// locomotion state and played once by a cut-scene, so the sampler is told (`loop`).
//
// Sampling allocates nothing: keys are arrays, found by binary search.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class AnimationClip
{
    private readonly Track[] _translation;
    private readonly Track[] _rotation;
    private readonly Track[] _scale;
    private readonly List<ClipEvent> _events = new();
    private readonly List<bool> _fromRecords = new();       // parallel to _events: put there by anim_events

    public AnimationClip(string name, int jointCount, float duration)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (jointCount < 0) throw new ArgumentOutOfRangeException(nameof(jointCount));
        if (!(duration >= 0) || float.IsInfinity(duration)) throw new ArgumentOutOfRangeException(nameof(duration), "a clip's duration is finite and not negative");
        Name = name;
        JointCount = jointCount;
        Duration = duration;
        _translation = new Track[jointCount];
        _rotation = new Track[jointCount];
        _scale = new Track[jointCount];
    }

    public string Name { get; }
    public int JointCount { get; }

    // In seconds: the last key's time in a clip read from glTF.
    public float Duration { get; }

    // Sorted by time, in seconds from the clip's start. Two events may share a time.
    public IReadOnlyList<ClipEvent> Events => _events;

    // Keeps Events sorted (a later event at an equal time goes after the earlier ones).
    public void AddEvent(float time, string name) => Insert(time, name, fromRecords: false);

    // The events `anim_events` records give this clip, replacing the ones they gave it before (a
    // records reload); events added by code stay.
    internal void SetRecordEvents(List<ClipEvent> events)
    {
        for (int i = _events.Count - 1; i >= 0; i--)
        {
            if (!_fromRecords[i]) continue;
            _events.RemoveAt(i);
            _fromRecords.RemoveAt(i);
        }
        foreach (var e in events) Insert(e.Time, e.Name, fromRecords: true);
    }

    private void Insert(float time, string name, bool fromRecords)
    {
        ArgumentNullException.ThrowIfNull(name);
        int at = _events.Count;
        while (at > 0 && _events[at - 1].Time > time) at--;
        _events.Insert(at, new ClipEvent(time, name));
        _fromRecords.Insert(at, fromRecords);
    }

    // `times` ascending; `values` one per key, or three per key for CubicSpline (in tangent, value, out
    // tangent). The arrays are kept, not copied: don't change them afterwards.
    public void SetTranslation(int joint, AnimationInterpolation interpolation, float[] times, Vector3[] values) =>
        _translation[CheckJoint(joint)] = Track.Create(interpolation, times, values, null, "translation");

    public void SetRotation(int joint, AnimationInterpolation interpolation, float[] times, Quaternion[] values) =>
        _rotation[CheckJoint(joint)] = Track.Create(interpolation, times, null, values, "rotation");

    public void SetScale(int joint, AnimationInterpolation interpolation, float[] times, Vector3[] values) =>
        _scale[CheckJoint(joint)] = Track.Create(interpolation, times, values, null, "scale");

    // True when any channel moves the joint.
    public bool Animates(int joint) =>
        (uint)joint < (uint)JointCount && (_translation[joint].Keys > 0 || _rotation[joint].Keys > 0 || _scale[joint].Keys > 0);

    private int CheckJoint(int joint)
    {
        if ((uint)joint >= (uint)JointCount) throw new ArgumentOutOfRangeException(nameof(joint), $"clip '{Name}' has {JointCount} joints");
        return joint;
    }

    // Writes the channels the clip has for `joint` over `local`, at a time already wrapped or clamped.
    internal void SampleJoint(int joint, float time, ref Pose local)
    {
        ref readonly var t = ref _translation[joint];
        if (t.Keys > 0) local.Position = t.SampleVector(time);
        ref readonly var r = ref _rotation[joint];
        if (r.Keys > 0) local.Rotation = r.SampleRotation(time);
        ref readonly var s = ref _scale[joint];
        if (s.Keys > 0) local.Scale = s.SampleVector(time);
    }

    // One joint's translation or rotation channel alone, at a time already wrapped or clamped (root
    // motion, issue #357). False when the clip has no such channel for the joint.
    internal bool TrySampleTranslation(int joint, float time, out Vector3 value)
    {
        value = default;
        if ((uint)joint >= (uint)JointCount || _translation[joint].Keys == 0) return false;
        value = _translation[joint].SampleVector(time);
        return true;
    }

    internal bool TrySampleRotation(int joint, float time, out Quaternion value)
    {
        value = Quaternion.Identity;
        if ((uint)joint >= (uint)JointCount || _rotation[joint].Keys == 0) return false;
        value = _rotation[joint].SampleRotation(time);
        return true;
    }

    // One channel: key times, and either vectors (translation, scale) or rotations.
    private readonly struct Track
    {
        public readonly AnimationInterpolation Interpolation;
        public readonly float[] Times;
        public readonly Vector3[]? Vectors;
        public readonly Quaternion[]? Rotations;

        private Track(AnimationInterpolation interpolation, float[] times, Vector3[]? vectors, Quaternion[]? rotations)
        {
            Interpolation = interpolation;
            Times = times;
            Vectors = vectors;
            Rotations = rotations;
        }

        public int Keys => Times?.Length ?? 0;

        public static Track Create(AnimationInterpolation interpolation, float[] times, Vector3[]? vectors, Quaternion[]? rotations, string what)
        {
            ArgumentNullException.ThrowIfNull(times);
            int count = vectors?.Length ?? rotations?.Length ?? throw new ArgumentNullException(nameof(vectors));
            if (!Enum.IsDefined(interpolation)) throw new ArgumentOutOfRangeException(nameof(interpolation));
            int perKey = interpolation == AnimationInterpolation.CubicSpline ? 3 : 1;
            if (count != times.Length * perKey)
                throw new ArgumentException($"A {interpolation} {what} channel with {times.Length} keys needs {times.Length * perKey} values, not {count}");
            for (int i = 0; i < times.Length; i++)
                if (!float.IsFinite(times[i]) || (i > 0 && times[i] < times[i - 1]))
                    throw new ArgumentException($"A {what} channel's key times must be finite and ascend (key {i}: {times[i]})");
            return new Track(interpolation, times, vectors, rotations);
        }

        // The key k with Times[k] <= time < Times[k + 1], and how far between them (u in [0, 1), and the
        // span in seconds). Before the first key: k = 0, u = 0; at or after the last: k = last, u = 0.
        private int Find(float time, out float u, out float span)
        {
            var times = Times;
            int last = times.Length - 1;
            u = 0; span = 0;
            if (time <= times[0]) return 0;
            if (time >= times[last]) return last;
            int lo = 0, hi = last;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (times[mid] <= time) lo = mid; else hi = mid;
            }
            span = times[lo + 1] - times[lo];
            u = span > 0 ? (time - times[lo]) / span : 0;
            return lo;
        }

        public Vector3 SampleVector(float time)
        {
            var v = Vectors!;
            int k = Find(time, out float u, out float span);
            bool cubic = Interpolation == AnimationInterpolation.CubicSpline;
            if (u <= 0) return cubic ? v[k * 3 + 1] : v[k];
            switch (Interpolation)
            {
                case AnimationInterpolation.Step: return v[k];
                case AnimationInterpolation.Linear: return Vector3.Lerp(v[k], v[k + 1], u);
                default:
                    Hermite(u, out float h00, out float h10, out float h01, out float h11);
                    // glTF 2.0 Appendix C: v_k, b_k (out of k), v_k+1, a_k+1 (into k+1), tangents scaled by the span.
                    return h00 * v[k * 3 + 1] + h10 * span * v[k * 3 + 2] + h01 * v[k * 3 + 4] + h11 * span * v[k * 3 + 3];
            }
        }

        public Quaternion SampleRotation(float time)
        {
            var q = Rotations!;
            int k = Find(time, out float u, out float span);
            bool cubic = Interpolation == AnimationInterpolation.CubicSpline;
            if (u <= 0) return Quaternion.Normalize(cubic ? q[k * 3 + 1] : q[k]);
            switch (Interpolation)
            {
                case AnimationInterpolation.Step: return Quaternion.Normalize(q[k]);
                case AnimationInterpolation.Linear: return Quaternion.Normalize(Quaternion.Slerp(q[k], q[k + 1], u));
                default:
                    Hermite(u, out float h00, out float h10, out float h01, out float h11);
                    return Quaternion.Normalize(q[k * 3 + 1] * h00 + q[k * 3 + 2] * (h10 * span) + q[k * 3 + 4] * h01 + q[k * 3 + 3] * (h11 * span));
            }
        }

        private static void Hermite(float u, out float h00, out float h10, out float h01, out float h11)
        {
            float u2 = u * u, u3 = u2 * u;
            h00 = 2 * u3 - 3 * u2 + 1;
            h10 = u3 - 2 * u2 + u;
            h01 = -2 * u3 + 3 * u2;
            h11 = u3 - u2;
        }
    }
}
