#nullable enable
using System;
using System.Collections.Generic;
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
    private const float Distance = ShadowMath.DefaultDistance;
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
        Assert.False(ShadowMath.Casts(RenderPass.AlphaTested, castShadows: true));
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
}
