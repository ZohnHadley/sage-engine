#nullable enable
using System;
using Microsoft.Xna.Framework;

namespace Sage.Client;

// Extract, last: the viewmodel pass (issue #121, docs/design/06 "As built (the viewmodel pass)").
//
// For every view that looks out of a first-person rig whose camera has a Viewmodel (ViewmodelPass.TryGet,
// headless and tested; every view since issue 4n-19, so each split-screen partner draws its own arms),
// this adds **one more view** to the snapshot: that view's viewport, rotation and lights, with the viewmodel's own field of view and depth range, drawn after it on the same target
// (a higher Order) and clearing only depth first (RenderView.DepthOnly). Its items are the arms and the
// weapon, which every other extract leaves out (the `sage:viewmodel_layer` tag): their poses are in view
// space, so ViewmodelPass turns them camera-relative with the view's rotation. The world's lights reach
// them because the view shares the main view's run of lights (the same camera position).
//
// It runs after every other extract so they never see its view: none of them culls, lights or copies
// debug lines into it. No culling here either: two pieces in front of the eye. Allocates nothing.
[System("sage.client.extract.viewmodel", Phase.Extract, After = new[]
{
    "sage.client.extract.camera", "sage.client.extract.meshes", "sage.client.extract.skinned", "sage.client.extract.sprites",
    "sage.client.extract.lights", "sage.client.extract.debug", "?sage.client.extract.particles",
})]
internal sealed class ViewmodelExtract : ISystem
{
    private readonly World _world;
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;
    private readonly SkinPoses _poses;
    private System.Numerics.Matrix4x4[] _skinJoints = new System.Numerics.Matrix4x4[SkinMath.MaxBones];   // grown, never shrunk

    public ViewmodelExtract(World world, Renderer renderer)
    {
        _world = world;
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _poses = world.Resources.GetOrAdd(() => new SkinPoses());
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        if (s.Views.Count == 0) return;
        if (!_world.Resources.TryGet<CameraViews>(out var cameras) || cameras == null) return;

        // Every view drawn from a camera (issue 4n-19), not only the screen's main one: each split-screen
        // partner looking out of its own first-person rig gets its own arms. The views this adds are not
        // looked at again (the count is taken first).
        int views = s.Views.Count;
        for (int v = 0; v < views; v++)
        {
            var main = s.Views[v];   // a copy: Add below may move the list
            if (main.ShadowCaster || main.DepthOnly || main.NoViewmodel || main.Source < 0 || main.Source >= cameras.Count) continue;
            if (!ViewmodelPass.TryGet(_world, in cameras[main.Source], out var viewmodel)) continue;
            Add(s, in main, in viewmodel, ctx.Frame.Alpha);
        }
    }

    // The viewmodel's view over `main`, and its pieces in it.
    private void Add(RenderSnapshot s, in RenderView main, in ViewmodelView viewmodel, float alpha)
    {
        int index = s.Views.Count;
        ref var view = ref s.Views.Add();
        view = main;
        float aspect = main.Viewport.Width / (float)Math.Max(1, main.Viewport.Height);
        view.Near = viewmodel.Near;
        view.Far = viewmodel.Far;
        view.Projection = Matrix.CreatePerspectiveFieldOfView(viewmodel.FovY, aspect, viewmodel.Near, viewmodel.Far);
        view.ViewProj = view.View * view.Projection;
        view.Order = main.Order + 1;          // after its view on the target (a split-screen partner's Order is its slot)
        view.DepthOnly = true;
        view.Hidden = 0;
        view.DebugStart = view.DebugCount = 0;
        view.Culled = 0;
        view.ItemStart = view.ItemCount = 0;
        view.SpriteStart = view.SpriteCount = 0;
        s.EnsureViewSlots(index + 1);          // per-view culling state; grows only when the view count does

        var draws = new Draws { Owner = this, Snapshot = s, View = index, Forward = view.Forward, Far = view.Far };
        ViewmodelPass.Emit(_world, in viewmodel, alpha, ref draws);
    }

