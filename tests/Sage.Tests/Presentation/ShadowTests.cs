#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Sun shadows (issue 4h-4, docs/design/06 "As built (sun shadows)"): where the shadow map sits, how its
// texels line up and what it keeps, decided headless by `ShadowMath`. The drawing (`sage:shadow`, the
// ShadowCaster techniques, the PCF in common.fxh) needs a GPU and is exercised by the smoke run.
public class ShadowTests
{
    public ShadowTests() { _ = TestEnv.UserRoot; }

    private const float FovY = 70f * MathF.PI / 180f;
    private const float Aspect = 16f / 9f;
    private const float Near = 0.1f;
    private const float Distance = 60f;   // the single map's reach (4h-4); cascades reach further (4n-11)
    private static readonly float TanY = MathF.Tan(FovY * 0.5f);
    private static readonly float TanX = TanY * Aspect;

    private static readonly Vector3 NoonSun = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.3f));
    private static readonly Vector3 DuskSun = Vector3.Normalize(new Vector3(-1f, -0.2f, 0.1f));

    private static ShadowFit Fit(Vector3 camera, Vector3 forward, Vector3 sun, Vector3 origin = default, int size = ShadowMath.DefaultSize)
    {
        var (centre, radius) = ShadowMath.PerspectiveSlice(Near, Distance, TanX, TanY);
        return ShadowMath.Fit(camera, forward, centre, radius, sun, size, origin);
    }

    // The eight corners of a perspective view's slice, camera-relative.
    private static IEnumerable<Vector3> SliceCorners(Vector3 forward, float near, float far)
    {
        forward = Vector3.Normalize(forward);
        var up0 = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var right = Vector3.Normalize(Vector3.Cross(forward, up0));
        var up = Vector3.Cross(right, forward);
        foreach (float d in new[] { near, far })
            foreach (float sx in new[] { -1f, 1f })
                foreach (float sy in new[] { -1f, 1f })
                    yield return forward * d + right * (sx * d * TanX) + up * (sy * d * TanY);
    }

    private static readonly Vector3[] Forwards =
    {
        Vector3.UnitX, -Vector3.UnitZ, Vector3.Normalize(new Vector3(0.3f, -0.9f, 0.2f)), Vector3.Normalize(new Vector3(-0.6f, 0.3f, 0.7f)),
        -NoonSun, NoonSun, -DuskSun, DuskSun,
    };

    private static float Fraction(float x) => x - MathF.Round(x);

    [Fact]
    public void TheFitContainsTheViewSlice()
    {
        var camera = new Vector3(37.3f, 12f, -81.6f);
        foreach (var sun in new[] { NoonSun, DuskSun, -Vector3.UnitY })
        {
            ShadowFit? first = null;
            foreach (var forward in Forwards)
            {
                var fit = Fit(camera, forward, sun);
                foreach (var corner in SliceCorners(forward, Near, Distance))
                    Assert.True(ShadowMath.Contains(fit, corner), $"sun {sun}, looking {forward}: corner {corner} is off the map ({ShadowMath.ToClip(fit, corner)})");

                // Turning the camera never changes the map's size: it is fitted to the slice's sphere.
                first ??= fit;
                Assert.Equal(first.Value.Radius, fit.Radius);
                Assert.Equal(first.Value.TexelSize, fit.TexelSize);
            }
        }

        // 60 m of a 70° view in a 2048 map: a few centimetres a texel.
        var noon = Fit(camera, Vector3.UnitX, NoonSun);
        Assert.InRange(noon.TexelSize, 0.02f, 0.1f);

        // An orthographic view's slice (a top-down map camera) fits too.
        var (centre, radius) = ShadowMath.OrthographicSlice(0.1f, 40f, 12f, 8f);
        var ortho = ShadowMath.Fit(camera, -Vector3.UnitY, centre, radius, NoonSun, 1024, default);
        foreach (float d in new[] { 0.1f, 40f })
            foreach (float x in new[] { -12f, 12f })
                foreach (float z in new[] { -8f, 8f })
                    Assert.True(ShadowMath.Contains(ortho, new Vector3(x, -d, z)));
    }

    [Fact]
    public void ASubTexelCameraMoveKeepsTheTexelGrid()
    {
        var camera = new Vector3(37.3f, 12f, -81.6f);
        var forward = Vector3.Normalize(new Vector3(0.7f, -0.2f, 0.6f));
        var a = Fit(camera, forward, NoonSun);

        // A point on the ground in origin space, and where it falls on the map before and after the move.
        var point = camera + new Vector3(10.37f, -12f, 21.91f);
        foreach (var step in new[] { new Vector3(0.3f, 0f, 0f), new Vector3(0f, 0.2f, 0.1f), new Vector3(-0.35f, 0.05f, 0.25f) })
        {
            var moved = camera + step * a.TexelSize;
            var b = Fit(moved, forward, NoonSun);

            // The corner sits on whole texels in absolute light space, before and after.
            Assert.Equal(0.0, Math.Abs(a.MinX / a.TexelSize - Math.Round(a.MinX / a.TexelSize)), 3);
            Assert.Equal(0.0, Math.Abs(b.MinY / b.TexelSize - Math.Round(b.MinY / b.TexelSize)), 3);

            // So the point is the same fraction of a texel into its texel: the grid did not slide under it
            // (which is what shimmer is). It may be a whole texel over, if the corner snapped by one.
            var ta = ShadowMath.TexelOf(a, point - camera);
            var tb = ShadowMath.TexelOf(b, point - moved);
            var shift = tb - ta;
            Assert.True(MathF.Abs(Fraction(shift.X)) < 2e-3f && MathF.Abs(Fraction(shift.Y)) < 2e-3f,
                        $"moving {step} of a texel slid the grid by {shift} texels");
            Assert.True(MathF.Abs(shift.X) <= 1.01f && MathF.Abs(shift.Y) <= 1.01f);
        }
    }

    [Fact]
    public void AnOriginRebaseKeepsTheTexelGrid()
    {
        // One absolute camera position, seen from two origin sectors: every number in memory differs by a
        // sector, and the map must land on the same texels of the world.
        var absolute = new Vector3(1500.4f, 20f, 700.2f);
        var point = absolute + new Vector3(-6.3f, -20f, 14.8f);
        var forward = Vector3.Normalize(new Vector3(-0.5f, -0.3f, 0.8f));

        var sectorA = new SectorCoord(0, 0);
        var sectorB = new SectorCoord(1, 0);
        var originA = sectorA.Origin(Terrain.SectorSize);
        var originB = sectorB.Origin(Terrain.SectorSize);
        var cameraA = absolute - originA;
        var cameraB = absolute - originB;
        Assert.NotEqual(cameraA, cameraB);

        var a = Fit(cameraA, forward, DuskSun, originA);
        var b = Fit(cameraB, forward, DuskSun, originB);

        Assert.Equal(a.MinX, b.MinX, 3);
        Assert.Equal(a.MinY, b.MinY, 3);
        var ta = ShadowMath.TexelOf(a, point - absolute);
        var tb = ShadowMath.TexelOf(b, point - absolute);
        Assert.True(Vector2.Distance(ta, tb) < 2e-3f, $"the rebase moved the point from texel {ta} to {tb}");

        // Without the origin added back, the grid would be anchored to the origin sector's corner and
        // would jump at the rebase (1024 m is not a whole number of these texels along the light's axes).
        var unanchored = Fit(cameraB, forward, DuskSun);
        var tu = ShadowMath.TexelOf(unanchored, point - absolute);
        Assert.True(MathF.Abs(Fraction(tu.X - ta.X)) > 1e-3f || MathF.Abs(Fraction(tu.Y - ta.Y)) > 1e-3f);
    }

    [Fact]
    public void CastersBetweenTheSunAndTheViewAreKept()
    {
        var camera = new Vector3(0f, 2f, 0f);
        foreach (var sun in new[] { NoonSun, DuskSun })
        {
            var forward = Vector3.Normalize(new Vector3(sun.X, 0f, sun.Z));   // looking away from the sun, down the shadows
            var fit = Fit(camera, forward, sun);
            var toSun = -sun;

            // A cliff well outside the view's sphere, toward the sun: it shades the slice, so it is drawn.
            Assert.True(ShadowMath.Contains(fit, fit.Center + toSun * (fit.Radius + 150f)));
            // So is a tree canopy over the slice.
            Assert.True(ShadowMath.Contains(fit, fit.Center + Vector3.UnitY * 20f));
            // Past the reach, no; behind the slice (away from the sun) or beside it, no: they shade nothing we see.
            Assert.False(ShadowMath.Contains(fit, fit.Center + toSun * (fit.Radius + ShadowMath.DefaultCasterReach + 10f)));
            Assert.False(ShadowMath.Contains(fit, fit.Center - toSun * (fit.Radius + 5f)));
            var side = Vector3.Normalize(Vector3.Cross(sun, Vector3.UnitY));
            Assert.False(ShadowMath.Contains(fit, fit.Center + side * (fit.Radius + 5f)));
        }
    }

    [Fact]
    public void ShadowStrengthIsZeroBelowTheHorizon()
    {
        var sky = new SkyRecord { Sunrise = 6f, Sunset = 18f };
        foreach (double hour in new[] { 18.0, 19.5, 21, 23, 0, 3, 5.5, 6.0 })
        {
            var (toSun, elevation) = SkyRules.SunAt(sky, hour);
            if (elevation > 0.001f) continue;   // sunrise and sunset sit on the horizon
            // Whatever the environment says — a world without a sky keeps 1 — a sun that is down casts nothing.
            Assert.Equal(0f, ShadowMath.Strength(1f, -toSun));
        }

        var (noon, _) = SkyRules.SunAt(sky, 12);
        Assert.Equal(1f, ShadowMath.Strength(1f, -noon));
        Assert.Equal(0.4f, ShadowMath.Strength(0.4f, -noon));   // a heavy sky's softer shadows
        Assert.Equal(1f, ShadowMath.Strength(3f, -noon));       // clamped
        Assert.Equal(0f, ShadowMath.Strength(1f, Vector3.UnitY));
        Assert.Equal(0f, ShadowMath.Strength(1f, Vector3.Zero));

        // A world with no sky: RenderEnvironment's defaults give full shadows.
        var environment = new RenderEnvironment();
        Assert.Equal(1f, ShadowMath.Strength(environment.ShadowStrength, environment.SunDirection));
    }

    [Fact]
    public void OnlyOpaqueMaterialsThatSaySoCastShadows()
    {
        Assert.True(ShadowMath.Casts(RenderPass.Opaque, castShadows: true));
        Assert.False(ShadowMath.Casts(RenderPass.Opaque, castShadows: false));
        Assert.False(ShadowMath.Casts(RenderPass.Transparent, castShadows: true));

        var fx = new MountFixture();
        fx.Write("engine", "data/materials.json", """
            [{ "type": "material", "id": "lit_default", "effect": "shaders/lit.mgfxo", "technique": "Default", "pass": "Opaque" }]
            """);
        fx.Write("game", "data/materials.json", """
            [{ "type": "material", "id": "rock", "base": "sage:lit_default" },
             { "type": "material", "id": "window", "base": "sage:lit_default", "castShadows": false }]
            """);
        fx.Mount("engine", "sage");
        fx.Mount("game", "sandbox");
        var store = new RecordStore();
        store.Register<MaterialRecord>();
        store.Load(fx.Vfs);

        Assert.Equal(0, store.ErrorCount);
        Assert.True(store.Get<MaterialRecord>(new RecordId("sage", "lit_default")).CastShadows);   // on by default
        Assert.True(store.Get<MaterialRecord>(new RecordId("sandbox", "rock")).CastShadows);
        Assert.False(store.Get<MaterialRecord>(new RecordId("sandbox", "window")).CastShadows);
    }

    // ---- Cascades, the quantised sun and cut-out casters (issue 4n-11) ----

    private static readonly Vector3 Camera = new(37.3f, 12f, -81.6f);

    private static (ShadowFit[] Fits, float[] Splits) Cascades(Vector3 camera, Vector3 forward, Vector3 sun, Vector3 origin = default,
                                                                 int count = ShadowMath.DefaultCascades, int size = 1024)
    {
        var fits = new ShadowFit[count];
        var splits = new float[count];
        ShadowMath.FitCascades(camera, forward, orthographic: false, TanX, TanY, Near, ShadowMath.DefaultDistance, sun, size, origin, fits, splits);
        return (fits, splits);
    }

    [Fact]
    public void CascadeSplitsLeanTowardTheCamera()
    {
        var splits = new float[3];
        ShadowMath.CascadeSplits(Near, 150f, splits);
        Assert.Equal(150f, splits[2]);
        Assert.True(splits[0] > Near && splits[0] < splits[1] && splits[1] < splits[2]);
        // Nearer than an even split: the ground at your feet gets the finest map.
        Assert.True(splits[0] < 50f && splits[1] < 100f, $"splits {string.Join(", ", splits)}");
        // Blend 0 is the even split, 1 the logarithmic one.
        ShadowMath.CascadeSplits(1f, 100f, splits, blend: 0f);
        Assert.Equal(new[] { 34f, 67f, 100f }, splits);
        ShadowMath.CascadeSplits(1f, 100f, splits, blend: 1f);
        Assert.Equal(100f, splits[2]);
        Assert.Equal(MathF.Pow(100f, 1f / 3f), splits[0], 3);

        // The atlas: one map alone, two side by side, three in a 2x2 square; never past 4096 a side.
        var one = ShadowMath.Atlas(1, 4096);
        Assert.Equal((4096, 4096), (one.Width, one.Height));
        var two = ShadowMath.Atlas(2, 2048);
        Assert.Equal((4096, 2048, (2048, 0)), (two.Width, two.Height, two.Corner(1)));
        var three = ShadowMath.Atlas(3, 4096);
        Assert.Equal((2048, 4096, 4096, (0, 2048)), (three.CascadeSize, three.Width, three.Height, three.Corner(2)));
        Assert.Equal(ShadowMath.MaxCascades, ShadowMath.Atlas(9, 512).Cascades);
    }

    [Fact]
    public void EachCascadeHoldsItsSliceAndTheNearerOnesAreFiner()
    {
        foreach (var sun in new[] { NoonSun, DuskSun })
            foreach (var forward in Forwards)
            {
                var (fits, splits) = Cascades(Camera, forward, sun);
                float from = Near;
                for (int k = 0; k < fits.Length; k++)
                {
                    // The cascade holds its slice with a texel to spare (what the shader asks), so a point
                    // of slice k is read from cascade k or a nearer, finer one — never from none.
                    foreach (var corner in SliceCorners(forward, from, splits[k]))
                    {
                        int chosen = ShadowMath.CascadeOf(fits, corner);
                        Assert.InRange(chosen, 0, k);
                        Assert.True(ShadowMath.Contains(fits[k], corner));
                    }
                    if (k > 0) Assert.True(fits[k].TexelSize > fits[k - 1].TexelSize);
                    from = splits[k];
                }

                // Along the view: near points from the first cascade; the far end from one of them (looking
                // into the sun, a nearer cascade's box reaches it: it is on that map, between the slice and
                // the sun); far past every map's reach, none.
                Assert.Equal(0, ShadowMath.CascadeOf(fits, forward * 1f));
                Assert.NotEqual(-1, ShadowMath.CascadeOf(fits, forward * (ShadowMath.DefaultDistance - 1f)));
                Assert.Equal(-1, ShadowMath.CascadeOf(fits, forward * 2000f));
            }

        // The nearest is finer than the single 60 m map was, and the last reaches 150 m.
        var (noon, _) = Cascades(Camera, Vector3.UnitX, NoonSun, size: ShadowMath.DefaultSize);
        Assert.True(noon[0].TexelSize < Fit(Camera, Vector3.UnitX, NoonSun).TexelSize);
    }

    [Fact]
    public void EveryCascadeKeepsItsTexelGrid()
    {
        var forward = Vector3.Normalize(new Vector3(0.7f, -0.2f, 0.6f));
        var (a, _) = Cascades(Camera, forward, NoonSun);
        for (int k = 0; k < a.Length; k++)
        {
            // A point in this cascade's slice.
            var point = Camera + forward * (k == 0 ? 3f : k == 1 ? 20f : 80f) + new Vector3(0.37f, -1.3f, 0.91f);
            foreach (var step in new[] { new Vector3(0.3f, 0f, 0f), new Vector3(0f, 0.2f, 0.1f), new Vector3(-0.35f, 0.05f, 0.25f) })
            {
                var moved = Camera + step * a[k].TexelSize;
                var (b, _) = Cascades(moved, forward, NoonSun);
                Assert.Equal(a[k].TexelSize, b[k].TexelSize);
                var shift = ShadowMath.TexelOf(b[k], point - moved) - ShadowMath.TexelOf(a[k], point - Camera);
                Assert.True(MathF.Abs(Fraction(shift.X)) < 2e-3f && MathF.Abs(Fraction(shift.Y)) < 2e-3f,
                            $"cascade {k}: moving {step} of a texel slid the grid by {shift} texels");
            }
        }

        // Turning the camera moves each map by whole texels and never changes its size.
        var (turned, _) = Cascades(Camera, Vector3.Normalize(new Vector3(-0.2f, 0.1f, 0.9f)), NoonSun);
        for (int k = 0; k < a.Length; k++)
        {
            Assert.Equal(a[k].TexelSize, turned[k].TexelSize);
            double dx = (turned[k].MinX - a[k].MinX) / a[k].TexelSize, dy = (turned[k].MinY - a[k].MinY) / a[k].TexelSize;
            Assert.Equal(0.0, Math.Abs(dx - Math.Round(dx)), 3);
            Assert.Equal(0.0, Math.Abs(dy - Math.Round(dy)), 3);
        }
    }

    [Fact]
    public void AQuantisedSunHoldsTheGridStill()
    {
        const float Step = ShadowMath.DefaultSunStep;
        float radians = Step * MathF.PI / 180f;
        var forward = Vector3.Normalize(new Vector3(0.7f, -0.2f, 0.6f));
        var point = Camera + new Vector3(10.37f, -12f, 21.91f);
        var sun = ShadowMath.QuantiseSun(DuskSun, Step);
        Assert.True(MathF.Acos(Math.Clamp(Vector3.Dot(sun, DuskSun), -1f, 1f)) <= radians, "the snapped sun is within a step of the real one");
        Assert.Equal(sun, ShadowMath.QuantiseSun(sun, Step));

        // The sun creeps a fifth of a step at a time (a few seconds of a game day): the light's axes and
        // every cascade's grid stay exactly where they were.
        var (still, _) = Cascades(Camera, forward, sun);
        var (rawStill, _) = Cascades(Camera, forward, DuskSun);
        bool rawSlid = false;
        for (int i = 1; i <= 2; i++)
        {
            var crept = Vector3.Normalize(Vector3.Transform(DuskSun, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.2f * radians * i)));
            if (ShadowMath.QuantiseSun(crept, Step) != sun) continue;   // crossed into the next step: allowed once a step
            var (fits, _) = Cascades(Camera, forward, ShadowMath.QuantiseSun(crept, Step));
            for (int k = 0; k < fits.Length; k++)
            {
                Assert.Equal(still[k].View, fits[k].View);
                Assert.Equal(still[k].MinX, fits[k].MinX);
                Assert.Equal(still[k].MinY, fits[k].MinY);
            }

            // Without the snap, the same creep slides the grid under the point (that is the shimmer).
            var (raw, _) = Cascades(Camera, forward, crept);
            var shift = ShadowMath.TexelOf(raw[2], point - Camera) - ShadowMath.TexelOf(rawStill[2], point - Camera);
            rawSlid |= MathF.Abs(Fraction(shift.X)) > 1e-2f || MathF.Abs(Fraction(shift.Y)) > 1e-2f;
        }
        Assert.True(rawSlid, "an unsnapped sun should slide the texel grid");

        // A step of 0 leaves the sun alone; a sun straight overhead stays overhead.
        Assert.Equal(Vector3.Normalize(DuskSun), ShadowMath.QuantiseSun(DuskSun, 0f));
        Assert.True(Vector3.Distance(-Vector3.UnitY, ShadowMath.QuantiseSun(-Vector3.UnitY, Step)) < 1e-5f);
    }

    [Fact]
    public void AlphaTestedMaterialsCastCutOutShadows()
    {
        Assert.True(ShadowMath.Casts(RenderPass.AlphaTested, castShadows: true));
        Assert.False(ShadowMath.Casts(RenderPass.AlphaTested, castShadows: false));
        Assert.Equal("ShadowCaster", ShadowMath.CasterTechnique(RenderPass.Opaque, skinned: false));
        Assert.Equal("ShadowCasterSkinned", ShadowMath.CasterTechnique(RenderPass.Opaque, skinned: true));
        Assert.Equal("ShadowCasterAlphaTest", ShadowMath.CasterTechnique(RenderPass.AlphaTested, skinned: false));
        Assert.Equal("ShadowCasterAlphaTestSkinned", ShadowMath.CasterTechnique(RenderPass.AlphaTested, skinned: true));
        Assert.True(ShadowMath.CutOutKeeps(0.5f, 0.5f));
        Assert.False(ShadowMath.CutOutKeeps(0.49f, 0.5f));

        // The engine's effects have the techniques, and the cut-out ones clip as the scene's AlphaTest does.
        string shaders = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content", "shaders");
        string lit = File.ReadAllText(Path.Combine(shaders, "lit.fx"));
        string sprite = File.ReadAllText(Path.Combine(shaders, "sprite.fx"));
        Assert.Contains("technique ShadowCasterAlphaTest\n", lit.Replace("\r", ""));
        Assert.Contains("technique ShadowCasterAlphaTestSkinned", lit);
        Assert.Contains("technique ShadowCasterAlphaTest\n", sprite.Replace("\r", ""));
        Assert.Contains("AlphaTest(tex2D(AlbedoSampler, input.UV).a", lit);
        Assert.Contains("AlphaTest(tex2D(AlbedoSampler, input.UV).a", sprite);

        // Every sprite material the engine ships is alpha-tested and casts; particles and decals are
        // transparent and do not.
        var fx = new MountFixture();
        fx.Write("engine", "data/materials.json", File.ReadAllText(Path.Combine(shaders, "..", "data", "materials.json")));
        fx.Mount("engine", "sage");
        var store = new RecordStore();
        store.Register<MaterialRecord>();
        store.Load(fx.Vfs);
        Assert.Equal(0, store.ErrorCount);
        bool Casts(string id) { var m = store.Get<MaterialRecord>(new RecordId("sage", id)); return ShadowMath.Casts(m.Pass, m.CastShadows); }
        Assert.True(Casts("sprite_default"));
        Assert.True(Casts("sprite_lit"));
        Assert.True(Casts("lit_default"));
        Assert.False(Casts("particle_additive"));
        Assert.False(Casts("particle_blend"));
        Assert.False(Casts("decal"));
    }

    [Fact]
    public void ALeafBillboardCastsACutOutShadow()
    {
        // A 2 m leaf card on a cylindrical billboard, pivot at its bottom centre, 1 m up a post. Its texture
        // is an 8x8 alpha mask: solid, with a 2x2 hole in the middle and a faint (below-cutoff) left column.
        const int N = 8;
        const float Cutoff = 0.5f;
        float Alpha(int col, int row) => col == 0 ? 0.3f : (col is 3 or 4 && row is 3 or 4) ? 0f : 1f;
        var size = new Vector2(2f, 2f);
        var pivot = new Vector2(0.5f, 1f);
        var camera = new Vector3(3f, 1.7f, -4f);   // origin space; the ground is y = 0
        var leaf = camera + new Vector3(6f, -0.7f, 4f);
        var sun = ShadowMath.QuantiseSun(Vector3.Normalize(new Vector3(-0.5f, -0.8f, 0.3f)), ShadowMath.DefaultSunStep);

        (Vector3 Right, Vector3 Up)? first = null;
        foreach (var forward in new[] { Vector3.Normalize(leaf - camera), -Vector3.UnitZ, Vector3.Normalize(new Vector3(0.3f, -0.9f, 0.2f)) })
        {
            var (fits, _) = Cascades(camera, forward, sun);

            // Turned to the sun, as the batcher turns it in the caster views: upright, facing the sun's
            // bearing — whichever way the player looks.
            var (right, up) = ShadowMath.BillboardAxes(fits[0], cylindrical: true);
            Assert.Equal(Vector3.UnitY, up);
            var normal = Vector3.Cross(right, up);
            var bearing = Vector3.Normalize(new Vector3(sun.X, 0f, sun.Z));
            Assert.True(MathF.Abs(Vector3.Dot(normal, bearing)) > 0.9999f);
            first ??= (right, up);
            Assert.Equal(first.Value.Right, right);

            // The quad's corners as the batcher lays them out (u right, v down from the top-left).
            var topLeft = leaf + right * (-pivot.X * size.X) + up * (pivot.Y * size.Y);
            Vector3 At(float u, float v) => topLeft + right * (u * size.X) - up * (v * size.Y);

            int shadowed = 0, lit = 0;
            for (int row = 0; row < N; row++)
                for (int col = 0; col < N; col++)
                {
                    // The texel's centre on the card, and the point of ground its shadow falls on.
                    var onCard = At((col + 0.5f) / N, (row + 0.5f) / N);
                    var ground = onCard + sun * (onCard.Y / -sun.Y);
                    Assert.Equal(0f, ground.Y, 4);

                    // The ground reads a cascade that the card is drawn into, and the card is nearer the
                    // sun there (a smaller depth), so where the card's texel survives the clip, the ground
                    // compares as shadowed; where it is cut out, the map holds the clear value there.
                    int k = ShadowMath.CascadeOf(fits, ground - camera);
                    Assert.True(k >= 0, "the leaf's shadow is on a cascade");
                    Assert.True(ShadowMath.Contains(fits[k], onCard - camera));
                    var cardClip = ShadowMath.ToClip(fits[k], onCard - camera);
                    var groundClip = ShadowMath.ToClip(fits[k], ground - camera);
                    Assert.True(Vector2.Distance(new Vector2(cardClip.X, cardClip.Y), new Vector2(groundClip.X, groundClip.Y)) < 1e-3f);
                    float stored = ShadowMath.CutOutKeeps(Alpha(col, row), Cutoff) ? cardClip.Z : 1f;   // cleared to 1
                    bool inShadow = groundClip.Z - fits[k].DepthBias > stored;
                    Assert.Equal(ShadowMath.CutOutKeeps(Alpha(col, row), Cutoff), inShadow);
                    if (inShadow) shadowed++; else lit++;
                }
            // 64 texels: 8 in the faint column and 4 in the hole let the sun through.
            Assert.Equal(52, shadowed);
            Assert.Equal(12, lit);
        }
    }
}
