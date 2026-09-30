#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;




namespace Sage.Client;
// Extract (docs/design/06 §3.1, §5): the only code that reads simulation components for rendering.
// It writes the world's RenderSnapshot; Render then draws only from the snapshot.

// Extract, first: clears the snapshot and extracts the views and the environment: the views from the
// world's ViewSource (ActiveCamera today, camera components later; the camera at display rate, not
// interpolated, 06 §3.3), the environment from RenderEnvironment.
//
// A world that does not draw to the screen (`Renderer.ScreenWorld`) keeps only its views into
// render targets: there is one screen, and one world's picture on it (issue #77).
[System("sage.client.extract.camera", Phase.Extract)]
internal sealed class CameraExtract : ISystem
{
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;
    private readonly RenderEnvironment _environment;
    private readonly ViewSource _source;
    private readonly PooledList<ViewRequest> _requests = new(4);

    public CameraExtract(World world, Renderer renderer, ViewSource source)
    {
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _environment = world.Resources.Get<RenderEnvironment>();
        _source = source;
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        s.Clear();

        _source.Collect(_requests);
        bool screen = _renderer.IsScreenWorld(ctx.World);
        s.EnsureViewSlots(_requests.Count);
        for (int r = 0; r < _requests.Count; r++)
        {
            ref readonly var request = ref _requests[r];
            if (request.Target == RenderViewPlan.Screen && !screen) continue;
            int index = AddView(s, _renderer, request);
            if (request.Main && request.Target == RenderViewPlan.Screen && s.MainView < 0) s.MainView = index;
        }

        var e = _environment;
        ref var env = ref s.Environment;
        env.ClearColor = e.ClearColor;
        env.FogColor = e.FogColor;
        env.FogParams = new Vector4(e.FogStart, e.FogEnd, e.Fog && _renderer.FogEnabled ? 1f : 0f, 0f);
        env.SunDirection = Vector3.Normalize(e.SunDirection);
        env.SunColor = e.SunColor;
        env.AmbientSky = e.AmbientSky;
        env.AmbientGround = e.AmbientGround;
        env.Time = (float)ctx.Frame.RealTime;
    }

    // One view into the snapshot, with its culling frustum (kept while r_freezecull holds it); its index.
    // Also what a render pass's `RenderContext.AddView` does. Allocates only when the view count grows.
    internal static int AddView(RenderSnapshot s, Renderer renderer, in ViewRequest request)
    {
        int index = s.Views.Count;
        s.EnsureViewSlots(index + 1);
        ref var view = ref s.Views.Add();
        Build(request, renderer.TargetSize(request.Target), ref view);
        if (!renderer.FreezeCull || !s.CullValid[index])
        {
            s.Frustum(index).Matrix = view.ViewProj;
            s.CullOrigins[index] = view.CameraPosition;
            s.CullValid[index] = true;
        }
        return index;
    }

    // A request in a target of `size` pixels → a camera-relative view. The aspect ratio is the
    // viewport's own, so half a screen or a square minimap is not stretched to the window's shape.
    internal static void Build(in ViewRequest request, Point size, ref RenderView view)
    {
        var rect = request.Rect;
        int x = (int)MathF.Round(rect.X * size.X), y = (int)MathF.Round(rect.Y * size.Y);
        int w = Math.Max(1, (int)MathF.Round(rect.Z * size.X)), h = Math.Max(1, (int)MathF.Round(rect.W * size.Y));
        view.Viewport = new Rectangle(x, y, w, h);
        view.FullTarget = x == 0 && y == 0 && w == size.X && h == size.Y;
        view.Target = request.Target;
        view.Order = request.Order;
        view.Hidden = request.Hidden.IsNull ? 0 : request.Hidden.Id;
        view.DepthOnly = false;          // pooled: the slot may have been last frame's viewmodel view

        Vector3 forward = Vector3.Transform(Vector3.Forward, request.Rotation);
        Vector3 up = Vector3.Transform(Vector3.Up, request.Rotation);
        float aspect = w / (float)h;
        view.CameraPosition = request.Position;
        view.Forward = forward;
        view.Near = request.Near;
        view.Far = request.Far;
        view.View = Matrix.CreateLookAt(Vector3.Zero, forward, up);   // camera-relative: no translation
        view.Projection = request.Orthographic
            ? Matrix.CreateOrthographic(request.OrthoHeight * aspect, request.OrthoHeight, request.Near, request.Far)
            : Matrix.CreatePerspectiveFieldOfView(request.FovY, aspect, request.Near, request.Far);
        view.ViewProj = view.View * view.Projection;
        view.LightStart = view.LightCount = 0;
        view.DebugStart = view.DebugCount = 0;
        view.Culled = 0;
        view.ItemStart = view.ItemCount = 0;
        view.SpriteStart = view.SpriteCount = 0;
    }
}

