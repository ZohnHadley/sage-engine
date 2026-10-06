#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// The CPU morph pass (issue #363, docs/design/12): a skinned part's vertices moved by a pose's morph
// weights, before the GPU skins them. Pure and allocation free, so it lives here where a headless test can
// reach it; the client's SkinnedMeshExtract calls it into a per-renderer vertex array and uploads that
// to a dynamic vertex buffer, only when the weights changed.
//
// A part's vertices are baked at the model's rest weights (MeshGeometry), so a vertex here is
//   baked + Σ (weight − rest) · delta
// for the position, and the same for the normal, normalised again. A target at its rest weight costs nothing.
internal static class MeshMorphing
{
    // For each of the mesh's targets (MeshGeometry.MorphTargets), its index in the pose's skeleton (-1:
    // the skeleton has none of that name, so it stays at rest). An identity when both came from one file.
    public static void Map(string[] meshTargets, Skeleton skeleton, Span<int> map)
    {
        for (int i = 0; i < meshTargets.Length && i < map.Length; i++)
            map[i] = i < skeleton.MorphTargetCount && string.Equals(skeleton.MorphTargetName(i), meshTargets[i], StringComparison.Ordinal)
                ? i : skeleton.MorphIndexOf(meshTargets[i]);
    }

    // The mesh's weights from a pose's (through `map`, from Map): rest where the pose has no such target.
    public static void Weights(ReadOnlySpan<float> poseWeights, ReadOnlySpan<int> map, ReadOnlySpan<float> rest, Span<float> weights)
    {
        for (int i = 0; i < weights.Length; i++)
        {
            int p = i < map.Length ? map[i] : -1;
            weights[i] = (uint)p < (uint)poseWeights.Length ? poseWeights[p] : i < rest.Length ? rest[i] : 0f;
        }
    }

    // True when any weight is off its rest value: the part needs morphing (else its baked vertices are right).
    public static bool Moved(ReadOnlySpan<float> weights, ReadOnlySpan<float> rest)
    {
        for (int i = 0; i < weights.Length; i++)
            if (weights[i] != (i < rest.Length ? rest[i] : 0f)) return true;
        return false;
    }

    // `part`'s skinned vertices moved by `weights` (one per mesh target) into `output` (VertexCount long).
    public static void Apply(MeshGeometryPart part, ReadOnlySpan<float> weights, ReadOnlySpan<float> rest, Span<SkinnedMeshVertex> output)
    {
        var source = part.Skinned ?? throw new ArgumentException("Only a skinned part is morphed", nameof(part));
        if (output.Length < source.Length) throw new ArgumentException($"{output.Length} vertices for a part of {source.Length}", nameof(output));
        source.AsSpan().CopyTo(output);
        if (part.Morphs is not { } morphs) return;

        bool normalsMoved = false;
        foreach (var morph in morphs)
        {
            float d = Delta(morph.Target, weights, rest);
            if (d == 0f) continue;
            var dp = morph.Positions;
            for (int v = 0; v < source.Length; v++)
            {
                ref readonly var o = ref output[v];
                output[v] = o.WithShape(o.Position + d * dp[v], morph.Normals != null ? o.Normal + d * morph.Normals[v] : o.Normal);
            }
            normalsMoved |= morph.Normals != null;
        }
        if (!normalsMoved) return;
        for (int v = 0; v < source.Length; v++)
        {
            ref readonly var o = ref output[v];
            var n = o.Normal;
            output[v] = o.WithShape(o.Position, n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : source[v].Normal);
        }
    }

    private static float Delta(int target, ReadOnlySpan<float> weights, ReadOnlySpan<float> rest)
    {
        if ((uint)target >= (uint)weights.Length) return 0f;
        float w = weights[target];
        if (!float.IsFinite(w)) return 0f;
        return w - (target < rest.Length ? rest[target] : 0f);
    }
}
