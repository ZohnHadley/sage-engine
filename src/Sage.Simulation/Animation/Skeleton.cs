#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// The experimental id and link every public type of skeletal animation carries (issue #116,
// docs/MAKING_A_GAME.md §10b): Phase 4d is still building on it (#117 skins on the GPU, #118 graphs and
// the Animator, #119 clip events, #120 sockets and IK).
internal static class AnimationApi
{
    internal const string Experimental = "SAGE0126";
    internal const string Url = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api";
}

// A skeleton (docs/design/12 §3, issue #116): the joints a skin binds to, in an order where every
// parent comes before its children, so one pass from the front turns local transforms into model-space
// ones (PoseSampler.ToModelSpace). Immutable once built; shared by every pose and clip made for it.
//
// Math is System.Numerics (ARCHITECTURE D2). A joint's local transform is a `Pose` (translation,
// rotation, scale: the same struct transforms use), and `InverseBind` is plain `Matrix4x4`s, row-vector
// convention like the rest of the engine: skinning matrix = InverseBind[j] * ModelSpace[j].
//
// A skeleton read from glTF keeps the order the file's skin gave its joints when that is already
// parents-first (exporters write it so), and otherwise reorders; `JointOfSkinIndex` maps the file's skin
// index (what a vertex's JOINTS_0 names) to the joint here, so a mesh can be remapped once, on load.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
public sealed class Skeleton
{
    private readonly string[] _names;
    private readonly int[] _parents;
    private readonly Pose[] _rest;
    private readonly Matrix4x4[] _inverseBind;
    private readonly int[] _jointOfSkinIndex;

    // `parents[i]` is -1 for a root, or a joint before i. Throws ArgumentException when the arrays
    // disagree in length or a parent does not come first: a skeleton that breaks the order would
    // silently read a parent's model-space matrix before it was written. The arrays are copied.
    public Skeleton(string[] names, int[] parents, Pose[] restPose, Matrix4x4[] inverseBind)
        : this(names, parents, restPose, inverseBind, null) { }

    internal Skeleton(string[] names, int[] parents, Pose[] restPose, Matrix4x4[] inverseBind, int[]? jointOfSkinIndex)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(restPose);
        ArgumentNullException.ThrowIfNull(inverseBind);
        int count = names.Length;
        if (parents.Length != count || restPose.Length != count || inverseBind.Length != count)
            throw new ArgumentException($"A skeleton's arrays must have one entry per joint: {count} names, {parents.Length} parents, " +
                                        $"{restPose.Length} rest transforms, {inverseBind.Length} inverse bind matrices");
        for (int i = 0; i < count; i++)
        {
            if (parents[i] < -1 || parents[i] >= i)
                throw new ArgumentException($"Joint {i} ('{names[i]}') has parent {parents[i]}: a parent must be -1 (a root) or come before its child");
        }
        _names = (string[])names.Clone();
        _parents = (int[])parents.Clone();
        _rest = (Pose[])restPose.Clone();
        _inverseBind = (Matrix4x4[])inverseBind.Clone();
        if (jointOfSkinIndex == null)
        {
            _jointOfSkinIndex = new int[count];
            for (int i = 0; i < count; i++) _jointOfSkinIndex[i] = i;
        }
        else
        {
            if (jointOfSkinIndex.Length != count) throw new ArgumentException("jointOfSkinIndex must have one entry per joint");
            _jointOfSkinIndex = (int[])jointOfSkinIndex.Clone();
        }
    }

    public int JointCount => _names.Length;

    // -1 for a root; otherwise always less than the joint's own index.
    public ReadOnlySpan<int> Parents => _parents;

    // Each joint's local transform when nothing animates it (glTF: the joint node's own TRS).
    public ReadOnlySpan<Pose> RestPose => _rest;

    // Model space to each joint's bind space; InverseBind[j] * ModelSpace[j] is joint j's skinning matrix.
    public ReadOnlySpan<Matrix4x4> InverseBind => _inverseBind;

    // For skin index s (the index a vertex's JOINTS_0 holds), the joint here. The identity unless the
    // file's skin listed a child before its parent.
    public ReadOnlySpan<int> JointOfSkinIndex => _jointOfSkinIndex;

    public string NameOf(int joint) => _names[joint];

    // -1 when there is no joint of that name. Ordinal, as the file wrote it.
    public int IndexOf(string name)
    {
        for (int i = 0; i < _names.Length; i++)
            if (string.Equals(_names[i], name, StringComparison.Ordinal)) return i;
        return -1;
    }

    // True when `joint` is `ancestor` or below it.
    public bool IsInBranch(int joint, int ancestor)
    {
        for (int j = joint; j >= 0; j = _parents[j])
            if (j == ancestor) return true;
        return false;
    }
}
