#nullable enable
using System;
using System.Runtime.CompilerServices;
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
// Lamps (issue #315): with `r_shadows 1`, day or night, the `r_shadow_lamps` nearest lamps that say `shadows`
// (LampShadows.Choose) get a map each in `sage:shadow:lamps` — a spot one perspective view, a point light six —
// through caster views like the cascades', which hide the lamp's own entity. LightExtract marks their samples
// (LightSample.ShadowSlot) and the lit draws read the atlas (common.fxh `LampShadow`). No lamp that asks: no
// target, no view, nothing drawn.
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
        if (views == 0) return;
        ExtractSun(context, s, views);
        AddLamps(context, s);   // after the sun's: lamps shadow at night too
    }

    private void ExtractSun(RenderContext context, RenderSnapshot s, int views)
    {
        if (s.Environment.ShadowStrength <= 0f) return;

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

    // ---- Lamp shadows (issue #315) ----

    private const string LampTarget = "sage:shadow:lamps";

    // Each world's lamps, queried once (a query allocates when it is made).
    private readonly ConditionalWeakTable<World, LampQuery> _lampQueries = new();
    private sealed class LampQuery(World world)
    {
        public readonly Query<GlobalTransform, PointLight> Lights = world.Query<GlobalTransform, PointLight>();
    }

    private LightSample[] _lampCandidates = new LightSample[16];
    private Entity[] _lampEntities = new Entity[16];
    private readonly int[] _lampChosen = new int[LampShadows.MaxLamps];

    // The lamps that get a map (LampShadows.Choose, from the receiving camera: the screen's main view, else
    // the first real view), each fitted (LampShadows.Fit) with a caster view per face, all into one atlas.
    // Nothing at all — no target, no view — when no lamp asks, or `r_shadow_lamps 0`.
    private void AddLamps(RenderContext context, RenderSnapshot s)
    {
        int budget = _cvars.ShadowLamps.Value;
        if (budget <= 0) return;
        int receiver = s.LodView();
        if (receiver < 0 || s.Views[receiver].NoShadows) return;
        var camera = s.Views[receiver].CameraPosition;

        // The lamps that ask, camera-relative, as LightExtract samples them (unflickered: a flicker's dark
        // step must not take the map away and give it back).
        var query = _lampQueries.GetValue(context.World, static w => new LampQuery(w)).Lights;
        int count = 0;
        foreach (var (globals, lights, entities) in query.Chunks)
            for (int n = 0; n < globals.Length; n++)
            {
                ref readonly var light = ref lights[n];
                if (!light.Shadows || !light.Lit) continue;
                var pose = globals[n].Interpolated(context.Alpha);
                var at = pose.Position - camera.ToNumerics();
                var spot = light.Cone > 0f ? LightRules.Spot(System.Numerics.Vector3.Transform(TransformMath.Forward, pose.Rotation), light.Cone, light.InnerCone)
                                           : System.Numerics.Vector4.Zero;
                if (count == _lampCandidates.Length)
                {
                    Array.Resize(ref _lampCandidates, count * 2);
                    Array.Resize(ref _lampEntities, count * 2);
                }
                _lampCandidates[count] = new LightSample(at, light.Colour * light.Intensity, light.Range) { Spot = spot, CastsShadows = true };
                _lampEntities[count++] = entities.EntityAt(n);
            }
        int chosen = LampShadows.Choose(_lampCandidates.AsSpan(0, count), budget, _lampChosen);
        if (chosen == 0) return;

        var frame = s.LampMaps;
        var atlas = LampShadows.Atlas(budget, _cvars.ShadowLampSize.Value);
        var renderer = context.Renderer;
        frame.Atlas = atlas;
        frame.Target = renderer.DeclareTargetId(LampTarget, atlas.Width, atlas.Height, SurfaceFormat.Single, DepthFormat.Depth24);
        frame.Camera = camera;
        frame.View = s.Views.Count;
        frame.Count = chosen;
        var main = s.Views[receiver];   // a copy: Add below may move the list
        for (int k = 0; k < chosen; k++)
        {
            int index = _lampChosen[k];
            frame.Lamps[k] = _lampEntities[index];
            ref var fit = ref frame.Fits[k];
            fit = LampShadows.Fit(_lampCandidates[index], k, atlas);
            s.EnsureViewSlots(s.Views.Count + fit.Faces);
            for (int f = 0; f < fit.Faces; f++)
            {
                ref readonly var face = ref fit.Views[f];
                int v = s.Views.Count;
                ref var view = ref s.Views.Add();
                view.View = face.View;              // System.Numerics → MonoGame (implicit)
                view.Projection = face.Projection;
                view.ViewProj = face.ViewProj;
                view.CameraPosition = camera;
                view.Forward = face.Forward;        // sprites turn as they would to a sun that way
                view.Near = fit.Near;
                view.Far = fit.Far;
                view.Target = frame.Target;
                view.Viewport = new Rectangle(face.X, face.Y, atlas.TileSize, atlas.TileSize);
                view.FullTarget = false;
                view.Order = 0;
                view.Hidden = frame.Lamps[k].Id;   // a lamp's own mesh (its glass, its torch) does not hide it
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

                s.Frustum(v).Matrix = view.ViewProj;
                s.CullOrigins[v] = camera;
                s.CullValid[v] = true;
            }
        }
        frame.ViewCount = s.Views.Count - frame.View;
    }
}