    private void Item(RenderSnapshot s, int view, int meshId, int part, int materialId, RenderPass pass, byte layer,
                      in Matrix world, Vector3 forward, float far, int boneStart, int boneCount)
    {
        var mesh = _renderer.Mesh(meshId);
        var p = mesh.Parts[part];
        Matrix partWorld = p.Bone * world;
        Vector3 center = Vector3.Transform(p.Bounds.Center, partWorld);
        ref var item = ref s.Items.Add();
        item.Mesh = meshId;
        item.Part = part;
        item.Material = materialId;
        item.World = partWorld;
        item.Tint = Vector4.One;
        item.SortKey = RenderSortKey.Make(pass, layer, materialId, meshId, Vector3.Dot(center, forward), far);
        item.View = view;
        bool skinned = p.Skinned && boneCount > 0;
        item.BoneStart = skinned ? boneStart : 0;
        item.BoneCount = skinned ? boneCount : 0;
    }

    // The viewmodel's pieces into the snapshot, in its one view.
    private struct Draws : IViewmodelDraws
    {
        public ViewmodelExtract Owner;
        public RenderSnapshot Snapshot;
        public int View;
        public Vector3 Forward;
        public float Far;

        public readonly void Mesh(Entity entity, in MeshRenderer renderer, in System.Numerics.Matrix4x4 world)
        {
            var r = Owner._renderer;
            int meshId = renderer.Handle.IsEmpty ? r.ResolveMesh(renderer.Mesh) : renderer.Handle.Id;
            var mesh = r.Mesh(meshId);
            int materialId = mesh.IsError ? 0 : r.Materials.Resolve(renderer.Material);
            var material = r.Materials.Get(materialId);
            if (material == null) return;
            Matrix at = world;   // System.Numerics → MonoGame (implicit)
            for (int p = 0; p < mesh.Parts.Length; p++)
                Owner.Item(Snapshot, View, meshId, p, materialId, material.Pass, renderer.Layer, at, Forward, Far, 0, 0);
        }

        public readonly void Skinned(Entity entity, in SkinnedMeshRenderer renderer, in System.Numerics.Matrix4x4 world)
        {
            var r = Owner._renderer;
            int meshId = r.ResolveMesh(renderer.Mesh);
            var mesh = r.Mesh(meshId);
            int materialId = mesh.IsError ? 0 : r.Materials.Resolve(renderer.Material);
            var material = r.Materials.Get(materialId);
            if (material == null) return;

            ReadOnlySpan<System.Numerics.Matrix4x4> joints = default, inverseBind = default;
            if (mesh.Skin is { } skin)
            {
                inverseBind = skin.InverseBind;
                joints = skin.RestJoints;
                int count = skin.RestJoints.Length;
                if (Owner._poses.TryGet(entity.Id, out var pose))
                {
                    if (Owner._skinJoints.Length < count) Owner._skinJoints = new System.Numerics.Matrix4x4[count];
                    if (SkinPoses.ToSkinOrder(pose, Owner._skinJoints.AsSpan(0, count))) joints = Owner._skinJoints.AsSpan(0, count);
                }
            }
            var one = new OneView
            {
                Owner = Owner, Snapshot = Snapshot, View = View, MeshId = meshId, MaterialId = materialId, Pass = material.Pass,
                Layer = renderer.Layer, Parts = mesh.Parts.Length, World = world, Forward = Forward, Far = Far,
            };
            SkinnedExtract.Emit(ref one, joints, inverseBind, mesh.Name);
        }
    }

    // SkinnedExtract.Emit's side for the one viewmodel view: it sees everything, and writes one palette.
    private struct OneView : ISkinnedDraws
    {
        public ViewmodelExtract Owner;
        public RenderSnapshot Snapshot;
        public int View, MeshId, MaterialId, Parts;
        public RenderPass Pass;
        public byte Layer;
        public Matrix World;
        public Vector3 Forward;
        public float Far;

        public readonly int Views => Snapshot.Views.Count;
        public readonly bool Sees(int view) => view == View;

        public readonly Span<System.Numerics.Matrix4x4> AddBones(int count, out int start)
        {
            start = Snapshot.Bones.Count;
            return Snapshot.Bones.AddRange(count);
        }

        public readonly void Draw(int view, int boneStart, int boneCount)
        {
            for (int p = 0; p < Parts; p++)
                Owner.Item(Snapshot, view, MeshId, p, MaterialId, Pass, Layer, World, Forward, Far, boneStart, boneCount);
        }
    }
}
