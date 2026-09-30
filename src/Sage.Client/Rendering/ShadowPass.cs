#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Sun shadows (issue 4h-4, decision 1): the engine's first Shadow-stage pass. One stable map, R32F,
// `r_shadow_size` texels a side, over the nearest `r_shadow_distance` metres of the screen's main view;
// the lit shaders read it with a 2x2 PCF (common.fxh `ShadowLit`). Where the map sits is `ShadowMath.Fit`,
// headless and tested; this only turns its answer into a view and draws it.
//
//   Extract  adds the **caster view**: the sun's orthographic view, camera-relative to the main view's
//            camera, flagged `ShadowCaster`. The mesh and skinned extracts put into it only what
//            `ShadowMath.Casts` (opaque materials with `castShadows`), culled against its box, which
//            reaches toward the sun; sprites, particles, lights and debug lines skip it, and the
//            viewmodel never reaches it. Nothing is added with `r_shadows 0`, in a world that does not
//            draw to the screen, with no main view, or when the strength is 0 (the sun is down).
//   Draw     renders those items into `sage:shadow` (Renderer.DrawShadowCasters).
//
// Strength is `RenderEnvironment.ShadowStrength` — the sky's `shadow` key, softened by weather — and
// nothing once the sun is below the horizon (`ShadowMath.Strength`). A world with no sky keeps the
// environment's default, 1: full shadows under the default sun.
[RenderPass(Id, RenderStage.Shadow)]
internal sealed class ShadowPass : IRenderPass
{
    public const string Id = "sage:shadow";
    public const string Target = "sage:shadow";   // the map's render target

    private readonly RendererCVars _cvars;

    public ShadowPass(RendererCVars cvars) => _cvars = cvars;

    public void Extract(RenderContext context)
    {
        if (!_cvars.Shadows.Value || !context.IsScreen) return;
        var s = context.Snapshot;
        if (s.MainView < 0 || s.Environment.ShadowStrength <= 0f) return;

        var main = s.Views[s.MainView];   // a copy: Add below may move the list
        if (main.DepthOnly) return;
        float distance = MathF.Min(main.Far, _cvars.ShadowDistance.Value);
        if (distance <= main.Near) return;

        // The lens, from the projection: an orthographic one has M44 = 1.
        var p = main.Projection;
        var (centre, radius) = p.M44 == 1f
            ? ShadowMath.OrthographicSlice(main.Near, distance, 1f / p.M11, 1f / p.M22)
            : ShadowMath.PerspectiveSlice(main.Near, distance, 1f / p.M11, 1f / p.M22);
        var origin = context.World.Resources.TryGet<Origin>(out var o) && o != null ? o.ToAbsolute(System.Numerics.Vector3.Zero) : default;
        int size = _cvars.ShadowSize.Value;
        var fit = ShadowMath.Fit(main.CameraPosition.ToNumerics(), main.Forward.ToNumerics(), centre, radius,
                                 s.Environment.SunDirection.ToNumerics(), size, origin);

        var renderer = context.Renderer;
        int target = renderer.DeclareTargetId(Target, size, size, SurfaceFormat.Single, DepthFormat.Depth24);
        int index = s.Views.Count;
        s.EnsureViewSlots(index + 1);
        ref var view = ref s.Views.Add();
        view.View = fit.View;              // System.Numerics → MonoGame (implicit)
        view.Projection = fit.Projection;
        view.ViewProj = fit.ViewProj;
        view.CameraPosition = main.CameraPosition;
        view.Forward = s.Environment.SunDirection;
        view.Near = 0f;
        view.Far = fit.DepthRange;
        view.Target = target;
        view.Viewport = new Rectangle(0, 0, size, size);
        view.FullTarget = true;
        view.Order = 0;
        view.Hidden = 0;                   // a first-person body still casts
        view.DepthOnly = false;
        view.ShadowCaster = true;
        view.LightStart = view.LightCount = 0;
        view.DebugStart = view.DebugCount = 0;
        view.Culled = 0;
        view.ItemStart = view.ItemCount = 0;
        view.SpriteStart = view.SpriteCount = 0;

        // Culled against the map's box, which reaches toward the sun (never frozen by r_freezecull).
        s.Frustum(index).Matrix = view.ViewProj;
        s.CullOrigins[index] = view.CameraPosition;
        s.CullValid[index] = true;

        s.Shadow = new ShadowFrame
        {
            View = index, Target = target, ViewProj = view.ViewProj, Camera = main.CameraPosition,
            Bias = fit.DepthBias, Size = size, Drawn = false,
        };
    }

    public void Draw(RenderContext context) => context.Renderer.DrawShadowCasters(context);
}
