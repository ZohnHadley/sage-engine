#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Water (issue #411): the renderer's half. Which surfaces the frame draws and whether the camera is under
// one are decided headless (WaterViews, Sage.Simulation) and extracted here; the chain's water step
// (PostStepKind.Water, the material `sage:post_water`, shaders/water.fx) draws them.
//
// **A post step, not geometry.** The surface is drawn after the views, over `sage:scene`, with this
// frame's `sage:depth` (the depth hook of issue #316), which a surface drawn during the views could not
// read: the depth target is drawn after them. Per pixel the step casts the main view's ray, meets the
// nearest surface's plane in front of what the scene drew there, and shades it: ripples (four travelling
// sine waves), the bottom seen through it bent by them and fogged by how much water the ray crosses, the
// sky (and, by a short screen-space march along the reflected ray, the shore) reflected by Fresnel, the
// sun's glint, and a fade where the bottom comes up to the waterline. With the camera under a volume the
// whole view is tinted and fogged by the water instead, and its surface seen from below shows what is
// above it, bent. The cost: one full-screen pass and the depth pass, only while water is in view.
//
// **The reflection is the sky plus screen space**, not a mirrored second camera: the shore, trees and
// huts on screen are found by marching the reflected ray through the depth buffer; what is off screen,
// or hidden behind something nearer, reflects the sky's own colour (sky.fx's gradient and haze). Cheap,
// and right for the case the issue names (a lake reflecting the sky and the shore); what it cannot show
// is a reflection of something the camera does not see.
//
// **What the depth does not hold, the water covers.** The depth hook draws opaque and alpha-tested items
// only: a sprite or a blended particle in front of the water is drawn over by it, and one behind the
// surface is seen through it like the bottom. The views draw into `sage:scene` and the step runs after
// the tonemap, on the LDR picture.
public sealed partial class Renderer
{
    private CVar<bool> _waterOn = null!;
    private bool _waterWanted;   // this frame has water to draw: set by Draw before the chain is planned

    // Scratch for the step's arrays: set every frame, allocated once.
    private readonly Vector4[] _waterRect = new Vector4[WaterViews.MaxSurfaces];
    private readonly Vector4[] _waterLevel = new Vector4[WaterViews.MaxSurfaces];
    private readonly Vector4[] _waterColour = new Vector4[WaterViews.MaxSurfaces];
    private readonly Vector4[] _waterWaves = new Vector4[WaterViews.MaxSurfaces];
    private readonly Vector4[] _waterMore = new Vector4[WaterViews.MaxSurfaces];

    // Whether the frame has water for the chain to draw: `r_water`, a main view, and a surface in sight or
    // the camera under one.
    private bool WantsWater(RenderSnapshot s) =>
        _waterOn.Value && s.MainView >= 0 && (s.Water.Count > 0 || s.Water.Under);

