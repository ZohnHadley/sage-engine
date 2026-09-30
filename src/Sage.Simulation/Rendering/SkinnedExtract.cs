#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// The palette half of extracting skinned meshes (issue #117, docs/design/06 §3.2): which joints each
// skinned draw is bent by, written once into the frame's pooled bone list and shared by every view that
// sees the renderer. Pure and allocation free, so it lives here where a headless test can reach it, the
// way RenderViewPlan does; the client's SkinnedMeshExtract supplies the views (frustum and hidden
// checks), the bone list (RenderSnapshot.Bones) and the RenderItems through ISkinnedDraws.

// What SkinnedExtract.Emit writes into. A struct, so the call is generic and nothing boxes.
internal interface ISkinnedDraws
{
    // How many views the frame has.
    int Views { get; }

    // Whether `view` draws this renderer (in its frustum, not the view's hidden entity).
    bool Sees(int view);

    // `count` new entries at the end of the frame's bone list, and where they start.
    Span<Matrix4x4> AddBones(int count, out int start);

    // One draw of this renderer in `view`, skinned by bones [start, start + count).
    void Draw(int view, int boneStart, int boneCount);
}

internal static class SkinnedExtract
{
    // One skinned renderer across the frame's views. The palette is in model space, so it does not
    // depend on the camera: it is written at most once, at the first view that sees the renderer, and
    // every view that sees it draws from that one range. A renderer no view sees writes nothing.
    // More joints than SkinMath.MaxBones are cut to the first MaxBones, with one warning per `name`.
    // Returns how many views drew it.
    public static int Emit<T>(ref T draws, ReadOnlySpan<Matrix4x4> modelJoints, ReadOnlySpan<Matrix4x4> inverseBind, string name)
        where T : struct, ISkinnedDraws
    {
        int count = Math.Min(modelJoints.Length, inverseBind.Length);
        if (count > SkinMath.MaxBones)
        {
            Log.Once(LogCat.Render, LogLevel.Warn, "skin-bones:" + name,
                $"Skinned mesh '{name}' has {count} joints; a draw takes {SkinMath.MaxBones} (vs_3_0), so the rest are ignored. Split the mesh by bones.");
            count = SkinMath.MaxBones;
        }

        int start = -1, drawn = 0;
        for (int v = 0; v < draws.Views; v++)
        {
            if (!draws.Sees(v)) continue;
            if (start < 0)
            {
                var palette = draws.AddBones(count, out start);
                SkinMath.Palette(modelJoints[..count], inverseBind[..count], palette);
            }
            draws.Draw(v, start, count);
            drawn++;
        }
        return drawn;
    }
}

// The seam between what poses a skeleton and what draws it (issue #117): a world resource mapping an
// entity to the `SkeletonPose` (#116) its skinned renderer is drawn in. Whatever animates the entity —
// #118's AnimatorSystem, in Phase.Animation, or `r_testskin` — samples the pose, calls
// `PoseSampler.ToModelSpace` and `Set`s it once; SkinnedMeshExtract reads `ModelSpace` every frame.
// An entity with no pose here draws its mesh's rest pose. The pose is borrowed, not owned: whoever
// sets it keeps it alive, disposes it, and `Remove`s it first.
internal sealed class SkinPoses
{
    private readonly Dictionary<int, SkeletonPose> _byEntity = new();

    public int Count => _byEntity.Count;

    public void Set(Entity entity, SkeletonPose pose) => _byEntity[entity.Id] = pose;

    public bool TryGet(int entityId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SkeletonPose? pose) =>
        _byEntity.TryGetValue(entityId, out pose);

    public void Remove(Entity entity) => _byEntity.Remove(entity.Id);

    // The pose's model-space matrices in the order of the file's skin — the order JOINTS_0 indexes and
    // the mesh's inverse bind matrices are in — from the skeleton's parent-first order:
    // skinJoints[s] = pose.ModelSpace[skeleton.JointOfSkinIndex[s]]. False, writing nothing, when the
    // skeleton does not have `skinJoints.Length` skin joints (a pose for another model).
    public static bool ToSkinOrder(SkeletonPose pose, Span<Matrix4x4> skinJoints)
    {
        var map = pose.Skeleton.JointOfSkinIndex;
        if (map.Length != skinJoints.Length) return false;
        var model = pose.ModelSpace;
        for (int s = 0; s < map.Length; s++) skinJoints[s] = model[map[s]];
        return true;
    }
}
