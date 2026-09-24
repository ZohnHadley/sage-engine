#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// Extract (docs/design/06 §3.1, §5): the only code that reads simulation components for rendering.
// It writes the world's RenderSnapshot; Render then draws only from the snapshot.

// Extract, first: clears the snapshot and extracts the view and environment from the world's
// ActiveCamera and RenderEnvironment resources (the camera at display rate, not interpolated, 06 §3.3).
internal sealed class CameraExtract : ISystem
{
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;
    private readonly ActiveCamera _camera;
    private readonly RenderEnvironment _environment;

    public CameraExtract(World world, Renderer renderer)
    {
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _camera = world.Resources.Get<ActiveCamera>();
        _environment = world.Resources.Get<RenderEnvironment>();
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        s.Clear();

        Quaternion rotation = _camera.Rotation;
        Vector3 forward = Vector3.Transform(Vector3.Forward, rotation);
        Vector3 up = Vector3.Transform(Vector3.Up, rotation);
        float aspect = _renderer.Device.Viewport.AspectRatio;

        ref var view = ref s.View;
        view.CameraPosition = _camera.Position;
        view.Forward = forward;
        view.Near = _camera.Near;
        view.Far = _camera.Far;
        view.View = Matrix.CreateLookAt(Vector3.Zero, forward, up);   // camera-relative: no translation
        view.Projection = Matrix.CreatePerspectiveFieldOfView(_camera.FovY, aspect, _camera.Near, _camera.Far);
        view.ViewProj = view.View * view.Projection;
        s.HasView = true;

        if (!_renderer.FreezeCull || !s.CullValid)
        {
            s.Frustum.Matrix = view.ViewProj;
            s.CullOrigin = view.CameraPosition;
            s.CullValid = true;
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
}

// Extract: one RenderItem per visible mesh part (06 §5): the pose interpolated between the last two
// ticks, made camera-relative, frustum-culled, with its sort key.
internal sealed class MeshExtract : ISystem
{
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;
    private readonly Friflo.Engine.ECS.ArchetypeQuery<GlobalTransform, MeshRenderer> _meshes;

    public MeshExtract(World world, Renderer renderer)
    {
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _meshes = world.Query<GlobalTransform, MeshRenderer>();
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        if (!s.HasView) return;
        Vector3 camera = s.View.CameraPosition;
        Vector3 cullOffset = camera - s.CullOrigin;   // non-zero only while r_freezecull holds an old frustum
        float alpha = ctx.Frame.Alpha;
        var materials = _renderer.Materials;

        foreach (var (globals, renderers, _) in _meshes.Chunks)
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

                Matrix world = g[n].Interpolated(alpha).ToMatrix();   // System.Numerics → MonoGame (implicit)
                world.Translation -= camera;

                for (int p = 0; p < mesh.Parts.Length; p++)
                {
                    var part = mesh.Parts[p];
                    Matrix partWorld = part.Bone * world;
                    Vector3 center = Vector3.Transform(part.Bounds.Center, partWorld);
                    float radius = part.Bounds.Radius * MaxScale(partWorld);
                    if (!s.Frustum.Intersects(new BoundingSphere(center + cullOffset, radius)))
                    {
                        s.Culled++;
                        continue;
                    }

                    ref var item = ref s.Items.Add();
                    item.Mesh = meshId;
                    item.Part = p;
                    item.Material = materialId;
                    item.World = partWorld;
                    item.Tint = Vector4.One;
                    item.SortKey = RenderSortKey.Make(material.Pass, mr.Layer, materialId, meshId, Vector3.Dot(center, s.View.Forward), s.View.Far);
                }
            }
        }
    }

    private static float MaxScale(in Matrix m) =>
        MathHelper.Max(new Vector3(m.M11, m.M12, m.M13).Length(), MathHelper.Max(new Vector3(m.M21, m.M22, m.M23).Length(), new Vector3(m.M31, m.M32, m.M33).Length()));
}