// Extract: one RenderItem per visible mesh part (06 §5): the pose interpolated between the last two
// ticks, made camera-relative, frustum-culled, with its sort key.
[System("sage.client.extract.meshes", Phase.Extract, After = new[] { "sage.client.extract.camera" })]
internal sealed class MeshExtract : ISystem
{
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;
    private readonly Query<GlobalTransform, MeshRenderer> _meshes;

    public MeshExtract(World world, Renderer renderer)
    {
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _meshes = world.Query<GlobalTransform, MeshRenderer>().WithoutAnyTags(Tags.Get<ViewmodelLayer>());   // the viewmodel pass draws those (#121)
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        int views = s.Views.Count;
        if (views == 0) return;
        float alpha = ctx.Frame.Alpha;
        var materials = _renderer.Materials;
        bool hiding = AnyHidden(s);

        foreach (var (globals, renderers, entities) in _meshes.Chunks)
        {
            var g = globals.Span;
            var r = renderers.Span;
            for (int n = 0; n < g.Length; n++)
            {
                ref readonly var mr = ref r[n];
                if (mr.Handle.IsEmpty && mr.Mesh.IsEmpty) continue;
                int meshId = mr.Handle.IsEmpty ? _renderer.ResolveMesh(mr.Mesh) : mr.Handle.Id;
                var mesh = _renderer.Mesh(meshId);
                int materialId = mesh.IsError ? 0 : materials.Resolve(mr.Material);
                var material = materials.Get(materialId);
                if (material == null) continue;

                // Interpolated once; made camera-relative once per view (issue #77).
                Matrix pose = g[n].Interpolated(alpha).ToMatrix();   // System.Numerics → MonoGame (implicit)
                int id = hiding ? entities.EntityAt(n).Id : 0;

                for (int v = 0; v < views; v++)
                {
                    ref var view = ref s.Views[v];
                    if (hiding && view.Hidden != 0 && view.Hidden == id) continue;   // a camera's own body (ViewSource.HiddenFor)
                    Vector3 cullOffset = view.CameraPosition - s.CullOrigins[v];   // non-zero only while r_freezecull holds an old frustum
                    var frustum = s.Frustum(v);
                    Matrix world = pose;
                    world.Translation -= view.CameraPosition;

                    for (int p = 0; p < mesh.Parts.Length; p++)
                    {
                        var part = mesh.Parts[p];
                        Matrix partWorld = part.Bone * world;
                        Vector3 center = Vector3.Transform(part.Bounds.Center, partWorld);
                        float radius = part.Bounds.Radius * MaxScale(partWorld);
                        if (!frustum.Intersects(new BoundingSphere(center + cullOffset, radius)))
                        {
                            s.Culled++;
                            view.Culled++;
                            continue;
                        }

                        ref var item = ref s.Items.Add();
                        item.Mesh = meshId;
                        item.Part = p;
                        item.Material = materialId;
                        item.World = partWorld;
                        item.Tint = Vector4.One;
                        item.SortKey = RenderSortKey.Make(material.Pass, mr.Layer, materialId, meshId, Vector3.Dot(center, view.Forward), view.Far);
                        item.View = v;
                        item.BoneStart = item.BoneCount = 0;   // pooled: the slot may have been a skinned draw
                    }
                }
            }
        }
    }

    // Whether any view leaves an entity out: only then is each entity's id worth looking up.
    internal static bool AnyHidden(RenderSnapshot s)
    {
        for (int v = 0; v < s.Views.Count; v++) if (s.Views[v].Hidden != 0) return true;
        return false;
    }

    internal static float MaxScale(in Matrix m) =>
        MathHelper.Max(new Vector3(m.M11, m.M12, m.M13).Length(), MathHelper.Max(new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()));
}

