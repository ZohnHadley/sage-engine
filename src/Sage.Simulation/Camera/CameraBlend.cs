#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Camera blends (issue #90): `CameraOn` with a blend time eases the screen from the view it had to the
// camera it turns on, instead of cutting (CameraIO). What was deferred from 4a (#80) to 4b's tweens.
//
// **The shape.** A `CameraBlend` on the camera entity holds the pose it blends *from* (the main view when
// the blend began: whatever drew the screen, a rig, another scripted camera, ActiveCamera), how long and
// along which curve. The camera's own pose — its CameraPose when a rig drives it, else its interpolated
// transform — is where it blends *to*, so a blend into a moving camera lands on it. The director applies
// it to the camera's resolved view (position, rotation, and the field of view when both are
// perspective) after choosing the views and before mirroring the screen into ActiveCamera, so every
// reader sees the blended view.
//
// **Deterministic.** `Elapsed` moves in fixed ticks (CameraBlendSystem, EntityIO phase before the
// dispatch, like timers and tweens) and is saved; a frame interpolates between the last two ticks'
// values with the frame's alpha, so the blend is smooth at any display rate and the same in every run.
// A finished blend stays on the entity, inert, until the next CameraOn replaces or ends it — no
// structural change in the middle of a frame.
[Experimental("SAGE0123")]
[Component("sage:camera_blend")]
public struct CameraBlend : IComponent
{
    public Vector3 FromPosition;            // origin space; moved by rebasing
    public Quaternion FromRotation;
    public float FromFovY;                  // radians, as CameraView's; 0 = do not blend the field of view
    public float Duration;                  // seconds
    public float Elapsed;                   // seconds, as of the last tick
    public float PreviousElapsed;           // as of the tick before, for the frame's interpolation
    public Ease Ease;

    // Still changing the view: false once a tick has passed with it complete.
    public readonly bool Active => PreviousElapsed < Duration;

    // How far along, 0..1 after the curve, `alpha` of the way from the previous tick to the last.
    public readonly float Amount(float alpha)
    {
        if (!(Duration > 0f)) return 1f;
        float elapsed = PreviousElapsed + (Elapsed - PreviousElapsed) * Math.Clamp(alpha, 0f, 1f);
        return Easing.Apply(Ease, elapsed / Duration);
    }
}

[Experimental("SAGE0123")]
public static class CameraBlends
{
    // Blends `camera` in from `from` over `seconds` (see CameraBlend). CameraOn does this; so may a game.
    public static void Begin(World world, Entity camera, in CameraView from, float seconds, Ease ease)
    {
        var blend = new CameraBlend
        {
            FromPosition = from.Position,
            FromRotation = from.Rotation,
            FromFovY = from.Projection == CameraProjection.Perspective ? from.FovY : 0f,
            Duration = seconds > 0f && float.IsFinite(seconds) ? seconds : 0f,
            Ease = ease,
        };
        if (world.Has<CameraBlend>(camera)) world.Get<CameraBlend>(camera) = blend;
        else world.Add(camera, blend);
    }

    // Any blend on `camera` over: its next frame is its own view (a cut).
    public static void End(World world, Entity camera)
    {
        if (!world.Has<CameraBlend>(camera)) return;
        ref var blend = ref world.Get<CameraBlend>(camera);
        blend.Elapsed = blend.PreviousElapsed = blend.Duration;
    }

    // The director's step: the view `blend` makes of `view` this frame.
    internal static void Apply(in CameraBlend blend, ref CameraView view, float alpha)
    {
        float t = blend.Amount(alpha);
        view.Position = Vector3.Lerp(blend.FromPosition, view.Position, t);
        view.Rotation = Quaternion.Normalize(Quaternion.Slerp(blend.FromRotation, view.Rotation, t));
        if (blend.FromFovY > 0f && view.Projection == CameraProjection.Perspective)
            view.FovY = Math.Clamp(blend.FromFovY + (view.FovY - blend.FromFovY) * t, 1e-3f, CameraMath.MaxFovY);
    }
}

// EntityIO phase, before the dispatch: every camera blend one tick further along.
[Experimental("SAGE0123")]
[System(Id, Phase.EntityIO, Before = new[] { "?sage.io.dispatch" })]
internal sealed class CameraBlendSystem : ISystem
{
    public const string Id = "sage.camera.blend";

    private readonly Query<CameraBlend> _blends;

    public CameraBlendSystem(World world) => _blends = world.Query<CameraBlend>();

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;
        foreach (var (blends, _) in _blends.Chunks)
        {
            var b = blends.Span;
            for (int i = 0; i < b.Length; i++)
            {
                b[i].PreviousElapsed = b[i].Elapsed;
                if (!(b[i].Elapsed < b[i].Duration)) continue;
                // Complete on the tick its seconds are up, not one later for float dust (Timers.Epsilon).
                float next = b[i].Elapsed + dt;
                b[i].Elapsed = next >= b[i].Duration - Timers.Epsilon ? b[i].Duration : next;
            }
        }
    }
}