// Render (06 §3.4): draws the snapshot. Nothing here reads components.
internal sealed class RenderSystem : ISystem
{
    private readonly Renderer _renderer;
    private readonly RenderSnapshot _snapshot;

    public RenderSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        _snapshot = world.Resources.Get<RenderSnapshot>();
    }

    public void Run(in SystemContext ctx) => _renderer.Draw(_snapshot);
}

// Extract: one SpriteInstance per visible billboard (06 §3.8). It picks the direction group from the
// angle between the sprite and the camera, and the animation frame from the animator's time (12 §3);
// the batcher expands the quads. Sprites without a SpriteAnimator show frame 0 of their first clip.
// Extract: copies the simulation's debug shapes into the snapshot, camera-relative like everything
// else, and ages the queue (06 §3.2). It also carries `r_debugdraw` the other way, so nothing in the
// simulation records shapes nobody is going to look at.
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

        var s = _snapshot;
        var camera = s.View.CameraPosition;
        foreach (var line in _lines)
        {
            var colour = new Color((byte)(line.Rgba >> 24), (byte)(line.Rgba >> 16), (byte)(line.Rgba >> 8), (byte)line.Rgba);
            Vector3 a = line.A, b = line.B;   // System.Numerics → MonoGame (implicit)
            a -= camera;
            b -= camera;
            if (!ClipToNear(s.View, ref a, ref b)) continue;
            s.DebugLines.Add() = new VertexPositionColor(a, colour);
            s.DebugLines.Add() = new VertexPositionColor(b, colour);
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

internal sealed class SpriteExtract : ISystem
{
    private readonly Renderer _renderer;
    private readonly RecordStore _records;
    private readonly RenderSnapshot _snapshot;
    private readonly Friflo.Engine.ECS.ArchetypeQuery<GlobalTransform, SpriteRenderer> _sprites;

    public SpriteExtract(World world, Renderer renderer, RecordStore records)
    {
        _renderer = renderer;
        _records = records;
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _sprites = world.Query<GlobalTransform, SpriteRenderer>();
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        if (!s.HasView) return;
        Vector3 camera = s.View.CameraPosition;
        Vector3 cullOffset = camera - s.CullOrigin;
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

                // Which way does it face the camera, and which frame is playing?
                int direction = SpriteMath.DirectionIndex(pose.Position, camera.ToNumerics(), SageMath.YawOf(pose.Rotation), sheet.Directions, out bool flipU);
                int frameIndex = 0;
                var entity = entities.EntityAt(n);
                if (entity.TryGetComponent(out SpriteAnimator animator) && sheet.Clip(animator.Clip) is { } clip)
                    frameIndex = SpriteMath.FrameAt(clip, direction, animator.Time);
                else if (sheet.Clip(0) is { } first)
                    frameIndex = SpriteMath.FrameAt(first, direction, 0f);
                if (frameIndex < 0 || frameIndex >= sheet.Frames.Count) continue;

                var frame = sheet.Frames[frameIndex];
                Vector2 size = sr.Size == Vector2.Zero ? sheet.Size : sr.Size;
                Vector2 scale = new(pose.Scale.X, pose.Scale.Y);
                size *= scale;

                // Bounding sphere around the quad, for culling: the pivot may be at the feet, so the
                // sphere is centred half a height up and sized by the diagonal.
                Vector3 center = position - camera;
                float radius = 0.5f * MathF.Sqrt(size.X * size.X + size.Y * size.Y);
                if (!s.Frustum.Intersects(new BoundingSphere(center + Vector3.Up * (size.Y * 0.5f) + cullOffset, radius)))
                {
                    s.Culled++;
                    continue;
                }

                int materialId = materials.Resolve(!sr.Material.IsEmpty ? sr.Material
                    : !sheet.Material.IsEmpty ? sheet.Material : SpriteSheetRecord.DefaultMaterial);
                var material = materials.Get(materialId);
                if (material == null) continue;

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
                    Vector3.Dot(center, s.View.Forward), s.View.Far);
            }
        }
    }
}