// Extract: skinned meshes (issue #117). As MeshExtract, plus each renderer's joint palette: written once
// into RenderSnapshot.Bones by `SkinnedExtract.Emit` (headless, tested) at the first view that sees it,
// and shared by every view's items. The joints are the entity's `SkeletonPose` in `SkinPoses` when
// something poses it (put in the skin's order, `SkinPoses.ToSkinOrder`), else the mesh's rest pose.
// Hook for #118: its AnimatorSystem `Set`s each animated entity's pose there; this system only reads it.
[System("sage.client.extract.skinned", Phase.Extract, After = new[] { "sage.client.extract.camera" })]
internal sealed class SkinnedMeshExtract : ISystem
{
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;
    private readonly SkinPoses _poses;
    private readonly Query<GlobalTransform, SkinnedMeshRenderer> _meshes;
    private readonly HashSet<int> _mismatched = new();   // entities already warned about
    private System.Numerics.Matrix4x4[] _skinJoints = new System.Numerics.Matrix4x4[SkinMath.MaxBones];   // grown, never shrunk

    public SkinnedMeshExtract(World world, Renderer renderer)
    {
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _poses = world.Resources.GetOrAdd(() => new SkinPoses());
        _meshes = world.Query<GlobalTransform, SkinnedMeshRenderer>().WithoutAnyTags(Tags.Get<ViewmodelLayer>());   // the viewmodel pass draws those (#121)
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        if (s.Views.Count == 0) return;
        float alpha = ctx.Frame.Alpha;
        var materials = _renderer.Materials;
        bool hiding = MeshExtract.AnyHidden(s);

        foreach (var (globals, renderers, entities) in _meshes.Chunks)
        {
            var g = globals.Span;
            var r = renderers.Span;
            for (int n = 0; n < g.Length; n++)
            {
                ref readonly var sr = ref r[n];
                if (sr.Mesh.IsEmpty) continue;
                int meshId = _renderer.ResolveMesh(sr.Mesh);
                var mesh = _renderer.Mesh(meshId);
                int materialId = mesh.IsError ? 0 : materials.Resolve(sr.Material);
                var material = materials.Get(materialId);
                if (material == null) continue;

                int id = entities.EntityAt(n).Id;
                var skin = mesh.Skin;
                ReadOnlySpan<System.Numerics.Matrix4x4> joints = default, inverseBind = default;
                if (skin != null)
                {
                    inverseBind = skin.InverseBind;
                    joints = skin.RestJoints;
                    if (_poses.TryGet(id, out var pose))
                    {
                        int count = skin.RestJoints.Length;
                        if (_skinJoints.Length < count) _skinJoints = new System.Numerics.Matrix4x4[count];
                        if (SkinPoses.ToSkinOrder(pose, _skinJoints.AsSpan(0, count))) joints = _skinJoints.AsSpan(0, count);
                        else if (!_mismatched.Contains(id))
                        {
                            _mismatched.Add(id);
                            Log.Warn(LogCat.Render, $"Entity {id}: its pose has {pose.Skeleton.JointOfSkinIndex.Length} skin joints and '{mesh.Name}' has {count}; drawn in the rest pose");
                        }
                    }
                }

                var draws = new Draws
                {
                    Snapshot = s, Mesh = mesh, MeshId = meshId, MaterialId = materialId, Pass = material.Pass, Layer = sr.Layer,
                    Pose = g[n].Interpolated(alpha).ToMatrix(),   // System.Numerics → MonoGame (implicit)
                    Hidden = hiding ? id : 0,
                };
                SkinnedExtract.Emit(ref draws, joints, inverseBind, mesh.Name);
            }
        }
    }

    // One renderer's views and items, for SkinnedExtract.Emit.
    private struct Draws : ISkinnedDraws
    {
        public RenderSnapshot Snapshot;
        public MeshData Mesh;
        public int MeshId, MaterialId, Hidden;
        public RenderPass Pass;
        public byte Layer;
        public Matrix Pose;

        public readonly int Views => Snapshot.Views.Count;

