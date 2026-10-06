#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Sun shadows (issue 4h-4, decision 1; cascades issue 4n-11): the engine's first Shadow-stage pass. Up to
// three stable cascades (`r_shadow_cascades`), R32F, `r_shadow_size` texels a side each, side by side in one
// target, over the nearest `r_shadow_distance` metres of the screen's main view; the lit shaders pick a
// cascade and read it with a 2x2 PCF (common.fxh `ShadowLit`). Where each map sits is
// `ShadowMath.FitCascades`, headless and tested; this only turns its answer into views and draws them.
//
//   Extract  adds one **caster view** per cascade: the sun's orthographic view, camera-relative to the
//            main view's camera, flagged `ShadowCaster`. The sun's direction is quantised first
//            (`r_shadow_sun_step`), so the grids hold still while it moves. The mesh, skinned and sprite
//            extracts put into each only what `ShadowMath.Casts` (opaque and alpha-tested materials with
//            `castShadows`), culled against its box, which reaches toward the sun; particles, decals,
//            lights and debug lines skip them, and the viewmodel never reaches them. Nothing is added with
//            `r_shadows 0`, in a world that does not draw to the screen, with no main view, or when the
//            strength is 0 (the sun is down).
//   Draw     renders those items and sprites into `sage:shadow` (Renderer.DrawShadowCasters).
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
    private readonly ShadowFit[] _fits = new ShadowFit[ShadowMath.MaxCascades];
    private readonly float[] _splits = new float[ShadowMath.MaxCascades];

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

        var atlas = ShadowMath.Atlas(_cvars.ShadowCascades.Value, _cvars.ShadowSize.Value);
        int count = atlas.Cascades, size = atlas.CascadeSize;
        var fits = _fits.AsSpan(0, count);
        var splits = _splits.AsSpan(0, count);

        // The lens, from the projection: an orthographic one has M44 = 1.
        var p = main.Projection;
        var origin = context.World.Resources.TryGet<Origin>(out var o) && o != null ? o.ToAbsolute(System.Numerics.Vector3.Zero) : default;
        var sun = ShadowMath.QuantiseSun(s.Environment.SunDirection.ToNumerics(), _cvars.ShadowSunStep.Value);
        ShadowMath.FitCascades(main.CameraPosition.ToNumerics(), main.Forward.ToNumerics(), p.M44 == 1f, 1f / p.M11, 1f / p.M22,
                               main.Near, distance, sun, size, origin, fits, splits);

        var renderer = context.Renderer;
        int target = renderer.DeclareTargetId(Target, atlas.Width, atlas.Height, SurfaceFormat.Single, DepthFormat.Depth24);
        int first = s.Views.Count;
        s.EnsureViewSlots(first + count);
        var frame = new ShadowFrame
        {
            View = first, Count = count, Target = target, Camera = main.CameraPosition, Size = size, Drawn = false,
        };
        for (int k = 0; k < count; k++)
        {
            ref readonly var fit = ref fits[k];
            var (x, y) = atlas.Corner(k);
            int index = first + k;
            ref var view = ref s.Views.Add();
            view.View = fit.View;              // System.Numerics → MonoGame (implicit)
            view.Projection = fit.Projection;
            view.ViewProj = fit.ViewProj;
            view.CameraPosition = main.CameraPosition;
            view.Forward = sun;                // the quantised sun: sprites turn to it
            view.Near = 0f;
            view.Far = fit.DepthRange;
            view.Target = target;
            view.Viewport = new Rectangle(x, y, size, size);
            view.FullTarget = count == 1;
            view.Order = 0;
            view.Hidden = 0;                   // a first-person body still casts
            view.DepthOnly = false;
            view.ShadowCaster = true;
            view.LightStart = view.LightCount = view.DynamicLightCount = 0;
            view.DebugStart = view.DebugCount = 0;
            view.Culled = 0;
            view.ItemStart = view.ItemCount = 0;
            view.SpriteStart = view.SpriteCount = 0;

            // Culled against the cascade's box, which reaches toward the sun (never frozen by r_freezecull).
            s.Frustum(index).Matrix = view.ViewProj;
            s.CullOrigins[index] = view.CameraPosition;
            s.CullValid[index] = true;

            ref var cascade = ref frame.Cascades[k];
            cascade.ViewProj = view.ViewProj;
            cascade.Bias = fit.DepthBias;
            cascade.Rect = new Vector4((float)x / atlas.Width, (float)y / atlas.Height,
                                       (float)size / atlas.Width, (float)size / atlas.Height);
        }
        s.Shadow = frame;
    }

    public void Draw(RenderContext context) => context.Renderer.DrawShadowCasters(context);
}
