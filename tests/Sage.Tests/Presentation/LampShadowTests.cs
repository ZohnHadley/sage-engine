#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Lamp shadows (issue #315's last part): which lamps get a map, where their maps sit, and the lookup the
// shaders make (common.fxh `LampShadow`), all decided headless by `LampShadows`. The drawing (`sage:shadow`
// drawing each face's casters, the shaders reading the atlas) needs a GPU and is exercised by the smoke run.
public class LampShadowTests
{
    public LampShadowTests() { _ = TestEnv.UserRoot; }

    private static LightSample Lamp(Vector3 at, float range = 8f, bool shadows = true, Vector4 spot = default) =>
        new(at, Vector3.One, range) { CastsShadows = shadows, Spot = spot };

    private static LightSample Spot(Vector3 at, Vector3 direction, float cone, float range = 10f) =>
        Lamp(at, range, spot: LightRules.Spot(direction, cone, 0f));

    private static IEnumerable<Vector3> Directions()
    {
        var random = new Random(315);
        for (int i = 0; i < 200; i++)
        {
            var d = new Vector3(random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1, random.NextSingle() * 2 - 1);
            if (d.LengthSquared() > 1e-4f) yield return Vector3.Normalize(d);
        }
        // The axes and the cube's edges and corners, where the faces meet.
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitZ })
            yield return axis;
        yield return Vector3.Normalize(new Vector3(1, 1, 0));
        yield return Vector3.Normalize(new Vector3(-1, 1, -1));
        yield return Vector3.Normalize(new Vector3(0.2f, -1, 1));
    }

    // A lamp that does not say `shadows` is never given a map, so it costs no target, view or draw — the
    // default, from data or code; a lamp that says it does.
    [Fact]
    public void ALampWithShadowsOffCostsNoMap()
    {
        Assert.False(new PointLight().Shadows);

        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule(), new LightsModule())
            .File("data/lamps.json", """
                [{ "type": "prefab", "id": "plain", "name": "plain", "parts": { "light": { "range": 6 } } },
                 { "type": "prefab", "id": "shadowed", "name": "shadowed", "parts": { "light": { "range": 6, "shadows": true } } },
                 { "type": "scene", "id": "room",
                   "place": [ { "prefab": "plain", "at": [0, 2, 0], "name": "plain" },
                              { "prefab": "shadowed", "at": [9, 2, 0], "name": "shadowed" } ] }]
                """)
            .StartScene("sage:room").Boot("lamp-shadows");
        var world = app.World;
        var plain = world.Get<PointLight>(world.FindByName("plain"));
        var shadowed = world.Get<PointLight>(world.FindByName("shadowed"));
        Assert.False(plain.Shadows);
        Assert.True(shadowed.Shadows);

        // The client's light extract samples them so; the plain one alone is chosen by nothing.
        var plainSample = LightRules.Sample(plain, new Vector3(0, 2, 0), Quaternion.Identity, 0);
        var shadowedSample = LightRules.Sample(shadowed, new Vector3(9, 2, 0), Quaternion.Identity, 0);
        Assert.False(plainSample.CastsShadows);
        Assert.True(shadowedSample.CastsShadows);
        Span<int> chosen = stackalloc int[LampShadows.MaxLamps];
        Assert.Equal(0, LampShadows.Choose(new[] { plainSample }, LampShadows.MaxLamps, chosen));
        Assert.Equal(1, LampShadows.Choose(new[] { plainSample, shadowedSample }, LampShadows.MaxLamps, chosen));
        Assert.Equal(1, chosen[0]);

        // And a lamp with no map goes to the shader as it always did: nothing in front of it moves.
        Span<int> order = stackalloc int[2];
        LampShadows.DrawOrder(new[] { plainSample, plainSample with { Range = 3 } }, order);
        Assert.Equal(new[] { 0, 1 }, order.ToArray());
    }

    // Of the lamps that ask, the nearest (to the edge of their light) get maps, at most the budget; lamps
    // that do not ask, give no light or reach nothing are passed over.
    [Fact]
    public void TheNearestLampsGetMapsWithinTheBudget()
    {
        var lights = new[]
        {
            Lamp(new Vector3(30, 0, 0)),                    // 0: 22 m beyond its range
            Lamp(new Vector3(0, 0, 12)),                    // 1: 4 m beyond
            Lamp(new Vector3(1, 0, 0), shadows: false),     // 2: nearest, but does not ask
            Lamp(new Vector3(0, 9, 0)),                     // 3: 1 m beyond
            Lamp(new Vector3(-3, 0, 0)),                    // 4: the camera stands in its light
            Lamp(new Vector3(0, 0, -2), range: 0f),         // 5: lights nothing
            Lamp(new Vector3(2, 0, 0)) with { Colour = Vector3.Zero },   // 6: gives nothing
            Lamp(new Vector3(0, -5, 0), range: 6f),         // 7: the camera stands in it too, but further from the lamp
        };
        Span<int> chosen = stackalloc int[LampShadows.MaxLamps];

        Assert.Equal(2, LampShadows.Choose(lights, 2, chosen));
        Assert.Equal(new[] { 4, 7 }, chosen[..2].ToArray());

        Assert.Equal(4, LampShadows.Choose(lights, 4, chosen));
        Assert.Equal(new[] { 4, 7, 3, 1 }, chosen.ToArray());

        // The budget is clamped to MaxLamps and to the room given; 0 is none.
        Assert.Equal(LampShadows.MaxLamps, LampShadows.Choose(lights, 99, chosen));
        Assert.Equal(0, LampShadows.Choose(lights, 0, chosen));
        Assert.Equal(1, LampShadows.Choose(lights, 3, chosen[..1]));
        Assert.Equal(4, chosen[0]);

        // Ties go to the earlier light: the same answer every frame.
        var twins = new[] { Lamp(new Vector3(0, 0, 3)), Lamp(new Vector3(3, 0, 0)), Lamp(new Vector3(0, 3, 0)) };
        Assert.Equal(2, LampShadows.Choose(twins, 2, chosen));
        Assert.Equal(new[] { 0, 1 }, chosen[..2].ToArray());
    }

    // Each lamp has a block of 3 x 2 tiles in one target, sized by the budget so it is never remade, and no
    // larger than a texture may be.
    [Fact]
    public void TheAtlasHoldsEachLampsBlockOfFaces()
    {
        var atlas = LampShadows.Atlas(2, 512);
        Assert.Equal((512, 1536, 2048), (atlas.TileSize, atlas.Width, atlas.Height));
        Assert.Equal((0, 0), atlas.Block(0));
        Assert.Equal((0, 1024), atlas.Block(1));
        Assert.Equal((1024, 1024 + 512), atlas.Tile(1, 5));

        var big = LampShadows.Atlas(4, 2048);
        Assert.Equal(512, big.TileSize);   // four blocks of two rows in 4096
        Assert.True(big.Width <= ShadowMath.MaxAtlasSize && big.Height <= ShadowMath.MaxAtlasSize);
        Assert.Equal(LampShadows.MaxLamps, LampShadows.Atlas(99, 512).Lamps);

        // Every face of every block is its own square inside the target.
        var seen = new HashSet<(int, int)>();
        for (int slot = 0; slot < big.Lamps; slot++)
            for (int face = 0; face < LampShadows.CubeFaces; face++)
            {
                var (x, y) = big.Tile(slot, face);
                Assert.True(seen.Add((x, y)));
                Assert.True(x >= 0 && y >= 0 && x + big.TileSize <= big.Width && y + big.TileSize <= big.Height);
            }

        // A lamp's map says where its block is, as uv.
        var fit = LampShadows.Fit(Lamp(Vector3.Zero), 1, atlas);
        Assert.Equal(new Vector2(0f, 0.5f), fit.Corner);
        for (int face = 0; face < fit.Faces; face++)
            Assert.Equal(atlas.Tile(1, face), (fit.Views[face].X, fit.Views[face].Y));
    }

    // A spot light draws one view down its cone, wide enough for the cone; a wider spot, and a point light,
    // a cube.
    [Fact]
    public void ASpotMapLooksDownItsCone()
    {
        var atlas = LampShadows.Atlas(2, 512);
        var down = Vector3.Normalize(new Vector3(0.2f, -1f, 0.1f));
        var lamp = Spot(new Vector3(4, 5, -2), down, 30f);
        Assert.Equal(30f, LampShadows.ConeOf(lamp), 2);
        Assert.False(LampShadows.IsCube(lamp));
        var fit = LampShadows.Fit(lamp, 0, atlas);
        Assert.Equal(1, fit.Faces);
        Assert.False(fit.Cube);
        Assert.True(Vector3.Distance(down, fit.Forward) < 1e-5f);
        Assert.True(fit.InvTan > 0f);

        // On the axis: the middle of the map. At the cone's edge: still on it. Well outside, or behind: off.
        var middle = LampShadows.Project(fit, lamp.Position + down * 3f);
        Assert.True(middle.Inside);
        Assert.True(Vector2.Distance(middle.Uv, new Vector2(0.5f, 0.5f)) < 1e-4f);
        Assert.Equal(3f, middle.Distance, 3);
        var side = Vector3.Normalize(Vector3.Cross(down, Vector3.UnitX));
        var edge = Vector3.Normalize(down * MathF.Cos(30f * MathF.PI / 180f) + side * MathF.Sin(30f * MathF.PI / 180f));
        Assert.True(LampShadows.Project(fit, lamp.Position + edge * 4f).Inside);
        var outside = Vector3.Normalize(down * MathF.Cos(70f * MathF.PI / 180f) + side * MathF.Sin(70f * MathF.PI / 180f));
        Assert.False(LampShadows.Project(fit, lamp.Position + outside * 4f).Inside);
        Assert.False(LampShadows.Project(fit, lamp.Position - down * 2f).Inside);
        Assert.True(LampShadows.Lit(fit, lamp.Position - down * 2f, down, stored: 0f));   // off the map is lit

        // Too wide for one view, and all round: cubes.
        Assert.True(LampShadows.IsCube(Spot(Vector3.Zero, down, 80f)));
        Assert.True(LampShadows.IsCube(Lamp(Vector3.Zero)));
        Assert.Equal(LampShadows.CubeFaces, LampShadows.Fit(Spot(Vector3.Zero, down, 80f), 0, atlas).Faces);
    }

    // A cube's six faces cover every direction: each point falls on the face whose axis is nearest its
    // direction, inside it.
    [Fact]
    public void ACubeMapsFacesCoverEveryDirection()
    {
        var fit = LampShadows.Fit(Lamp(new Vector3(-3, 2, 7), range: 12f), 0, LampShadows.Atlas(2, 512));
        Assert.True(fit.Cube);
        var seen = new HashSet<int>();
        foreach (var d in Directions())
        {
            var sample = LampShadows.Project(fit, fit.Position + d * 5f);
            Assert.True(sample.Inside, $"{d}");
            Assert.InRange(sample.Uv.X, 0f, 1f);
            Assert.InRange(sample.Uv.Y, 0f, 1f);
            float best = float.MinValue;
            for (int face = 0; face < LampShadows.CubeFaces; face++) best = MathF.Max(best, Vector3.Dot(d, LampShadows.FaceAxis(face)));
            Assert.Equal(best, Vector3.Dot(d, LampShadows.FaceAxis(sample.Face)), 5);
            Assert.Equal(best * 5f, sample.Distance, 3);
            seen.Add(sample.Face);
        }
        Assert.Equal(LampShadows.CubeFaces, seen.Count);
    }

    // The shaders' lookup has no matrices; the casters are drawn through matrices. They agree: a point's
    // uv on its face and the depth it compares with are what the face's view and projection give it.
    [Fact]
    public void TheShaderLookupMatchesTheCasterMatrices()
    {
        var atlas = LampShadows.Atlas(2, 512);
        var down = Vector3.Normalize(new Vector3(-0.3f, -1f, 0.4f));
        foreach (var lamp in new[] { Lamp(new Vector3(1, 3, 2), range: 9f), Spot(new Vector3(-5, 4, 1), down, 40f, 15f), Spot(Vector3.Zero, Vector3.UnitY, 20f) })
        {
            var fit = LampShadows.Fit(lamp, 1, atlas);
            foreach (var d in Directions())
                foreach (float distance in new[] { 0.5f, 2.5f, 7f })
                {
                    var at = lamp.Position + d * distance;
                    var sample = LampShadows.Project(fit, at);
                    if (!sample.Inside) continue;
                    var uv = LampShadows.CasterUv(fit, sample.Face, at);
                    Assert.True(Vector2.Distance(uv, sample.Uv) < 1e-4f, $"{d} at {distance}: {uv} vs {sample.Uv}");
                    Assert.Equal(LampShadows.StoredDepth(fit, sample.Face, at), LampShadows.QueryDepth(fit, sample.Distance, 0f), 4);
                }
        }
    }

    // The done criterion: a point behind an occluder, as the lamp sees it, reads the occluder's depth and is
    // shadowed; a point between the lamp and the occluder is lit; and the occluder's own lit side does not
    // shadow itself (the margin).
    [Fact]
    public void APointBehindAnOccluderIsShadowed()
    {
        var atlas = LampShadows.Atlas(2, 512);
        foreach (var lamp in new[] { Lamp(new Vector3(2, 3, -1), range: 10f), Spot(new Vector3(2, 3, -1), -Vector3.UnitY, 45f) })
        {
            var fit = LampShadows.Fit(lamp, 0, atlas);
            foreach (var d in Directions())
            {
                if (!fit.Cube && Vector3.Dot(d, -Vector3.UnitY) < MathF.Cos(40f * MathF.PI / 180f)) continue;   // the spot's cone only
                var occluder = lamp.Position + d * 2f;
                var face = LampShadows.Project(fit, occluder).Face;
                float stored = LampShadows.StoredDepth(fit, face, occluder);
                var facingLamp = -d;

                Assert.False(LampShadows.Lit(fit, lamp.Position + d * 4f, facingLamp, stored), $"behind, {d}");
                Assert.False(LampShadows.Lit(fit, lamp.Position + d * 2.3f, facingLamp, stored), $"just behind, {d}");
                Assert.True(LampShadows.Lit(fit, lamp.Position + d * 1.5f, facingLamp, stored), $"in front, {d}");
                Assert.True(LampShadows.Lit(fit, occluder, facingLamp, stored), $"its own face, {d}");
                Assert.True(LampShadows.Lit(fit, lamp.Position + d * 4f, facingLamp, stored: 1f), $"nothing in the way, {d}");
            }
        }
    }

    // A draw's lamps go to the shader with the ones that have maps first, in their order; the rest after.
    [Fact]
    public void TheLampsWithMapsGoFirst()
    {
        var chosen = new[]
        {
            Lamp(new Vector3(1, 0, 0)),
            Lamp(new Vector3(2, 0, 0)) with { ShadowSlot = 2 },
            Lamp(new Vector3(3, 0, 0)),
            Lamp(new Vector3(4, 0, 0)) with { ShadowSlot = 1 },
        };
        Span<int> order = stackalloc int[4];
        LampShadows.DrawOrder(chosen, order);
        Assert.Equal(new[] { 1, 3, 0, 2 }, order.ToArray());

        // A map is part of what a lamp is to a draw: the same lamp with and without one is not the same set.
        Assert.False(LightRules.SameSet(new[] { chosen[0] }, new[] { chosen[0] with { ShadowSlot = 1 } }));
    }
}