        // In the view's frustum (the rest-pose sphere of the whole mesh), and not the view's own body.
        public readonly bool Sees(int v)
        {
            var s = Snapshot;
            ref var view = ref s.Views[v];
            if (Hidden != 0 && view.Hidden == Hidden) return false;   // a camera's own body (ViewSource.HiddenFor)
            Matrix world = Pose;
            world.Translation -= view.CameraPosition;
            var bounds = Mesh.Parts[0].Bounds;
            Vector3 center = Vector3.Transform(bounds.Center, world) + (view.CameraPosition - s.CullOrigins[v]);
            if (s.Frustum(v).Intersects(new BoundingSphere(center, bounds.Radius * MeshExtract.MaxScale(world)))) return true;
            s.Culled++;
            view.Culled++;
            return false;
        }

        public readonly Span<System.Numerics.Matrix4x4> AddBones(int count, out int start)
        {
            start = Snapshot.Bones.Count;
            return Snapshot.Bones.AddRange(count);
        }

        public readonly void Draw(int v, int boneStart, int boneCount)
        {
            var s = Snapshot;
            ref var view = ref s.Views[v];
            Matrix world = Pose;
            world.Translation -= view.CameraPosition;
            for (int p = 0; p < Mesh.Parts.Length; p++)
            {
                var part = Mesh.Parts[p];
                Matrix partWorld = part.Bone * world;
                Vector3 center = Vector3.Transform(part.Bounds.Center, partWorld);
                ref var item = ref s.Items.Add();
                item.Mesh = MeshId;
                item.Part = p;
                item.Material = MaterialId;
                item.World = partWorld;
                item.Tint = Vector4.One;
                item.SortKey = RenderSortKey.Make(Pass, Layer, MaterialId, MeshId, Vector3.Dot(center, view.Forward), view.Far);
                item.View = v;
                bool skinned = part.Skinned && boneCount > 0;
                item.BoneStart = skinned ? boneStart : 0;
                item.BoneCount = skinned ? boneCount : 0;
            }
        }
    }
}

// Render (06 §3.4): draws the snapshot. Nothing here reads components.
[System("sage.client.render", Phase.Render)]
internal sealed class RenderSystem : ISystem
{
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;

    public RenderSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
    }

    public void Run(in SystemContext ctx) => _renderer.Draw(_snapshot, _renderer.IsScreenWorld(ctx.World), ctx.World);
}

// Extract, right after the cameras: every render pass's Extract hook (issue 4h-1), so a view a pass adds
// is one the world's meshes, sprites, lights and debug lines are extracted into like any camera's.
[System("sage.client.extract.passes", Phase.Extract, After = new[] { "sage.client.extract.camera" }, Before = new[]
{
    "sage.client.extract.meshes", "sage.client.extract.skinned", "sage.client.extract.sprites",
    "sage.client.extract.lights", "sage.client.extract.debug", "?sage.client.extract.particles",
})]
internal sealed class RenderPassExtract : ISystem
{
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;

    public RenderPassExtract(World world, Renderer renderer)
    {
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
    }

    public void Run(in SystemContext ctx) => _renderer.ExtractPasses(ctx.World, _snapshot, _renderer.IsScreenWorld(ctx.World));
}

// Extract: one SpriteInstance per visible billboard (06 §3.8). It picks the direction group from the
// angle between the sprite and the camera, and the animation frame from the animator's time (12 §3);
// the batcher expands the quads. Sprites without a SpriteAnimator show frame 0 of their first clip.
// Extract: copies the simulation's debug shapes into the snapshot, camera-relative like everything
// else, and ages the queue (06 §3.2). It also carries `r_debugdraw` the other way, so nothing in the
// simulation records shapes nobody is going to look at.
// Extract: the frame's point lights, camera-relative like everything else (06 §3.9).
//
// No culling beyond what `LightRules` does per object: a hundred lamps in a level is a list of a hundred
// structs, and the work that matters is per *draw*, not per light. When that stops being true the answer
// is a grid, not a longer loop here.
[System("sage.client.extract.lights", Phase.Extract, After = new[] { "sage.client.extract.camera" })]
internal sealed class LightExtract : ISystem
{
    private readonly Query<GlobalTransform, PointLight> _lights;
    private readonly RenderSnapshot _snapshot;
    private readonly CVar<bool> _enabled;

    public LightExtract(World world, Renderer renderer, CVar<bool> enabled)
    {
        _lights = world.Query<GlobalTransform, PointLight>();
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _enabled = enabled;
    }

