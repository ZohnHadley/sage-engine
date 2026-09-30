#nullable enable
using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// A skeleton's pose (issue #116): each joint's local transform, and the model-space matrices
// PoseSampler.ToModelSpace makes from them. Scratch, never saved: a pose is recomputed from the clips
// and their times every tick, and those times are what a save would hold (#118's Animator).
//
// Named SkeletonPose because `Pose` is the engine's translation-rotation-scale struct (transforms), which
// is exactly what a joint's local transform is, so the joints use it.
//
// The arrays are rented from the shared ArrayPool when the pose is made and returned by Dispose, so a
// character that comes and goes does not churn the heap; make poses at spawn or content time, never per
// tick. After Dispose the spans are empty.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class SkeletonPose : IDisposable
{
    private Pose[]? _local;
    private Matrix4x4[]? _model;

    // Starts at the skeleton's rest pose, with identity model-space matrices until ToModelSpace runs.
    public SkeletonPose(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Skeleton = skeleton;
        JointCount = skeleton.JointCount;
        _local = ArrayPool<Pose>.Shared.Rent(Math.Max(JointCount, 1));
        _model = ArrayPool<Matrix4x4>.Shared.Rent(Math.Max(JointCount, 1));
        ResetToRest();
        ModelSpace.Fill(Matrix4x4.Identity);
    }

    public Skeleton Skeleton { get; }
    public int JointCount { get; }

    // Each joint's transform relative to its parent (a root's: relative to the model).
    public Span<Pose> Local => _local == null ? Span<Pose>.Empty : _local.AsSpan(0, JointCount);

    // Each joint's transform in model space, as of the last PoseSampler.ToModelSpace. A skinning palette
    // is Skeleton.InverseBind[j] * ModelSpace[j] (#117).
    public Span<Matrix4x4> ModelSpace => _model == null ? Span<Matrix4x4>.Empty : _model.AsSpan(0, JointCount);

    public void ResetToRest() => Skeleton.RestPose.CopyTo(Local);

    // Copies another pose of the same skeleton (local transforms and model-space matrices).
    public void CopyFrom(SkeletonPose other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.JointCount != JointCount) throw new ArgumentException($"A pose of {other.JointCount} joints cannot be copied into one of {JointCount}");
        other.Local.CopyTo(Local);
        other.ModelSpace.CopyTo(ModelSpace);
    }

    public void Dispose()
    {
        if (_local != null) ArrayPool<Pose>.Shared.Return(_local);
        if (_model != null) ArrayPool<Matrix4x4>.Shared.Return(_model);
        _local = null;
        _model = null;
    }
}

// A weight per joint, for blending part of a body (issue #116): an upper-body layer blends only the
// spine's branch. 0 keeps the first pose, 1 takes the second (times the blend's own weight).
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class JointMask
{
    private readonly float[] _weights;

    // Every joint at `weight` (0 by default: set the branches that should blend).
    public JointMask(Skeleton skeleton, float weight = 0f)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Skeleton = skeleton;
        _weights = new float[skeleton.JointCount];
        Array.Fill(_weights, Math.Clamp(weight, 0f, 1f));
    }

    public Skeleton Skeleton { get; }
    public ReadOnlySpan<float> Weights => _weights;

    // Clamped to [0, 1].
    public float this[int joint]
    {
        get => _weights[joint];
        set => _weights[joint] = Math.Clamp(value, 0f, 1f);
    }

    // `joint` and every joint below it. Returns false (and changes nothing) when there is no such joint.
    public bool SetBranch(string joint, float weight)
    {
        int index = Skeleton.IndexOf(joint);
        if (index < 0) return false;
        SetBranch(index, weight);
        return true;
    }

    public void SetBranch(int joint, float weight)
    {
        if ((uint)joint >= (uint)_weights.Length) throw new ArgumentOutOfRangeException(nameof(joint));
        float w = Math.Clamp(weight, 0f, 1f);
        // Children come after their parents, so the branch is joint and whatever follows that descends from it.
        for (int j = joint; j < _weights.Length; j++)
            if (Skeleton.IsInBranch(j, joint)) _weights[j] = w;
    }
}
