#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// FrameUpdate: resolves the world's cameras into CameraViews and mirrors the screen's into ActiveCamera
// (issue #76). Every world an Engine creates has one, owned by the engine (`sage.core`), like its
// scenes: a game with `"plugins": []` can still have a camera.
//
// **Order (decision D2).** Rigs (#78: first person, #79: third person, fixed and cinematic) live on
// camera entities and write `CameraPose` in FrameUpdate at display rate; each declares
// `Before = new[] { CameraDirector.Id }`, so the director reads this frame's pose. The director itself
// runs as early in FrameUpdate as that allows — the engine adds it before any plugin's systems — so that
// the FrameUpdate systems that read ActiveCamera (audio, weather, particles, a game's HUD) see this
// frame's view rather than last frame's. It is "last" among the camera writers, not last in the phase: a
// system that moves a camera in FrameUpdate declares Before the director, like a rig.
//
// **Resolution.** For each target ("" = the screen), the enabled camera with the highest priority wins
// (a tie: the lower entity id, so it is stable). Its pose is its CameraPose when a rig drives it, else
// its GlobalTransform interpolated to this frame. Out-of-range values resolve to the defaults (see
// Camera), so every view is usable as it stands. A winner with a CameraBlend running is eased from the
// pose the blend began at (issue #90, CameraBlend) before the screen is mirrored; so is whatever took the
// place of a camera turned off with a blend out (issue 4n-19). Split screen (issue 4n-19): each target has
// one view per `Camera.Slot`, each resolved as above; the screen's lowest slot is the main view, the one
// ActiveCamera mirrors.
//
// **ActiveCamera (decision D1).** ActiveCamera stays, as a mirror of the screen view that only the
// director writes once a camera entity draws to the screen: its position, rotation, field of view and
// clip planes are copied every frame, so the code that reads it and fixed-tick readers see the view the
// player sees. Until one does, the director leaves it **exactly** as it is — a game's own code may drive
// it — and the screen view is made from it (a null Entity), so a renderer that reads only CameraViews
// still draws. Since #78 a player pawn always has a camera entity (PlayerCameraSystem), so in a game this
// is the case with no player yet, or a world without the character plugin.
//
// **No special cases (#81).** The editor's free camera (`cam_free`) is a camera entity too, a
// `DebugCamera` at a priority above every other camera while it overrides the screen and below every
// other otherwise; the director resolves it like any camera. `ActiveCamera.RigEnabled` and `DrivenByRig`
// (the protocol with the free camera before #81) are neither read nor written any more.
[Experimental("SAGE0123")]
[System(Id, Phase.FrameUpdate)]
public sealed class CameraDirector : ISystem
{
    public const string Id = "sage.camera.director";

    // How far from unit a rig's rotation may be before it is taken for "not written" (a zeroed pose).
    private const float MinRotationLengthSquared = 1e-6f;

    private readonly World _world;
    private readonly CameraViews _views;
    private readonly Query<Camera, CameraPose> _rigged;
    private readonly Query<Camera, GlobalTransform> _placed;
    private readonly Query<Camera, CameraBlend> _blending;
    private ActiveCamera? _active;

    public CameraDirector(World world)
    {
        _world = world;
        _views = world.Resources.GetOrAdd(() => new CameraViews());
        _rigged = world.Query<Camera, CameraPose>();
        _placed = world.Query<Camera, GlobalTransform>().WithoutComponent<CameraPose>();
        _blending = world.Query<Camera, CameraBlend>();
    }