    public void Run(in SystemContext ctx)
    {
        var snapshot = _snapshot;
        // `r_lights 0` leaves the list empty, which the shader reads as "no lamps" — the old look, and
        // the quickest way to see what the lights are actually contributing.
        if (!snapshot.HasView || !_enabled.Value) return;

        float alpha = ctx.Frame.Alpha;
        // A view at a time, so each view's lights are one run of the list (RenderView.LightStart).
        for (int v = 0; v < snapshot.Views.Count; v++)
        {
            ref var view = ref snapshot.Views[v];
            Vector3 camera = view.CameraPosition;
            view.LightStart = snapshot.Lights.Count;

            foreach (var (globals, lights, _) in _lights.Chunks)
                for (int n = 0; n < globals.Length; n++)
                {
                    ref readonly var light = ref lights[n];
                    if (light.Range <= 0f || light.Intensity <= 0f) continue;

                    var world = globals[n].Interpolated(alpha).Position;
                    var at = new System.Numerics.Vector3(world.X - camera.X, world.Y - camera.Y, world.Z - camera.Z);
                    snapshot.Lights.Add() = new LightSample(at, light.Colour * light.Intensity, light.Range);
                }
            view.LightCount = snapshot.Lights.Count - view.LightStart;
        }
    }
}

[System("sage.client.extract.debug", Phase.Extract, After = new[] { "sage.client.extract.camera" })]
internal sealed class DebugExtract : ISystem
{
    private readonly RenderSnapshot _snapshot;
    private readonly DebugDraw _debug;
    private readonly CVar<bool> _enabled;
    private readonly List<DebugLine> _lines = new(256);   // reused: the frame budget allows no garbage

    public DebugExtract(World world, CVar<bool> enabled)
    {
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _debug = world.Resources.Get<DebugDraw>();
        _enabled = enabled;
    }

    public void Run(in SystemContext ctx)
    {
        _debug.Enabled = _enabled.Value;
        if (!_enabled.Value) { _debug.Clear(); return; }

        _lines.Clear();
        _debug.CopyTo(_lines);

        // Every view gets its own camera-relative, near-clipped copy (one run each); the queue ages once.
        var s = _snapshot;
        for (int v = 0; v < s.Views.Count; v++)
        {
            ref var view = ref s.Views[v];
            var camera = view.CameraPosition;
            view.DebugStart = s.DebugLines.Count;
            foreach (var line in _lines)
            {
                var colour = new Color((byte)(line.Rgba >> 24), (byte)(line.Rgba >> 16), (byte)(line.Rgba >> 8), (byte)line.Rgba);
                Vector3 a = line.A, b = line.B;   // System.Numerics → MonoGame (implicit)
                a -= camera;
                b -= camera;
                if (!ClipToNear(view, ref a, ref b)) continue;
                s.DebugLines.Add() = new VertexPositionColor(a, colour);
                s.DebugLines.Add() = new VertexPositionColor(b, colour);
            }
            view.DebugCount = s.DebugLines.Count - view.DebugStart;
        }
        _debug.Advance((float)ctx.Frame.Dt);
    }

    // A line list is not clipped for you: a segment with an endpoint behind the camera comes out as a
    // streak across the whole screen (you see it the moment you draw your own capsule from inside it).
    // Both ends are camera-relative, so "in front" is just a dot with the view direction.
    private static bool ClipToNear(in RenderView view, ref Vector3 a, ref Vector3 b)
    {
        float near = view.Near + 0.01f;
        float da = Vector3.Dot(a, view.Forward) - near;
        float db = Vector3.Dot(b, view.Forward) - near;
        if (da < 0f && db < 0f) return false;                     // wholly behind: nothing to draw
        if (da < 0f) a = Vector3.Lerp(a, b, da / (da - db));      // crosses the plane: pull it forward
        else if (db < 0f) b = Vector3.Lerp(b, a, db / (db - da));
        return true;
    }
}

[System("sage.client.extract.sprites", Phase.Extract, After = new[] { "sage.client.extract.camera" })]
internal sealed class SpriteExtract : ISystem
{
    private readonly Renderer _renderer;
    private readonly RecordStore _records;
    private readonly RenderSnapshot _snapshot;
    private readonly Query<GlobalTransform, SpriteRenderer> _sprites;

