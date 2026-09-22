#nullable enable
using Microsoft.Xna.Framework;

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
                if (mr.Mesh.IsEmpty) continue;
                int meshId = _renderer.ResolveMesh(mr.Mesh);
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
