#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// The deciding half of the viewmodel pass (issue #121, docs/design/06 "As built (the viewmodel pass)"):
// whether the screen draws first-person arms this frame, with which projection, and where each piece
// is. Pure and allocation free, so it lives here where a headless test can reach it, the way
// RenderViewPlan and SkinnedExtract do; the client's ViewmodelExtract adds the view and its items.

// The viewmodel the screen draws this frame (ViewmodelPass.TryGet).
internal struct ViewmodelView
{
    public Entity Camera;
    public Entity Arms;
    public Entity Weapon;           // null: bare hands
    public Quaternion Rotation;     // the view's: turns view space into camera-relative world
    public float FovY;              // radians
    public float Near, Far;
}

// What ViewmodelPass.Emit writes into. A struct, so the call is generic and nothing boxes.
internal interface IViewmodelDraws
{
    // A rigid mesh at `world`, a camera-relative world matrix.
    void Mesh(Entity entity, in MeshRenderer renderer, in Matrix4x4 world);

    // A skinned mesh at `world` (its pose, if any, is the entity's in SkinPoses).
    void Skinned(Entity entity, in SkinnedMeshRenderer renderer, in Matrix4x4 world);
}

internal static class ViewmodelPass
{
    private const float ToRadians = MathF.PI / 180f;

    // The screen's viewmodel this frame: the main view is a camera looking out of its first-person rig,
    // with an enabled Viewmodel whose arms are spawned. False in third person, from the editor's free
    // camera (a DebugCamera: no rig), from a scripted camera, and before the director has run.
    public static bool TryGet(World world, out ViewmodelView view)
    {
        view = default;
        if (!world.Resources.TryGet<CameraViews>(out var views) || views == null || !views.TryGetMain(out var main)) return false;
        var camera = main.Entity;
        if (camera.IsNull || main.Projection != CameraProjection.Perspective) return false;
        if (CameraRigs.RigOf(world, camera) != CameraRigKind.FirstPerson) return false;
        if (!world.TryGet<Viewmodel>(camera, out var viewmodel) || !viewmodel.Enabled || !world.IsAlive(viewmodel.Arms)) return false;

        view.Camera = camera;
        view.Arms = viewmodel.Arms;
        view.Weapon = world.IsAlive(viewmodel.Weapon) ? viewmodel.Weapon : default;
        view.Rotation = main.Rotation;
        view.FovY = viewmodel.FovY > 0f && viewmodel.FovY < 180f ? viewmodel.FovY * ToRadians : main.FovY;
        bool depth = viewmodel.Near > 0f && viewmodel.Far > viewmodel.Near;
        view.Near = depth ? viewmodel.Near : Viewmodel.DefaultNear;
        view.Far = depth ? viewmodel.Far : Viewmodel.DefaultFar;
        return true;
    }

    // A piece's pose in view space (its interpolated GlobalTransform) as a camera-relative world matrix:
    // the view's rotation applied, no translation, because the eye is the origin of both.
    public static Matrix4x4 ToCameraRelative(in Matrix4x4 viewSpace, Quaternion rotation) =>
        viewSpace * Matrix4x4.CreateFromQuaternion(rotation);

    // The arms and the weapon, each as a mesh or a skinned mesh, camera-relative. Returns how many pieces
    // it wrote. Allocates nothing.
    public static int Emit<T>(World world, in ViewmodelView view, float alpha, ref T draws) where T : struct, IViewmodelDraws
    {
        int drawn = Piece(world, view.Arms, view.Rotation, alpha, ref draws);
        if (!view.Weapon.IsNull) drawn += Piece(world, view.Weapon, view.Rotation, alpha, ref draws);
        return drawn;
    }

    private static int Piece<T>(World world, Entity entity, Quaternion rotation, float alpha, ref T draws) where T : struct, IViewmodelDraws
    {
        if (!world.TryGet<GlobalTransform>(entity, out var global)) return 0;
        var at = ToCameraRelative(global.Interpolated(alpha).ToMatrix(), rotation);
        int drawn = 0;
        if (world.TryGet<SkinnedMeshRenderer>(entity, out var skinned) && !skinned.Mesh.IsEmpty)
        {
            draws.Skinned(entity, in skinned, in at);
            drawn++;
        }
        if (world.TryGet<MeshRenderer>(entity, out var mesh) && (!mesh.Mesh.IsEmpty || !mesh.Handle.IsEmpty))
        {
            draws.Mesh(entity, in mesh, in at);
            drawn++;
        }
        return drawn;
    }
}
