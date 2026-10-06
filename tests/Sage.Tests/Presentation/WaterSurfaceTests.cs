#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Water surfaces (issue #411): which surfaces a frame draws and whether the camera is under one (and
// that this agrees with the swim volume of #262), the ripples' normals, the curves water.fx draws with
// (Fresnel, depth fog, shore fade, the underwater view), the chain's water step, the record's and the
// part's checks, and the Sandbox's lake. All headless (WaterViews, WaterShading, PostChainPlan); the
// drawing is the client's: the smoke run exercises the step and Windows CI compiles water.fx.
public class WaterSurfaceTests
{
    public WaterSurfaceTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // A pool 10 m across and 4 m deep with its surface at y = 0, a puddle with no surface beside it, a
    // pond further off and two more lakes beyond, each with a surface.
    private const string Content = """
    [
      { "type": "water_surface", "id": "murky", "colour": [0.1, 0.1, 0.05], "fogDensity": 1.5,
        "underwaterColour": [0.2, 0.2, 0.1], "underwaterDensity": 0.5 },
      { "type": "prefab", "id": "pool_floor", "name": "pool_floor", "parts": { "body": { "size": [30, 1, 30] } } },
      { "type": "prefab", "id": "pool", "name": "pool", "parts": { "water": { "size": [10, 4, 10] }, "water_surface": "murky" } },
      { "type": "prefab", "id": "puddle", "name": "puddle", "parts": { "water": { "size": [4, 1, 4] } } },
      { "type": "prefab", "id": "pond", "name": "pond", "parts": { "water": { "size": [6, 2, 6] }, "water_surface": {} } },
      { "type": "prefab", "id": "swimmer", "name": "swimmer", "parts": { "character": { "layer": "player" } } },

      { "type": "scene", "id": "pool_scene", "place": [
          { "prefab": "pool_floor", "at": [0, -4.5, 0], "name": "floor" },
          { "prefab": "pool", "at": [0, -2, 0], "name": "pool" },
          { "prefab": "swimmer", "at": [0, 2, 0], "name": "swimmer" } ] },
      { "type": "scene", "id": "lakes", "place": [
          { "prefab": "pool", "at": [0, -2, 0], "name": "pool" },
          { "prefab": "puddle", "at": [20, -0.5, 0], "name": "puddle" },
          { "prefab": "pond", "at": [-30, -1, 0], "name": "pond" },
          { "prefab": "pond", "at": [0, -1, 60], "name": "far pond" },
          { "prefab": "pond", "at": [0, -1, -45], "name": "south pond" },
          { "prefab": "pond", "at": [45, -1, 45], "name": "east pond" },
          { "prefab": "pool", "at": [0, -1, 500], "name": "very far" } ] }
    ]
    """;

