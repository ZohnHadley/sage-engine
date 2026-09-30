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

// Model-space joint matrices by entity, for skinned renderers (issue #117). A world resource. What
// poses a skeleton writes here every frame before extract; a renderer with no entry draws its mesh's
// rest pose. Today that is only `r_testskin`; #118's AnimatorSystem is meant to be the writer (or to
// replace this with its own pose component), after which this stays the extract's one question:
// "where are this entity's joints?".
internal sealed class SkinPoses
{
    private readonly Dictionary<int, Matrix4x4[]> _byEntity = new();

    public int Count => _byEntity.Count;

    // The entity's joints, `joints` long, to write in place. Allocates only the first time, or when
    // the skeleton grows.
    public Span<Matrix4x4> Write(Entity entity, int joints)
    {
        if (!_byEntity.TryGetValue(entity.Id, out var pose) || pose.Length < joints)
            _byEntity[entity.Id] = pose = new Matrix4x4[joints];
        return pose.AsSpan(0, joints);
    }

    public bool TryGet(int entityId, out ReadOnlySpan<Matrix4x4> joints)
    {
        if (_byEntity.TryGetValue(entityId, out var pose)) { joints = pose; return true; }
        joints = default;
        return false;
    }

    public void Remove(Entity entity) => _byEntity.Remove(entity.Id);
}