    public SpriteExtract(World world, Renderer renderer, RecordStore records)
    {
        _renderer = renderer;
        _records = records;
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _sprites = world.Query<GlobalTransform, SpriteRenderer>().WithoutAnyTags(Tags.Get<ViewmodelLayer>());
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        int views = s.Views.Count;
        if (views == 0) return;
        float alpha = ctx.Frame.Alpha;
        var materials = _renderer.Materials;

        foreach (var (globals, renderers, entities) in _sprites.Chunks)
        {
            var g = globals.Span;
            var r = renderers.Span;
            for (int n = 0; n < g.Length; n++)
            {
                ref readonly var sr = ref r[n];
                if (!_records.TryGet(sr.Sheet, out SpriteSheetRecord sheet))
                {
                    // Records that name a sheet are validated at load; this catches sheets set from code.
                    Log.Once(LogCat.Render, LogLevel.Warn, $"sheet:{sr.Sheet}", $"Sprite sheet {sr.Sheet} not found; nothing drawn for it");
                    continue;
                }
                int texture = _renderer.ResolveTexture(sheet.Texture);
                var pose = g[n].Interpolated(alpha);
                Vector3 position = pose.Position;   // System.Numerics → MonoGame (implicit)
                var entity = entities.EntityAt(n);
                bool animated = entity.TryGetComponent(out SpriteAnimator animator);
                int id = entity.Id;

                Vector2 size = sr.Size == Vector2.Zero ? sheet.Size : sr.Size;
                Vector2 scale = new(pose.Scale.X, pose.Scale.Y);
                size *= scale;
                float radius = 0.5f * MathF.Sqrt(size.X * size.X + size.Y * size.Y);

                int materialId = materials.Resolve(!sr.Material.IsEmpty ? sr.Material
                    : !sheet.Material.IsEmpty ? sheet.Material : SpriteSheetRecord.DefaultMaterial);
                var material = materials.Get(materialId);
                if (material == null) continue;

                // Per view (issue #77): which way it faces depends on where *this* camera is.
                for (int v = 0; v < views; v++)
                {
                    ref var view = ref s.Views[v];
                    if (view.Hidden != 0 && view.Hidden == id) continue;   // a camera's own body (ViewSource.HiddenFor)
                    Vector3 camera = view.CameraPosition;

                    // Bounding sphere around the quad, for culling: the pivot may be at the feet, so the
                    // sphere is centred half a height up and sized by the diagonal.
                    Vector3 center = position - camera;
                    if (!s.Frustum(v).Intersects(new BoundingSphere(center + Vector3.Up * (size.Y * 0.5f) + (camera - s.CullOrigins[v]), radius)))
                    {
                        s.Culled++;
                        view.Culled++;
                        continue;
                    }

                    // Which way does it face the camera, and which frame is playing?
                    int direction = SpriteMath.DirectionIndex(pose.Position, camera.ToNumerics(), SageMath.YawOf(pose.Rotation), sheet.Directions, out bool flipU);
                    int frameIndex = 0;
                    if (animated && sheet.Clip(animator.Clip) is { } clip)
                        frameIndex = SpriteMath.FrameAt(clip, direction, animator.Time);
                    else if (sheet.Clip(0) is { } first)
                        frameIndex = SpriteMath.FrameAt(first, direction, 0f);
                    if (frameIndex < 0 || frameIndex >= sheet.Frames.Count) continue;
                    var frame = sheet.Frames[frameIndex];

                    ref var instance = ref s.Sprites.Add();
                    instance.Center = center;
                    instance.Size = size;
                    instance.Pivot = _renderer.Pivot(texture, frame);
                    instance.Uv = _renderer.Uv(texture, frame, flipU);
                    instance.Tint = Vector4.One;
                    instance.Material = materialId;
                    instance.Texture = texture;
                    instance.Mode = sr.Mode;
                    instance.Roll = 0f;                 // sprites stand upright; only particles turn (06 §3.12)
                    instance.SortKey = RenderSortKey.Make(material.Pass, sr.Layer, materialId, texture,
                        Vector3.Dot(center, view.Forward), view.Far);
                    instance.View = v;
                }
            }
        }
    }
}