    private static HeadlessApp Boot(string scene)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().Mount(files).StartScene("game:" + scene).Boot();
        app.World.RunFixed(Dt);
        return app;
    }

    // ---- which water, and under it ----

    // The surfaces a frame draws: the volumes with a surface, nearest first across the ground, at most
    // four, none past the view's reach; a volume with none (the puddle) is not drawn.
    [Fact]
    public void TheNearestSurfacesInReach_AreDrawn_NearestFirst()
    {
        using var app = Boot("lakes");
        Assert.Equal(0, app.Records.ErrorCount);
        var water = new WaterViews(app.World, app.Records);
        var planes = new WaterPlane[WaterViews.MaxSurfaces];

        int n = water.Collect(new Vector3(0, 10, 0), 200f, planes);
        Assert.Equal(WaterViews.MaxSurfaces, n);
        var names = planes.Take(n).Select(p => p.Volume.Name).ToArray();
        Assert.Equal(new[] { "pool", "pond", "south pond", "far pond" }, names);   // the east pond (59 m) is the fifth
        Assert.Equal(0f, planes[0].Distance);                                       // standing over the pool
        Assert.Equal(27f, planes[1].Distance, 3);                                   // the pond's near edge
        Assert.DoesNotContain("puddle", names);

        // The pool's top: its footprint and its surface and floor heights, and its look.
        Assert.Equal(new Vector2(-5, -5), planes[0].Min);
        Assert.Equal(new Vector2(5, 5), planes[0].Max);
        Assert.Equal(0f, planes[0].Surface);
        Assert.Equal(-4f, planes[0].Floor);
        Assert.Equal(1.5f, planes[0].Look.FogDensity);

        // A pond naming no look is drawn with the engine's (here, with no engine content, the defaults).
        Assert.Equal(new WaterSurfaceRecord().FogDensity, planes[1].Look.FogDensity);

        // A short reach leaves out what is beyond it.
        Assert.Equal(1, water.Collect(new Vector3(0, 10, 0), 20f, planes));
    }

    // Under water is inside a volume's box, below its surface and above its floor; the puddle with no
    // surface is water too, with the engine's look.
    [Fact]
    public void UnderWater_IsInsideTheBoxBelowItsSurface()
    {
        using var app = Boot("lakes");
        var water = new WaterViews(app.World, app.Records);
        var pool = app.World.FindByName("pool");

        Assert.True(water.Under(new Vector3(1, -0.5f, 2), out var under));
        Assert.Equal(pool, under.Volume);
        Assert.Equal(0.5f, under.Look.UnderwaterDensity);   // the pool's own look, "murky"

        Assert.False(water.Under(new Vector3(1, 0.1f, 2), out _));    // above the surface
        Assert.False(water.Under(new Vector3(1, -4.1f, 2), out _));   // below the floor
        Assert.False(water.Under(new Vector3(6, -1f, 2), out _));     // beside the box

        Assert.True(water.Under(new Vector3(20, -0.2f, 0), out var puddle));
        Assert.Equal(app.World.FindByName("puddle"), puddle.Volume);
        Assert.Equal(new WaterSurfaceRecord().UnderwaterDensity, puddle.Look.UnderwaterDensity);
    }

    // The view goes under exactly when the swim volume says the swimmer's eyes are: floating with its head
    // out, the eye is above the water and the view is not tinted; diving, both are under.
    [Fact]
    public void TheViewGoesUnderWhereTheSwimmerDoes()
    {
        using var app = Boot("pool_scene");
        var world = app.World;
        var swimmer = world.FindByName("swimmer");
        var water = new WaterViews(world, app.Records);
        var profile = MovementProfileRecord.Fallback;

        // The controller reads the water where the swimmer stood when the tick began, before it moves it:
        // the view is asked at that eye.
        Vector3 before = default;
        bool Agrees(out bool under)
        {
            var character = world.Get<CharacterController>(swimmer);
            under = water.Under(CharacterController.EyeOf(before, character, profile), out _);
            return under == character.Underwater;
        }
        void Tick()
        {
            before = world.Get<Transform>(swimmer).LocalPosition;
            world.RunFixed(Dt);
        }

        // In it falls, and floats: every tick the two agree.
        bool everUnder = false;
        for (int i = 0; i < 360; i++)
        {
            Tick();
            Assert.True(Agrees(out bool under), $"tick {i}: the view and the swimmer disagree");
            everUnder |= under;
        }
        Assert.True(everUnder, "the splash carries its eyes under");
        Assert.True(world.Get<CharacterController>(swimmer).Swimming);
        Assert.True(Agrees(out bool floating) && !floating, "floating, its head is out");

        // Diving: under, and agreeing all the way down.
        var crouch = default(ActionMask).With(app.Engine.Actions.Get("Crouch"));
        for (int i = 0; i < 60; i++)
        {
            world.Get<PawnIntent>(swimmer).Held = crouch;
            Tick();
            Assert.True(Agrees(out _), $"diving, tick {i}: the view and the swimmer disagree");
        }
        Assert.True(Agrees(out bool dived) && dived, "dived, the view is under");
    }

    // ---- the surface ----

    private static WaterSurfaceRecord Look(float strength = 0.12f, float speed = 0.8f, float direction = 30f) =>
        new() { WaveStrength = strength, WaveSpeed = speed, WaveDirection = direction, WaveLength = 4f };

    // The ripples tilt the surface by at most their strength, move with time at their speed, and stand
    // still (or vanish) when those are 0.
    [Fact]
    public void TheRipplesTiltTheSurfaceAndMoveWithTime()
    {
        var look = Look();
        float maxTilt = 0f;
        bool moved = false;
        for (int i = 0; i < 200; i++)
        {
            float x = i * 0.37f, z = i * -0.21f;
            var n = WaterShading.Normal(look, x, z, 3f);
            Assert.Equal(1f, n.Length(), 4);
            Assert.True(n.Y > 0f);
            float slope = MathF.Sqrt(n.X * n.X + n.Z * n.Z) / n.Y;
            maxTilt = MathF.Max(maxTilt, slope);
            Assert.True(slope <= look.WaveStrength + 1e-4f, $"the slope {slope} is more than the strength");
            moved |= Vector3.Distance(n, WaterShading.Normal(look, x, z, 3.5f)) > 1e-3f;
        }
        Assert.True(maxTilt > look.WaveStrength * 0.3f, $"the surface ripples ({maxTilt})");
        Assert.True(moved, "the ripples travel");

        Assert.Equal(Vector3.UnitY, WaterShading.Normal(Look(strength: 0f), 1.3f, 2.7f, 5f));
        var still = Look(speed: 0f);
        Assert.Equal(WaterShading.Normal(still, 1.3f, 2.7f, 0f), WaterShading.Normal(still, 1.3f, 2.7f, 9f));
    }

    // The direction turns the whole pattern: the ripples of a look facing 90 degrees at a point are those
    // of one facing 0 at the point turned back by 90 degrees, turned.
    [Fact]
    public void TheDirectionTurnsTheRipples()
    {
        Assert.Equal(1f, WaterShading.Direction(Look(direction: 0f)).X, 5);
        Assert.Equal(1f, WaterShading.Direction(Look(direction: 90f)).Y, 5);
        var east = Look(direction: 0f);
        var north = Look(direction: 90f);
        for (int i = 0; i < 20; i++)
        {
            float x = i * 0.53f - 4f, z = i * 0.31f - 2f, t = i * 0.1f;
            var turned = WaterShading.Normal(north, x, z, t);
            var original = WaterShading.Normal(east, z, -x, t);   // (x, z) turned back by 90 degrees
            // The original's slope (gx, gz), turned by +90 degrees: (-gz, gx).
            Assert.Equal(-original.Z, turned.X, 4);
            Assert.Equal(original.X, turned.Z, 4);
        }
    }

    // Fresnel: 2% head-on, `reflectivity` at a grazing angle, more the flatter the eye looks.
    [Fact]
    public void TheReflectionGrowsTowardAGrazingAngle()
    {
        Assert.Equal(0.02f, WaterShading.Fresnel(1f, 1f), 5);
        Assert.Equal(1f, WaterShading.Fresnel(0f, 1f), 5);
        Assert.Equal(0.5f, WaterShading.Fresnel(0f, 0.5f), 5);
        float last = 1f;
        for (float c = 0.05f; c <= 1f; c += 0.05f)
        {
            float f = WaterShading.Fresnel(c, 1f);
            Assert.True(f < last, $"Fresnel rises toward the grazing angle ({c}: {f})");
            last = f;
        }
    }

    // Through the water the bottom fades into its colour with the metres the eye looks through; at the
    // shore the surface fades out where the bottom comes up to it.
    [Fact]
    public void DepthFogsTheBottomAndTheShoreFadesTheEdge()
    {
        Assert.Equal(0f, WaterShading.DepthFog(0.35f, 0f));
        Assert.Equal(1f - MathF.Exp(-0.7f), WaterShading.DepthFog(0.35f, 2f), 5);
        Assert.True(WaterShading.DepthFog(0.35f, 30f) > 0.99f);
        Assert.Equal(0f, WaterShading.DepthFog(0f, 30f));   // clear water hides nothing

        Assert.Equal(0f, WaterShading.ShoreFade(0.6f, 0f));
        Assert.Equal(0.5f, WaterShading.ShoreFade(0.6f, 0.3f), 5);
        Assert.Equal(1f, WaterShading.ShoreFade(0.6f, 0.6f));
        Assert.Equal(1f, WaterShading.ShoreFade(0.6f, 5f));
        Assert.Equal(0f, WaterShading.ShoreFade(0.6f, -1f));   // the bank above the waterline
        Assert.Equal(1f, WaterShading.ShoreFade(0f, 0.01f));   // no fade: a hard edge
        Assert.Equal(0f, WaterShading.ShoreFade(0f, 0f));
    }

    // Under water: right in front of the eye a thing is only tinted; far off, everything is the water's
    // colour in the sky's light, which is dark at night.
    [Fact]
    public void UnderWater_TheViewIsTintedAndFogged()
    {
        var look = new WaterSurfaceRecord();
        var red = new Vector3(1f, 0f, 0f);
        var day = WaterShading.Light(new Vector3(0.45f, 0.47f, 0.52f), new Vector3(1f, 0.95f, 0.85f), Vector3.Normalize(new Vector3(0.3f, -1f, 0.2f)));
        Assert.Equal(red * look.UnderwaterTint, WaterShading.Underwater(look, red, 0f, day));
        var far = WaterShading.Underwater(look, red, 200f, day);
        Assert.True(Vector3.Distance(far, look.UnderwaterColour * day) < 1e-3f, $"far off it is the water's colour, not {far}");
        var near = WaterShading.Underwater(look, red, 2f, day);
        Assert.True(near.X < red.X * look.UnderwaterTint.X && near.X > far.X);

        Assert.Equal(Vector3.One, day);   // a bright day lights the water fully
        var night = WaterShading.Light(new Vector3(0.02f, 0.03f, 0.08f), Vector3.Zero, new Vector3(0f, 1f, 0f));
        Assert.Equal(new Vector3(0.02f, 0.03f, 0.08f), night);
        Assert.True(WaterShading.Underwater(look, red, 200f, night).Length() < 0.02f, "dark at night");
    }

    // The ray meets a surface's top from above or below, inside its footprint; it misses beside it, going
    // away from it, or running flat.
    [Fact]
    public void TheViewRayMeetsTheSurfaceInsideItsFootprint()
    {
        var plane = new WaterPlane { Min = new Vector2(-5, -5), Max = new Vector2(5, 5), Surface = 0f, Floor = -4f };
        var down = Vector3.Normalize(new Vector3(1f, -1f, 0f));
        Assert.Equal(MathF.Sqrt(2f) * 2f, WaterShading.Hit(plane, new Vector3(0, 2, 0), down), 4);
        Assert.Equal(float.PositiveInfinity, WaterShading.Hit(plane, new Vector3(0, 10, 0), down));      // lands at x = 10: beside it
        Assert.Equal(float.PositiveInfinity, WaterShading.Hit(plane, new Vector3(0, 2, 0), -down));     // looking up, from above
        Assert.Equal(2f, WaterShading.Hit(plane, new Vector3(1, -2, 1), Vector3.UnitY), 5);             // from below
        Assert.Equal(float.PositiveInfinity, WaterShading.Hit(plane, new Vector3(0, 2, 0), Vector3.UnitX));
    }

    // ---- the chain ----

    // The water step comes after the tonemap and before the post_effects, so a grade falls on the water;
    // it reads the scene's depth; and water alone turns the chain on (one step, the scene to the screen).
    [Fact]
    public void TheWaterStep_ComesBeforeTheEffects_AndReadsDepth()
    {
        var options = new PostOptions(1f, Hdr: true, Water: true, Aa: AntiAliasing.Fxaa);
        var steps = new PostStep[PostChainPlan.Capacity(2)];
        int n = PostChainPlan.Plan(new[] { true, true }, options, steps);
        Assert.Equal(new[] { PostStepKind.Tonemap, PostStepKind.Water, PostStepKind.Effect, PostStepKind.Effect, PostStepKind.Fxaa },
                     steps.Take(n).Select(s => s.Kind));
        Assert.Equal(PostTarget.Post0, steps[1].Source);   // it reads what the tonemap wrote
        Assert.Equal(PostTarget.Screen, steps[n - 1].Destination);

        n = PostChainPlan.Plan(ReadOnlySpan<bool>.Empty, new PostOptions(Water: true), steps);
        Assert.Equal(1, n);
        Assert.Equal(new PostStep(-1, PostTarget.Scene, PostTarget.Screen, PostStepKind.Water), steps[0]);

        Assert.True(PostChainPlan.NeedsDepth(ReadOnlySpan<bool>.Empty, ReadOnlySpan<bool>.Empty, water: true));
        Assert.False(PostChainPlan.NeedsDepth(ReadOnlySpan<bool>.Empty, ReadOnlySpan<bool>.Empty, water: false));

        // Everything at once fits the capacity.
        var all = new PostOptions(1f, true, PostChainPlan.MaxBloomLevels, AntiAliasing.Fxaa, true);
        var full = new PostStep[PostChainPlan.Capacity(3)];
        Assert.Equal(3 + 2 * PostChainPlan.MaxBloomLevels - 1 + 3, PostChainPlan.Plan(new[] { true, true, true }, all, full));
    }

    // ---- content ----

    // The engine's look and the step's material load: water.fx's technique and the reflection's params.
    [Fact]
    public void TheEnginesWaterLoads()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent()
            .OnRegistered(a => a.Records.Register<MaterialRecord>())
            .Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var look = app.Records.Get<WaterSurfaceRecord>(WaterViews.DefaultLook);
        Assert.True(look.WaveStrength > 0f && look.FogDensity > 0f && look.UnderwaterDensity > 0f);

        var material = app.Records.Get<MaterialRecord>(new RecordId("sage", "post_water"));
        Assert.Equal("shaders/water.mgfxo", material.Effect.ToString());
        Assert.Equal("Water", material.Technique);
        Assert.False(material.DepthTest);
        foreach (var param in new[] { "ReflectionDistance", "ReflectionThickness", "ScreenReflections" })
            Assert.True(material.Params.ContainsKey(param), $"sage:post_water has no {param}");

        // Every parameter water.fx declares is the material's or one the engine sets (GL has no .fx defaults,
        // so one that is neither would leave the material unbuilt), and the engine sets nothing it lacks.
        string fx = File.ReadAllText(Path.Combine(TestEnv.FolderAbove("Sage.sln"), "engine_content", "shaders", "water.fx"));
        var declared = System.Text.RegularExpressions.Regex.Matches(fx, @"^(?:float[0-9x]*|texture)\s+(\w+)\s*(?:\[\d+\])?\s*;", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet();
        var engine = WaterShading.EngineParams.Concat(new[] { "Source", "SceneDepth" }).ToHashSet();
        foreach (var param in declared)
            Assert.True(engine.Contains(param) || material.Params.ContainsKey(param), $"water.fx's {param} is neither the engine's nor sage:post_water's");
        foreach (var param in WaterShading.EngineParams)
            Assert.Contains(param, declared);
    }

    // A look's numbers are checked as it loads, each an error at its line.
    [Fact]
    public void ABadLook_IsALoadError()
    {
        using var log = new CaptureSink();
        using var app = HeadlessApp.Gameplay()
            .File("data/water.json", """
                [{ "type": "water_surface", "id": "bad", "fogDensity": -1, "reflectivity": 2, "waveLength": 0,
                   "waveStrength": 3, "colour": [-1, 0, 0], "refraction": 0.5 }]
                """)
            .Boot();
        var lines = log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message).Where(m => m.Contains("water_surface")).ToArray();
        foreach (var word in new[] { "fogDensity", "reflectivity", "waveLength", "waveStrength", "colour", "refraction" })
            Assert.Contains(lines, l => l.Contains(word));
        Assert.True(app.Records.ErrorCount >= 6);
    }

    // A surface needs a volume to be the top of; a look it names must exist.
    [Fact]
    public void ASurfaceWithoutAVolume_OrALookThatIsNot_IsAnError()
    {
        using var log = new CaptureSink();
        string id = "floating_" + Guid.NewGuid().ToString("N")[..8];
        using var app = HeadlessApp.Gameplay()
            .File("data/water.json", $$"""
                [{ "type": "prefab", "id": "{{id}}", "name": "{{id}}", "parts": { "water_surface": {} } },
                 { "type": "prefab", "id": "{{id}}_lost", "parts": { "water": { "size": [2, 2, 2] }, "water_surface": "nowhere" } },
                 { "type": "scene", "id": "s", "place": [ { "prefab": "{{id}}", "at": [0, 0, 0] } ] }]
                """)
            .StartScene("sage:s")
            .Boot();
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Error && e.Message.Contains(id) && e.Message.Contains("water volume"));
        Assert.Contains(log.Entries, e => e.Level >= LogLevel.Error && e.Message.Contains("nowhere"));
        var floating = app.World.FindByName(id);
        Assert.False(app.World.Has<WaterSurface>(floating));
    }

    // The Sandbox's lake: a water volume with the lake's surface in the basin east of the clearing, its
    // surface above the basin's floor and below the ground at its far edges, so it has a shore all round;
    // a camera over it sees it, and one dropped into it is under.
    [Fact]
    public void TheSandboxHasALake()
    {
        string sandbox = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
        using var app = HeadlessApp.ForGame(sandbox, new global::Sandbox.SandboxModule()).WithEngineContent().Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var lake = world.FindByName("lake");
        Assert.False(lake.IsNull);
        Assert.Equal(new RecordId("sandbox", "lake"), world.Get<WaterSurface>(lake).Look);

        var water = new WaterViews(world, app.Records);
        var planes = new WaterPlane[WaterViews.MaxSurfaces];
        var centre = world.Get<Transform>(lake).LocalPosition;
        Assert.Equal(1, water.Collect(centre with { Y = 0f }, 1000f, planes));
        var plane = planes[0];
        Assert.Equal(lake, plane.Volume);
        Assert.Equal(0.25f, plane.Look.FogDensity);   // sandbox:lake's

        // A basin: the deepest ground under it is metres below the surface and above its floor, and every
        // edge of the box is dry ground, above the water.
        var terrain = world.Resources.Get<Terrain>();
        float deepest = float.MaxValue;
        for (float x = plane.Min.X; x <= plane.Max.X; x += 2f)
            for (float z = plane.Min.Y; z <= plane.Max.Y; z += 2f)
                deepest = MathF.Min(deepest, terrain.HeightAt(x, z));
        Assert.True(deepest < plane.Surface - 4f && deepest > plane.Floor, $"the basin's floor ({deepest}) is under the water and inside the box");
        for (float s = 0f; s <= 1f; s += 0.05f)
        {
            Assert.True(terrain.HeightAt(plane.Min.X + s * (plane.Max.X - plane.Min.X), plane.Min.Y) > plane.Surface, "the north edge is shore");
            Assert.True(terrain.HeightAt(plane.Min.X + s * (plane.Max.X - plane.Min.X), plane.Max.Y) > plane.Surface, "the south edge is shore");
            Assert.True(terrain.HeightAt(plane.Min.X, plane.Min.Y + s * (plane.Max.Y - plane.Min.Y)) > plane.Surface, "the west edge is shore");
            Assert.True(terrain.HeightAt(plane.Max.X, plane.Min.Y + s * (plane.Max.Y - plane.Min.Y)) > plane.Surface, "the east edge is shore");
        }

        // Over the deepest part a camera a metre under is under the lake, and one a metre over is not.
        var spot = new Vector3(centre.X, plane.Surface, centre.Z);
        Assert.True(water.Under(spot - Vector3.UnitY, out var under));
        Assert.Equal(lake, under.Volume);
        Assert.Equal(0.15f, under.Look.UnderwaterDensity);
        Assert.False(water.Under(spot + Vector3.UnitY, out _));

        // Far from the play area: the clearing's things are more than 60 m from its edge.
        foreach (var name in new[] { "smith", "hermit", "hut path", "crypt_door", "road_sign" })
            Assert.True(WaterViews.Across(plane, world.Get<Transform>(world.FindByName(name)).LocalPosition) > 60f, $"{name} is far from the lake");
    }
}