    public void Run(in SystemContext ctx)
    {
        // Installed by plugins after the director is built (the client, the character plugin), and a
        // headless world may never have one: looked up until found, then held.
        if (_active == null) _world.Resources.TryGet(out _active);

        _views.Begin();
        foreach (var (cameras, poses, entities) in _rigged.Chunks)
        {
            var c = cameras.Span;
            var p = poses.Span;
            for (int i = 0; i < c.Length; i++)
            {
                if (!c[i].Enabled) continue;
                _views.Offer(Resolve(c[i], entities.EntityAt(i), p[i].Position, p[i].Rotation));
            }
        }

        float alpha = ctx.Frame.Alpha;
        foreach (var (cameras, globals, entities) in _placed.Chunks)
        {
            var c = cameras.Span;
            var g = globals.Span;
            for (int i = 0; i < c.Length; i++)
            {
                if (!c[i].Enabled) continue;
                var pose = g[i].Interpolated(alpha);
                _views.Offer(Resolve(c[i], entities.EntityAt(i), pose.Position, pose.Rotation));
            }
        }

        // No camera entity on the screen: ActiveCamera as it is. If one had the screen until now (it was
        // turned off, destroyed, or its rig let go), ActiveCamera is left where that camera last was, not
        // snapped anywhere. Before the blends, so a camera blending out eases into it too.
        var active = _active;
        bool fallback = active != null && _views.IndexOfScreen() < 0;
        if (fallback) _views.SetScreen(FromActiveCamera(active!));

        // Blends (issue #90): a camera that won its target and is blending in has its view eased from
        // where the screen was, before anything — ActiveCamera included — reads it. Blending out (issue
        // 4n-19): a camera turned off with a blend eases whatever now holds its target and slot from where
        // it was.
        foreach (var (cameras, blends, entities) in _blending.Chunks)
        {
            var c = cameras.Span;
            var b = blends.Span;
            for (int i = 0; i < c.Length; i++)
            {
                if (!b[i].Active || c[i].Enabled == b[i].Out) continue;   // in: while on; out: while off
                var self = entities.EntityAt(i);
                int at = b[i].Out ? _views.IndexOf(c[i].Target ?? "", Math.Max(0, c[i].Slot)) : _views.IndexOf(self);
                if (at >= 0) CameraBlends.Apply(b[i], ref _views.At(at), alpha);
            }
        }

        // ActiveCamera mirrors the screen's main view (blended) once a camera entity draws there.
        int screen = _views.IndexOfScreen();
        if (active != null && !fallback && screen >= 0)
        {
            ref readonly var main = ref _views[screen];
            active.Position = main.Position;
            active.Rotation = main.Rotation;
            active.FovY = main.FovY;
            active.Near = main.Near;
            active.Far = main.Far;
        }
        _views.End();
    }

    // A camera's view, every value made usable (Camera says what the defaults are).
    internal static CameraView Resolve(in Camera camera, Entity entity, Vector3 position, Quaternion rotation)
    {
        float near = camera.Near > 0f && float.IsFinite(camera.Near) ? camera.Near : Camera.DefaultNear;
        float far = camera.Far > near && float.IsFinite(camera.Far) ? camera.Far : MathF.Max(Camera.DefaultFar, near * 2f);
        float fov = camera.FovY > 0f && camera.FovY < 180f ? camera.FovY : Camera.DefaultFovY;
        return new CameraView
        {
            Entity = entity,
            Target = camera.Target ?? "",
            Viewport = camera.Viewport.Resolved(),
            Position = position,
            Rotation = rotation.LengthSquared() > MinRotationLengthSquared ? Quaternion.Normalize(rotation) : Quaternion.Identity,
            Projection = camera.Projection,
            FovY = MathF.Min(fov * MathF.PI / 180f, CameraMath.MaxFovY),
            OrthoHeight = camera.OrthoHeight > 0f && float.IsFinite(camera.OrthoHeight) ? camera.OrthoHeight : Camera.DefaultOrthoHeight,
            Near = near,
            Far = far,
            Priority = camera.Priority,
            Slot = Math.Max(0, camera.Slot),
            NoShadows = camera.NoShadows,
            NoViewmodel = camera.NoViewmodel,
            NoSky = camera.NoSky,
            NoDebugLines = camera.NoDebugLines,
        };
    }

    // The screen view made from ActiveCamera, when no camera entity draws there (also TryGetMainView's).
    internal static CameraView FromActiveCamera(ActiveCamera active) => new()
    {
        Entity = default,   // IsNull: made from ActiveCamera
        Target = "",
        Viewport = CameraViewport.Full,
        Position = active.Position,
        Rotation = active.Rotation,
        Projection = CameraProjection.Perspective,
        FovY = active.FovY,
        OrthoHeight = Camera.DefaultOrthoHeight,
        Near = active.Near,
        Far = active.Far,
        Priority = int.MinValue,
    };
}
