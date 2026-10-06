#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Lamps that switch and flicker through entity I/O, spot lights, sprites lit by lamps, and the light grid
// that keeps a hundred lamps cheap (issue #314, docs/design/06 §3.9). All of it is decided headlessly:
// the client's light extract is `LightRules.Sample`, its renderer asks `LightGrid.Nearest`, and the
// shaders' sums are `LightRules.Sum` (common.fxh `PointLights`, sprite.fx `SpritePointLights`).
public class LampTests
{
    public LampTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static void Tick(World world, int times = 2)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static void Fire(HeadlessApp app, string command)
    {
        Assert.True(app.Engine.CVars.Execute(command, ExecSource.Code), command);
        Tick(app.World);
    }

    // A lamp by a wall and a creature beside it, as a level would place them.
    private static HeadlessApp LampScene(string lampParts = """{ "range": 6, "intensity": 2 }""") =>
        HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule(), new LightsModule())
            .File("data/lamps.json", $$"""
                [{ "type": "prefab", "id": "lamp", "name": "lamp", "parts": { "light": {{lampParts}} } },
                 { "type": "prefab", "id": "creature", "name": "creature" },
                 { "type": "scene", "id": "room",
                   "place": [ { "prefab": "lamp", "at": [0, 2, 0], "name": "lamp" },
                              { "prefab": "creature", "at": [1.5, 1, 0], "name": "creature" } ] }]
                """)
            .StartScene("sage:room").Boot("lamps");

    private static Entity Named(World world, string name)
    {
        var entity = world.FindByName(name);
        Assert.True(world.IsAlive(entity), name);
        return entity;
    }

    // What the client's light extract hands the renderer this frame: every lit lamp, sampled at the world's
    // simulated seconds (`LightExtract` is this loop, camera at the origin).
    private static List<LightSample> Extract(World world)
    {
        var samples = new List<LightSample>();
        double seconds = WorldTime.Of(world).Scaled;
        foreach (var entity in world.Query<GlobalTransform, PointLight>().Entities.ToEntityList())
        {
            ref readonly var light = ref world.Get<PointLight>(entity);
            if (!light.Lit) continue;
            var pose = world.Get<GlobalTransform>(entity).Current;
            samples.Add(LightRules.Sample(light, pose.Position, pose.Rotation, seconds));
        }
        return samples;
    }

    // How lit a sprite standing at `at` is by the lamps, facing the camera along `facing`: the four the
    // renderer chooses for it, summed as sprite.fx sums them (wrapped).
    private static float SpriteLight(World world, Vector3 at, Vector3 facing)
    {
        var grid = new LightGrid();
        grid.Build(Extract(world).ToArray());
        Span<LightSample> chosen = stackalloc LightSample[LightRules.PerObject];
        int n = grid.Nearest(at, chosen);
        var sum = LightRules.Sum(chosen[..n], at, facing, wrapped: true);
        return MathF.Max(MathF.Max(sum.X, sum.Y), sum.Z);
    }

    // The issue's done criterion, both halves: `ent_fire` on a lamp toggles it, and a sprite near the lamp
    // is lit by it — and dark again when it is switched off.
    [Fact]
    public void EntFireOnALampTogglesIt_AndASpriteNearItIsLitByIt()
    {
        using var app = LampScene();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        Tick(world);
        var lamp = Named(world, "lamp");
        var creature = Named(world, "creature");
        var at = world.Get<GlobalTransform>(creature).Current.Position;
        var facing = Vector3.UnitZ;   // a billboard faces the camera; the lamp is off to its side and above

        Assert.True(world.Get<PointLight>(lamp).Lit);
        float lit = SpriteLight(world, at, facing);
        Assert.True(lit > 0.1f, $"a sprite 1.8 m from a lamp got {lit}");
        Assert.Equal(0f, SpriteLight(world, at + new Vector3(20, 0, 0), facing));   // out of its range: nothing

        Fire(app, "ent_fire lamp Toggle");
        Assert.False(world.Get<PointLight>(lamp).Lit);
        Assert.Equal(0f, SpriteLight(world, at, facing));

        Fire(app, "ent_fire lamp Toggle");
        Assert.True(world.Get<PointLight>(lamp).Lit);
        Assert.Equal(lit, SpriteLight(world, at, facing), 5);

        Fire(app, "ent_fire lamp TurnOff");
        Assert.False(world.Get<PointLight>(lamp).Lit);
        Fire(app, "ent_fire lamp TurnOn");
        Assert.True(world.Get<PointLight>(lamp).Lit);
    }

    // `SetPattern` sets a flicker by preset or by letters, empty makes it steady, and nonsense is refused
    // with the light left as it was.
    [Fact]
    public void SetPatternFlickersALampAndEmptyMakesItSteady()
    {
        using var app = LampScene();
        var world = app.World;
        Tick(world);
        var lamp = Named(world, "lamp");
        Assert.Null(world.Get<PointLight>(lamp).Pattern);

        Fire(app, "ent_fire lamp SetPattern candle");
        Assert.Equal("candle", world.Get<PointLight>(lamp).Pattern);
        Fire(app, "ent_fire lamp SetPattern mmamammmmammamamaaamammma");
        Assert.Equal("mmamammmmammamamaaamammma", world.Get<PointLight>(lamp).Pattern);
        Fire(app, "ent_fire lamp SetPattern Bright!");
        Assert.Equal("mmamammmmammamamaaamammma", world.Get<PointLight>(lamp).Pattern);   // refused
        Fire(app, "ent_fire lamp SetPattern");
        Assert.Null(world.Get<PointLight>(lamp).Pattern);
        Assert.Equal("sage.gameplay.lights", app.Engine.Registrations.OwnerOf("entity input", "SetPattern@sage:point_light"));
    }

    // Quake's light styles: `a` dark, `m` as set, `z` a little over double, one letter a step at ten a second
    // unless the light says otherwise; looping, and the same answer for the same time on every run.
    [Fact]
    public void APatternIsAQuakeLightStyleReadAtTheWorldsTime()
    {
        Assert.Equal(1f, LightStyles.Brightness(null, 0f, 3.7));
        Assert.Equal(1f, LightStyles.Brightness("", 0f, 3.7));
        Assert.Equal(1f, LightStyles.Brightness("m", 0f, 3.7));
        Assert.Equal(0f, LightStyles.Brightness("a", 0f, 0));
        Assert.Equal(25f / 12f, LightStyles.Brightness("z", 0f, 0));

        // "am" at the default ten a second: dark, lit, dark again as it loops; at two a second, slower.
        Assert.Equal(0f, LightStyles.Brightness("am", 0f, 0.05));
        Assert.Equal(1f, LightStyles.Brightness("am", 0f, 0.15));
        Assert.Equal(0f, LightStyles.Brightness("am", 0f, 0.25));
        Assert.Equal(0f, LightStyles.Brightness("am", 2f, 0.45));
        Assert.Equal(1f, LightStyles.Brightness("am", 2f, 0.55));
        Assert.Equal(1f, LightStyles.Brightness("am", 0f, -0.05));   // before zero still loops

        // Presets by name, any case; letters only otherwise.
        Assert.True(LightStyles.TryResolve("Torch", out var torch));
        Assert.Equal("nmonqnmomnmomomno", torch);
        Assert.Contains("fluorescent", LightStyles.PresetNames);
        Assert.False(LightStyles.TryResolve("MMA", out _));
        Assert.False(LightStyles.TryResolve("no such preset", out _));
        Assert.Equal(1f, LightStyles.Brightness("no such preset", 0f, 1.0));   // steady, not dark

        // Sampled into what the renderer draws: the colour times the intensity times the pattern.
        var light = new PointLight { Colour = new Vector3(1, 0.5f, 0.25f), Range = 5, Intensity = 2, Pattern = "az" };
        Assert.Equal(Vector3.Zero, LightRules.Sample(light, Vector3.Zero, Quaternion.Identity, 0.05).Colour);
        Assert.Equal(new Vector3(1, 0.5f, 0.25f) * 2f * (25f / 12f), LightRules.Sample(light, Vector3.Zero, Quaternion.Identity, 0.15).Colour);
    }

    // A pattern that is neither letters nor a preset is reported where the lamp is placed (the light part's
    // error, naming the presets), not a lamp that silently does not flicker; it is lit, steady.
    [Fact]
    public void ABadPatternIsReportedWhenTheLampIsPlaced()
    {
        using var capture = new CaptureSink();
        using var app = LampScene("""{ "range": 6, "pattern": "Bright!" }""");
        Tick(app.World);
        Assert.Contains(capture.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("light: \"pattern\": \"Bright!\" is neither letters a..z nor a preset (") && e.Message.Contains("torch"));
        Assert.Equal(1f, LightStyles.Brightness(app.World.Get<PointLight>(Named(app.World, "lamp")).Pattern, 0f, 0.3));

        using var good = LampScene("""{ "range": 6, "pattern": "torch", "patternRate": 5, "cone": 30, "innerCone": 20 }""");
        Tick(good.World);
        Assert.Equal(0, good.Records.ErrorCount);
        var light = good.World.Get<PointLight>(Named(good.World, "lamp"));
        Assert.Equal("torch", light.Pattern);
        Assert.Equal(5f, light.PatternRate);
        Assert.Equal(30f, light.Cone);
        Assert.Equal(20f, light.InnerCone);
    }

    // Spot lights: full strength inside the inner cone, nothing outside the outer, a ramp between, and
    // nothing behind; pointed along the entity's forward (-Z), turned with it.
    [Fact]
    public void ASpotLightLightsOnlyItsCone()
    {
        var spot = new LightSample(Vector3.Zero, Vector3.One, 20f)
        {
            Spot = LightRules.Spot(-Vector3.UnitZ, coneDegrees: 30f, innerDegrees: 20f),
        };
        Vector3 At(float degrees) => new(MathF.Sin(degrees * MathF.PI / 180f) * 5f, 0, -MathF.Cos(degrees * MathF.PI / 180f) * 5f);

        Assert.Equal(1f, spot.ConeAt(At(0)), 4);
        Assert.Equal(1f, spot.ConeAt(At(19)), 4);
        float half = spot.ConeAt(At(25));
        Assert.InRange(half, 0.2f, 0.8f);
        Assert.Equal(0f, spot.ConeAt(At(31)), 4);
        Assert.Equal(0f, spot.ConeAt(new Vector3(0, 0, 5)));      // behind it
        Assert.Equal(0f, spot.InfluenceAt(At(40)));
        Assert.True(spot.InfluenceAt(At(10)) > 0f);

        // A point light is a cone of nothing: all round.
        var point = new LightSample(Vector3.Zero, Vector3.One, 20f);
        Assert.Equal(1f, point.ConeAt(new Vector3(0, 0, 5)));
        Assert.Equal(Vector4.Zero, LightRules.Spot(Vector3.UnitZ, 0f, 0f));
        Assert.Equal(Vector4.Zero, LightRules.Spot(Vector3.UnitZ, 180f, 0f));

        // From a component on an entity turned to face +X: the cone follows the entity.
        var lamp = new PointLight { Colour = Vector3.One, Range = 20, Intensity = 1, Cone = 30 };
        var turned = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);   // -Z turned to +X
        var sample = LightRules.Sample(lamp, Vector3.Zero, turned, 0);
        Assert.True(sample.ConeAt(new Vector3(5, 0, 0)) > 0.99f);
        Assert.Equal(0f, sample.ConeAt(new Vector3(0, 0, -5)), 4);

        // The shader's sum agrees: the floor under a downward spot is lit, the floor beside it is not.
        var down = new LightSample(new Vector3(0, 4, 0), Vector3.One, 10f) { Spot = LightRules.Spot(-Vector3.UnitY, 25f, 0f) };
        Assert.True(LightRules.Sum(new[] { down }, Vector3.Zero, Vector3.UnitY, wrapped: false).X > 0.1f);
        Assert.Equal(0f, LightRules.Sum(new[] { down }, new Vector3(4, 0, 0), Vector3.UnitY, wrapped: false).X);

        // And the choice of four ignores a spot that points away, however close.
        Span<LightSample> chosen = stackalloc LightSample[LightRules.PerObject];
        var away = new LightSample(new Vector3(0, 1, 0), Vector3.One * 4, 10f) { Spot = LightRules.Spot(Vector3.UnitY, 30f, 0f) };
        Assert.Equal(0, LightRules.Nearest(stackalloc LightSample[] { away }, Vector3.Zero, chosen));
    }

    // A sprite's wrapped lighting reaches it from the side, where a wall's Lambert would not: a lamp level
    // with a billboard still lights it (sprite.fx's reason for half-Lambert).
    [Fact]
    public void ASpriteIsLitFromTheSideWhereAWallWouldNotBe()
    {
        var lamp = new LightSample(new Vector3(2, 0, 0), Vector3.One, 6f);
        var facing = Vector3.UnitZ;
        Assert.Equal(0f, LightRules.Sum(new[] { lamp }, Vector3.Zero, facing, wrapped: false).X, 5);
        Assert.True(LightRules.Sum(new[] { lamp }, Vector3.Zero, facing, wrapped: true).X > 0.1f);
    }

    private static LightSample[] Town(int count, int seed)
    {
        var random = new Random(seed);
        var lights = new LightSample[count];
        for (int i = 0; i < count; i++)
        {
            var at = new Vector3(random.NextSingle() * 200 - 100, random.NextSingle() * 10, random.NextSingle() * 200 - 100);
            float b = 0.2f + random.NextSingle() * 3f;
            var sample = new LightSample(at, new Vector3(b, b * 0.8f, b * 0.6f), 2f + random.NextSingle() * 14f);
            if (i % 5 == 0) sample = sample with { Spot = LightRules.Spot(new Vector3(random.NextSingle() - 0.5f, -1, random.NextSingle() - 0.5f), 20 + random.NextSingle() * 50, 0) };
            lights[i] = sample;
        }
        // One floodlight that reaches everything (the grid's everywhere list), one lamp with no reach.
        lights[count / 2] = new LightSample(new Vector3(0, 50, 0), new Vector3(0.3f), 500f);
        lights[count / 3] = new LightSample(new Vector3(1, 1, 1), Vector3.One, 0f);
        return lights;
    }

    // The grid is a cache, not a decision: for any point it chooses exactly what a pass over every light
    // chooses, in the same order (06 §3.9's choice of four, now for a town's hundreds of lamps).
    [Fact]
    public void TheGridChoosesWhatTheFullListChooses()
    {
        Span<LightSample> expected = stackalloc LightSample[LightRules.PerObject];
        Span<LightSample> actual = stackalloc LightSample[LightRules.PerObject];
        foreach (int count in new[] { 3, LightGrid.Threshold, 300 })
        {
            var lights = Town(count, seed: count);
            var grid = new LightGrid();
            grid.Build(lights);
            Assert.Equal(count, grid.Count);

            var random = new Random(7);
            for (int p = 0; p < 2000; p++)
            {
                var at = new Vector3(random.NextSingle() * 240 - 120, random.NextSingle() * 12 - 1, random.NextSingle() * 240 - 120);
                int e = LightRules.Nearest(lights, at, expected);
                int a = grid.Nearest(at, actual);
                Assert.Equal(e, a);
                for (int i = 0; i < e; i++) Assert.Equal(expected[i], actual[i]);
            }
        }
    }

    // And a grid of three hundred lamps keeps far fewer than three hundred per cell: a draw in the middle
    // of a town looks at the handful near it.
    [Fact]
    public void AGridCellHoldsOnlyTheLampsThatReachIt()
    {
        var lights = Town(300, seed: 1);
        var grid = new LightGrid();
        grid.Build(lights);
        Assert.InRange(grid.Cells, 300, 300 * LightGrid.MaxCellsPerLight);
        Assert.True(grid.Cells / 300.0 < LightGrid.MaxCellsPerLight);
    }
}

// A frame's grid is rebuilt every view of every frame, so it must not make garbage once warm (02 §4.6).
[Collection(MeasurementsCollection.Name)]
public class LightGridAllocationTests
{
    [Fact]
    public void RebuildingAndAskingTheGridAllocatesNothingOnceWarm()
    {
        var random = new Random(3);
        var lights = new LightSample[200];
        for (int i = 0; i < lights.Length; i++)
            lights[i] = new LightSample(new Vector3(random.NextSingle() * 100, 2, random.NextSingle() * 100), Vector3.One, 6f);
        var grid = new LightGrid();
        var chosen = new LightSample[LightRules.PerObject];

        void Frame()
        {
            grid.Build(lights);
            for (int i = 0; i < 100; i++) grid.Nearest(new Vector3(i, 2, i), chosen);
        }
        Frame();   // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
