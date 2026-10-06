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
//   Extract  fits a map to **every view that receives shadows** (issue 4n-19; `ShadowViews`: the main view
//            first, then split-screen partners, monitors and the views of worlds drawing only into render
//            targets, up to `r_shadow_views`, unless a camera says `noShadows`), each in its own target, and
//            adds one **caster view** per cascade of each: the sun's orthographic view, camera-relative to
//            its receiver's camera, flagged `ShadowCaster`. The sun's direction is quantised first
//            (`r_shadow_sun_step`), so the grids hold still while it moves. The mesh, skinned and sprite
//            extracts put into each only what `ShadowMath.Casts` (opaque and alpha-tested materials with
//            `castShadows`), culled against its box, which reaches toward the sun; particles, decals,
//            lights and debug lines skip them, and the viewmodel never reaches them. Nothing is added with
//            `r_shadows 0`, with no view that receives, or when the strength is 0 (the sun is down).
//   Draw     renders those items and sprites into `sage:shadow` (the main view's; `sage:shadow:1` and on
//            for the others), and each view's lit draws read its own (Renderer.DrawShadowCasters).
//
// Strength is `RenderEnvironment.ShadowStrength` — the sky's `shadow` key, softened by weather — and
// nothing once the sun is below the horizon (`ShadowMath.Strength`). A world with no sky keeps the
// environment's default, 1: full shadows under the default sun.
[RenderPass(Id, RenderStage.Shadow)]
internal sealed class ShadowPass : IRenderPass
{
    public const string Id = "sage:shadow";
    public const string Target = "sage:shadow";   // the main view's map; the others' are `sage:shadow:1` and on (Targets[k])

    // The targets (made once: a name is a string). The main view's is always `Targets[0]` and the others'
    // `Targets[1..]` in order, so each name keeps one size from frame to frame and from world to world (a
    // world drawing only into render targets has no main view), and no target is remade.
    private static readonly string[] Targets = MakeTargets();

    private readonly RendererCVars _cvars;
    private readonly ShadowFit[] _fits = new ShadowFit[ShadowMath.MaxCascades];
    private readonly float[] _splits = new float[ShadowMath.MaxCascades];
    private bool[] _receives = new bool[4];
    private readonly int[] _chosen = new int[ShadowViews.MaxViews];

    public ShadowPass(RendererCVars cvars) => _cvars = cvars;

    private static string[] MakeTargets()
    {
        var names = new string[ShadowViews.MaxViews + 1];   // the main view's, and one per other view
        names[0] = Target;
        for (int i = 1; i < names.Length; i++) names[i] = Target + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return names;
    }

    public void Extract(RenderContext context)
    {
        if (!_cvars.Shadows.Value) return;
        var s = context.Snapshot;
        int views = s.Views.Count;
        if (views == 0 || s.Environment.ShadowStrength <= 0f) return;

        // Which views get a map (ShadowViews, headless and tested): the main view first, then the rest.
        float distance = _cvars.ShadowDistance.Value;
        if (_receives.Length < views) _receives = new bool[Math.Max(views, _receives.Length * 2)];
        for (int v = 0; v < views; v++)
        {
            ref readonly var view = ref s.Views[v];
            _receives[v] = ShadowViews.Receives(view.ShadowCaster, view.DepthOnly, view.NoShadows, view.Near, view.Far, distance);
        }
        int count = ShadowViews.Choose(_receives.AsSpan(0, views), s.MainView, _cvars.ShadowViews.Value, _chosen);

        var origin = context.World.Resources.TryGet<Origin>(out var o) && o != null ? o.ToAbsolute(System.Numerics.Vector3.Zero) : default;
        var sun = ShadowMath.QuantiseSun(s.Environment.SunDirection.ToNumerics(), _cvars.ShadowSunStep.Value);
        int secondary = 0;
        for (int r = 0; r < count; r++)
            AddMap(context, s, _chosen[r], _chosen[r] == s.MainView ? 0 : Math.Min(++secondary, Targets.Length - 1), distance, sun, origin);
    }

    // One receiver's map: its cascades fitted to its camera, a caster view each, in target `Targets[name]`.
    private void AddMap(RenderContext context, RenderSnapshot s, int receiver, int name, float distance,
                        System.Numerics.Vector3 sun, System.Numerics.Vector3 origin)
    {
        var main = s.Views[receiver];   // a copy: Add below may move the list
        int size = ShadowViews.CascadeSize(receiver == s.MainView, _cvars.ShadowSize.Value, _cvars.ShadowViewSize.Value);
        var atlas = ShadowMath.Atlas(_cvars.ShadowCascades.Value, size);
        int count = atlas.Cascades;
        size = atlas.CascadeSize;
        var fits = _fits.AsSpan(0, count);
        var splits = _splits.AsSpan(0, count);
        var projection = main.Projection.ToNumerics();
        if (!ShadowViews.Fit(main.CameraPosition.ToNumerics(), main.Forward.ToNumerics(), projection, main.Near, main.Far, distance,
                             sun, size, origin, fits, splits)) return;

        var renderer = context.Renderer;
        int target = renderer.DeclareTargetId(Targets[name], atlas.Width, atlas.Height, SurfaceFormat.Single, DepthFormat.Depth24);
        int first = s.Views.Count;
        s.EnsureViewSlots(first + count);
        var frame = new ShadowFrame
        {
            Receiver = receiver, View = first, Count = count, Target = target, Camera = main.CameraPosition, Size = size, Drawn = false,
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
            view.Source = main.Source;
            view.NoShadows = view.NoViewmodel = view.NoSky = view.NoDebugLines = false;
            view.Shadow = -1;
            view.Receiver = receiver;
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
        s.Views[receiver].Shadow = s.Shadows.Count;
        s.Shadows.Add() = frame;
    }

    public void Draw(RenderContext context) => context.Renderer.DrawShadowCasters(context);
}