    // The water step's parameters (water.fx): the main view, the surfaces relative to its camera, the sky
    // they reflect and the underwater view. With no main view the step draws no water (WaterCount 0).
    private void SetWaterParams(Effect effect, RenderSnapshot s)
    {
        var p = effect.Parameters;
        var water = s.Water;
        if (s.MainView < 0)
        {
            p["WaterCount"]?.SetValue(0f);
            p["WaterUnderTint"]?.SetValue(Vector4.Zero);
            return;
        }
        ref var main = ref s.Views[s.MainView];
        var camera = main.CameraPosition;
        ref var env = ref s.Environment;

        p["WaterInvViewProj"]?.SetValue(Matrix.Invert(main.ViewProj));
        p["WaterViewProj"]?.SetValue(main.ViewProj);
        p["WaterCamera"]?.SetValue(new Vector4(camera, env.Time));
        var rect = ViewRect(main);
        float w = Math.Max(1, _sceneSize.X), h = Math.Max(1, _sceneSize.Y);
        p["WaterViewport"]?.SetValue(new Vector4(rect.X / w, rect.Y / h, rect.Right / w, rect.Bottom / h));

        var light = WaterShading.Light(env.AmbientSky.ToNumerics(), env.SunColor.ToNumerics(), env.SunDirection.ToNumerics());
        int count = Math.Min(water.Count, WaterViews.MaxSurfaces);
        for (int i = 0; i < WaterViews.MaxSurfaces; i++)
        {
            if (i >= count) { _waterRect[i] = _waterLevel[i] = _waterColour[i] = _waterWaves[i] = _waterMore[i] = Vector4.Zero; continue; }
            ref readonly var plane = ref water.Planes[i];
            var look = plane.Look;
            var dir = WaterShading.Direction(look);
            _waterRect[i] = new Vector4(plane.Min.X - camera.X, plane.Min.Y - camera.Z, plane.Max.X - camera.X, plane.Max.Y - camera.Z);
            _waterLevel[i] = new Vector4(plane.Surface - camera.Y, plane.Floor - camera.Y, look.FogDensity, look.ShoreFade);
            _waterColour[i] = new Vector4(look.Colour.X, look.Colour.Y, look.Colour.Z, look.Reflectivity);
            _waterWaves[i] = new Vector4(dir.X, dir.Y, MathF.Max(look.WaveLength, 0.05f), look.WaveStrength);
            _waterMore[i] = new Vector4(look.WaveSpeed, look.Refraction, look.Specular, 0f);
        }
        p["WaterCount"]?.SetValue((float)count);
        p["WaterRect"]?.SetValue(_waterRect);
        p["WaterLevel"]?.SetValue(_waterLevel);
        p["WaterColour"]?.SetValue(_waterColour);
        p["WaterWaves"]?.SetValue(_waterWaves);
        p["WaterMore"]?.SetValue(_waterMore);

        // The sky the surface reflects: sky.fx's gradient and haze (no sun disc: the glint is the water's own).
        p["WaterHorizon"]?.SetValue(env.ClearColor);
        p["WaterZenith"]?.SetValue(env.DrawSky ? env.Zenith : env.ClearColor);
        p["WaterFog"]?.SetValue(env.FogColor);
        p["WaterSunDir"]?.SetValue(env.SunDirection);
        p["WaterSunColour"]?.SetValue(env.SunColor);
        p["WaterSky"]?.SetValue(new Vector4(env.DrawSky ? env.FogParams.Z : 0f, SkyRules.HazeBand, 0f, 0f));
        p["WaterLight"]?.SetValue(new Vector3(light.X, light.Y, light.Z));

        if (water.Under)
        {
            var look = water.UnderPlane.Look;
            var colour = look.UnderwaterColour * light;
            p["WaterUnder"]?.SetValue(new Vector4(colour.X, colour.Y, colour.Z, look.UnderwaterDensity));
            p["WaterUnderTint"]?.SetValue(new Vector4(look.UnderwaterTint.X, look.UnderwaterTint.Y, look.UnderwaterTint.Z, 1f));
        }
        else
        {
            p["WaterUnder"]?.SetValue(Vector4.Zero);
            p["WaterUnderTint"]?.SetValue(Vector4.Zero);
        }
    }
}

// The water the main view sees this frame (issue #411): the nearest surfaces in its frustum, and the
// volume its camera is under, if any. Written by WaterExtract, read by the chain's water step.
internal sealed class WaterFrame
{
    public readonly WaterPlane[] Planes = new WaterPlane[WaterViews.MaxSurfaces];
    public int Count;
    public bool Under;
    public WaterPlane UnderPlane;

    public void Clear()
    {
        Array.Clear(Planes);   // drop the look references
        Count = 0;
        Under = false;
        UnderPlane = default;
    }
}

// Water into the snapshot (issue #411): the main view's surfaces and whether its camera is under water,
// asked of WaterViews (headless). A surface whose top lies outside the view's frustum is left out, so a
// lake behind the camera costs no post step.
[System("sage.client.extract.water", Phase.Extract, After = new[] { "sage.client.extract.camera" })]
internal sealed class WaterExtract : ISystem
{
    private readonly RenderSnapshot _snapshot;
    private readonly WaterViews _water;
    private readonly WaterPlane[] _found = new WaterPlane[WaterViews.MaxSurfaces * 2];
    private readonly BoundingFrustum _frustum = new(Matrix.Identity);

    public WaterExtract(World world, RecordStore records)
    {
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _water = new WaterViews(world, records);
    }

    public void Run(in SystemContext ctx)
    {
        var s = _snapshot;
        if (s.MainView < 0 || !_water.Any) return;
        ref var main = ref s.Views[s.MainView];
        var camera = main.CameraPosition.ToNumerics();
        var frame = s.Water;

        frame.Under = _water.Under(camera, out frame.UnderPlane);
        int found = _water.Collect(camera, main.Far, _found);
        _frustum.Matrix = main.ViewProj;
        for (int i = 0; i < found && frame.Count < WaterViews.MaxSurfaces; i++)
        {
            ref readonly var plane = ref _found[i];
            // The top face, camera-relative, as a thin box: in the frustum or not.
            var box = new BoundingBox(
                new Vector3(plane.Min.X - camera.X, plane.Surface - camera.Y - 0.5f, plane.Min.Y - camera.Z),
                new Vector3(plane.Max.X - camera.X, plane.Surface - camera.Y + 0.5f, plane.Max.Y - camera.Z));
            if (_frustum.Contains(box) == ContainmentType.Disjoint) continue;
            frame.Planes[frame.Count++] = plane;
        }
    }
}
