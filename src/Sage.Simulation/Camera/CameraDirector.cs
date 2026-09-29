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
// runs *after* the legacy first-person rig (which still writes ActiveCamera directly until #78) and
// otherwise as early in FrameUpdate as registration puts it — the engine adds it before any plugin's
// systems — so that the FrameUpdate systems that read ActiveCamera (audio, weather, particles, a game's
// HUD) see this frame's view rather than last frame's. It is "last" among the camera writers, not last in
// the phase: a system that moves a camera in FrameUpdate declares Before the director, like a rig.
//
// **Resolution.** For each target ("" = the screen), the enabled camera with the highest priority wins
// (a tie: the lower entity id, so it is stable). Its pose is its CameraPose when a rig drives it, else
// its GlobalTransform interpolated to this frame. Out-of-range values resolve to the defaults (see
// Camera), so every view is usable as it stands.
//
// **ActiveCamera (decision D1).** ActiveCamera stays, as a mirror of the screen view that only the
// director writes once a camera entity draws to the screen. Until one does, the director leaves it
// **exactly** as it is today — FirstPersonCameraSystem and the editor's free camera drive it — and the
// screen view is made from it (a null Entity), so a renderer that reads only CameraViews still draws.
//   - A camera entity on the screen: its view is mirrored into ActiveCamera (position, rotation, field
//     of view, clip planes) and ActiveCamera.DrivenByRig is set, so the editor's free camera stands aside
//     as it does for a rig, and fixed-tick readers never see the free camera's pose in between.
//   - `cam_free` (ActiveCamera.RigEnabled false): the free camera wins the screen whatever the entities
//     say; the director writes nothing to ActiveCamera and clears DrivenByRig (only if a camera entity
//     had claimed the screen: with none, it is the legacy rig's to clear, as today).
[Experimental("SAGE0123")]
[System(Id, Phase.FrameUpdate, After = new[] { "?sage.character.camera" })]
public sealed class CameraDirector : ISystem
{
    public const string Id = "sage.camera.director";

    // How far from unit a rig's rotation may be before it is taken for "not written" (a zeroed pose).
    private const float MinRotationLengthSquared = 1e-6f;

    private readonly World _world;
    private readonly CameraViews _views;
    private readonly Query<Camera, CameraPose> _rigged;
    private readonly Query<Camera, GlobalTransform> _placed;
    private ActiveCamera? _active;

    public CameraDirector(World world)
    {
        _world = world;
        _views = world.Resources.GetOrAdd(() => new CameraViews());
        _rigged = world.Query<Camera, CameraPose>();
        _placed = world.Query<Camera, GlobalTransform>().WithoutComponent<CameraPose>();
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

        int screen = _views.IndexOf("");
        var active = _active;
        if (active != null)
        {
#pragma warning disable CS0618   // ActiveCamera's rig flags: obsolete for games, still the protocol with cam_free until #78/#81
            if (!active.RigEnabled)
            {
                // cam_free: the editor camera flies, over whatever camera had the screen.
                if (screen >= 0) active.DrivenByRig = false;
                _views.SetScreen(FromActiveCamera(active));
            }
            else if (screen >= 0)
            {
                ref readonly var main = ref _views[screen];
                active.Position = main.Position;
                active.Rotation = main.Rotation;
                active.FovY = main.FovY;
                active.Near = main.Near;
                active.Far = main.Far;
                active.DrivenByRig = true;
            }
            else
            {
                _views.SetScreen(FromActiveCamera(active));   // no camera entity: ActiveCamera as today
            }
#pragma warning restore CS0618
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
        };
    }

    private static CameraView FromActiveCamera(ActiveCamera active) => new()
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
