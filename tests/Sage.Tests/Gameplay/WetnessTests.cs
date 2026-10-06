#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Wet ground and puddles from rain (issue #311, docs/design/06 §3.13): the world's wetness rises while it rains
// and dries after (`WetnessRules.Step`), and a surface shows it by its normal, its sky term and the puddle mask
// (`WetnessRules.Surface`, which common.fxh's `Wetting` is).
#pragma warning disable SAGE0122, SAGE0130   // brush levels and the material's surface fields are experimental
public class WetnessTests
{
    public WetnessTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private const string Records = """
        [{ "type": "weather", "id": "clear", "label": "clear", "drying": 0.01 },
         { "type": "weather", "id": "rain", "label": "rain", "wetting": 0.05, "drying": 0 },
         { "type": "weather", "id": "drizzle", "label": "drizzle", "wetting": 0.01 }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp Boot()
    {
        var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<WeatherRecord>())
            .File("data/weather.json", Records)
            .Boot("wet");
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static void Run(World world, float seconds, float dt = Dt)
    {
        for (int i = 0; i < (int)MathF.Round(seconds / dt); i++) world.RunFixed(dt);
    }

    // ---- the world's wetness ----

    [Fact]
    public void RainWetsTheWorldAndItDriesAfter()
    {
        using var app = Boot();
        var world = app.World;
        var weather = world.Resources.Get<Weather>();
        Assert.Equal(0f, weather.Wetness);

        weather.Set(Id("rain"), 0f);
        Run(world, 10f);
        Assert.Equal(0.5f, weather.Wetness, 2);                // 0.05 a second
        Run(world, 15f);
        Assert.Equal(1f, weather.Wetness);                     // soaked, and no wetter

        weather.Set(Id("clear"), 0f);
        Run(world, 30f);
        Assert.Equal(0.7f, weather.Wetness, 2);                // 0.01 a second
        Run(world, 80f);
        Assert.Equal(0f, weather.Wetness);
    }

    // Net of the two, blended as the weather changes: a drizzle with the default drying still wets, slowly, and a
    // change from rain to clear eases the wetting off rather than stopping it.
    [Fact]
    public void WettingAndDryingAreNetAndBlend()
    {
        var records = Boot().Records;
        var weather = new Weather { Current = Id("drizzle"), Target = Id("drizzle"), Blend = 1f };
        WetnessRules.Step(weather, records, 10f);
        Assert.Equal((0.01f - 0.004f) * 10f, weather.Wetness, 4);

        weather = new Weather { Current = Id("rain"), Target = Id("clear"), Blend = 0.5f, Wetness = 0.5f };
        WetnessRules.Step(weather, records, 1f);
        Assert.Equal(0.5f + (0.025f - 0.005f), weather.Wetness, 4);

        // A missing record is a clear sky, and the default clear dries.
        weather = new Weather { Current = Id("nope"), Target = Id("nope"), Blend = 1f, Wetness = 1f };
        WetnessRules.Step(weather, records, 10f);
        Assert.Equal(1f - 0.04f, weather.Wetness, 4);
    }

    [Fact]
    public void AnInteriorShowsNoWetness()
    {
        using var app = Boot();
        var world = app.World;
        world.Resources.Get<Weather>().Wetness = 0.8f;
        Assert.Equal(0.8f, WetnessRules.Shown(world));

        world.Resources.Replace(new ActiveScene { Interior = true });
        Assert.Equal(0f, WetnessRules.Shown(world));
    }

    // ---- a surface ----

    [Fact]
    public void FlatOpenGroundIsWet_WallsAndCoveredFloorsAreNot()
    {
        var flat = WetnessRules.Surface(1f, 1f, 1f, 0f, 1f);
        Assert.Equal(1f, flat.Wet);

        Assert.Equal(0f, WetnessRules.Surface(1f, 0f, 1f, 1f, 1f).Wet);                 // a wall
        Assert.Equal(0f, WetnessRules.Surface(1f, -1f, 1f, 1f, 1f).Wet);                // a ceiling
        float slope = WetnessRules.Surface(1f, MathF.Cos(MathF.PI / 4f), 1f, 0f, 1f).Wet;   // 45°
        Assert.InRange(slope, 0.4f, 0.9f);

        // The sky term: a lightmapped floor under a roof sees the bake's floor of 0.2, and stays dry.
        Assert.Equal(0f, WetnessRules.Surface(1f, 1f, 0.2f, 1f, 1f).Wet);
        Assert.Equal(0f, WetnessRules.Surface(1f, 1f, 0.2f, 1f, 1f).Puddle);
        Assert.Equal(1f, WetnessRules.Surface(1f, 1f, 0.9f, 0f, 1f).Wet);

        // A material that opts out.
        Assert.Equal((0f, 0f), WetnessRules.Surface(1f, 1f, 1f, 1f, 0f));

        // Dry is dry.
        Assert.Equal((0f, 0f), WetnessRules.Surface(0f, 1f, 1f, 1f, 1f));
    }

    [Fact]
    public void PuddlesGatherOnTheFlatWhereTheMaskIsHigh_AndGrowAsItGetsWetter()
    {
        // Not until the ground is past PuddleStart wet.
        Assert.Equal(0f, WetnessRules.Surface(0.4f, 1f, 1f, 1f, 1f).Puddle);
        Assert.True(WetnessRules.Surface(0.7f, 1f, 1f, 1f, 1f).Puddle > 0.9f);

        // Where the mask is high, not where it is low; and never on a slope.
        Assert.Equal(1f, WetnessRules.Surface(1f, 1f, 1f, 0.9f, 1f).Puddle);
        Assert.Equal(0f, WetnessRules.Surface(1f, 1f, 1f, 0.3f, 1f).Puddle);
        Assert.Equal(0f, WetnessRules.Surface(1f, 0.85f, 1f, 1f, 1f).Puddle);

        // Over a tile of the mask, more of it is puddle the wetter it is, and at soaked it is some of the ground,
        // not all of it.
        float Cover(float wetness)
        {
            int n = 0, under = 0;
            for (float x = 0; x < WetnessRules.MaskTile; x += 0.25f)
                for (float z = 0; z < WetnessRules.MaskTile; z += 0.25f, n++)
                    if (WetnessRules.Surface(wetness, 1f, 1f, PuddleMask.At(x, z), 1f).Puddle > 0.5f) under++;
            return (float)under / n;
        }
        float half = Cover(0.7f), soaked = Cover(1f);
        Assert.True(half > 0f && half < soaked, $"puddles grow: {half} then {soaked}");
        Assert.InRange(soaked, 0.05f, 0.5f);
    }

    [Fact]
    public void ThePuddleMaskTilesAndIsTheSameEveryRun()
    {
        Assert.Equal(PuddleMask.Make(WetnessRules.MaskSize), PuddleMask.Pixels);
        Assert.Equal(0, PuddleMask.Pixels.Min());
        Assert.Equal(255, PuddleMask.Pixels.Max());
        foreach (var (x, z) in new[] { (0.3f, 1.7f), (5.1f, 12.9f), (15.99f, 0.01f) })
        {
            Assert.Equal(PuddleMask.At(x, z), PuddleMask.At(x + WetnessRules.MaskTile, z), 4);
            Assert.Equal(PuddleMask.At(x, z), PuddleMask.At(x, z - 3 * WetnessRules.MaskTile), 4);
        }
    }

    // Only an opaque material wets, by its `weathering`; it reaches the shader as the `Weathering` parameter.
    [Fact]
    public void AMaterialOptsOutAndOnlyOpaqueOnesWet()
    {
        Assert.Equal(1f, WetnessRules.Weathering(new MaterialRecord()));
        Assert.Equal(0.5f, WetnessRules.Weathering(new MaterialRecord { Weathering = 0.5f }));
        Assert.Equal(0f, WetnessRules.Weathering(new MaterialRecord { Weathering = 0f }));
        Assert.Equal(0f, WetnessRules.Weathering(new MaterialRecord { Blend = MaterialBlend.AlphaBlend, Pass = RenderPass.Transparent }));
        Assert.Equal(1f, WetnessRules.Weathering(new MaterialRecord { Pass = RenderPass.AlphaTested }));

        Assert.True(MaterialSurface.TryGet(new MaterialRecord { Weathering = 0.25f }, MaterialSurface.Weathering, out var value));
        Assert.Equal(new[] { 0.25f }, value.Values);
    }

    // ---- the done criterion, on the Sandbox ----

    // Rain soaks the Sandbox's open ground and puddles it, the floor inside the hut stays dry (its lightmap says
    // it sees little sky), and once the rain stops it all dries.
    [Fact]
    public void RainWetsTheSandboxGroundOutdoorsButNotTheHutFloor_AndItDriesAfter()
    {
        string game = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
        using var app = HeadlessApp.ForGame(game, new global::Sandbox.SandboxModule()).WithEngineContent()
            .OnRegistered(a =>
            {
                a.Records.Register<WeatherRecord>();
                a.Records.Register<WeatherPatternRecord>();
            })
            .Boot();
        var world = app.World;
        var weather = world.Resources.Get<Weather>();
        world.RunFixed(Dt);

        // The hut: its level, its lightmap and the floor under its lamp.
        var level = world.Resources.Get<MapLevels>().Loaded.Single(l => l.Record == new RecordId("sandbox", "hut"));
        Assert.NotNull(level.Lightmap);
        var lamp = world.Query<PointLight, FromMap>().Entities.ToEntityList().First();
        var inside = world.Get<GlobalTransform>(lamp).Current.Position - level.Position;
        var floor = level.Brushes.SelectMany(b => b.Faces)
            .Where(f => f.Normal.Y > 0.99f && f.Positions[0].Y < inside.Y && Contains(f, inside))
            .OrderByDescending(f => f.Positions[0].Y).First();
        var onFloor = inside with { Y = floor.Positions[0].Y };
        float floorSky = level.Lightmap!.Sample(level.Lightmap.UvAt(floor, onFloor)).W;

        // The ground in the open, thirty metres from the hut, and the terrain under the hut is below its floor
        // (so no wet ground shows through it).
        var terrain = world.Resources.Get<Terrain>();
        var hut = onFloor + level.Position;
        Assert.True(terrain.HeightAt(hut.X, hut.Z) < hut.Y, "the hut's floor is above the ground");
        var outside = hut + new Vector3(30f, 0f, 0f);
        var ground = terrain.NormalAt(outside.X, outside.Z);

        (float Wet, float Puddle) Ground(float x, float z) =>
            WetnessRules.Surface(weather.Wetness, ground.Y, 1f, PuddleMask.At(x, z), 1f);
        (float Wet, float Puddle) Floor() =>
            WetnessRules.Surface(weather.Wetness, floor.Normal.Y, floorSky, PuddleMask.At(hut.X, hut.Z), 1f);

        Assert.Equal(0f, Ground(outside.X, outside.Z).Wet);

        weather.Set(new RecordId("sandbox", "rain"), 0f);
        Run(world, 60f, 0.25f);
        Assert.Equal(1f, weather.Wetness);
        Assert.True(Ground(outside.X, outside.Z).Wet > 0.8f, $"the open ground is wet: {Ground(outside.X, outside.Z).Wet}");
        Assert.True(floorSky < WetnessRules.ShelteredSky, $"the hut's floor sees {floorSky} of the sky");
        Assert.Equal((0f, 0f), Floor());

        // Somewhere near, the open ground has puddles.
        bool puddled = false;
        for (float dx = 0; dx < WetnessRules.MaskTile && !puddled; dx += 0.5f)
            for (float dz = 0; dz < WetnessRules.MaskTile && !puddled; dz += 0.5f)
                puddled = Ground(outside.X + dx, outside.Z + dz).Puddle > 0.9f;
        Assert.True(puddled, $"the open ground (normal y {ground.Y}) puddles somewhere in a tile of the mask");

        // The sun comes out, and in a few minutes it is dry again.
        weather.Set(new RecordId("sandbox", "clear"), 0f);
        Run(world, 30f, 0.25f);
        Assert.InRange(weather.Wetness, 0.8f, 0.9f);
        Run(world, 240f, 0.25f);
        Assert.Equal(0f, weather.Wetness);
        Assert.Equal((0f, 0f), Ground(outside.X, outside.Z));
    }

    private static bool Contains(LevelFace face, Vector3 point)
    {
        var min = face.Positions.Aggregate(Vector3.Min);
        var max = face.Positions.Aggregate(Vector3.Max);
        return point.X >= min.X && point.X <= max.X && point.Z >= min.Z && point.Z <= max.Z;
    }

    // A save from before this has no wetness: the world comes back dry.
    [Fact]
    public void AnOldWeatherIsDry()
    {
        var weather = System.Text.Json.JsonSerializer.Deserialize<Weather>(
            """{ "Current": "sage:clear", "Target": "sage:clear", "Blend": 1, "BlendRate": 1 }""")!;
        Assert.Equal(0f, weather.Wetness);
    }
}
#pragma warning restore SAGE0122, SAGE0130
