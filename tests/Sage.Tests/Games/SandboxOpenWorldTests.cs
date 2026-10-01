#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The Sandbox's open world from its real content, headless (issue 4g-7; games/Sandbox/content/data/
// open_world.json): the crypt behind the door on the hut's hill is an interior lit by its torches, the smith
// keeps his forge's hours, the road leads to the wilds, which stream by sector, and the kit's map travels
// between waystones. What the client adds — drawing the crypt by its torches and the rest screen — is the
// smoke run's.
public class SandboxOpenWorldTests
{
    public SandboxOpenWorldTests() { _ = TestEnv.UserRoot; }

    private static RecordId Sandbox(string name) => new("sandbox", name);

    private static HeadlessApp Boot(double hour)
    {
        var app = SandboxScreensTests.Boot();
        var clock = WorldClock.Of(app.World);
        clock.Scale = 0;                                                  // the test sets the hour
        clock.Hour = hour;
        Step(app.World, 3);
        return app;
    }

    private static void Step(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunFixed(1f / 60f);
            world.RunFrame(1f / 60f, 1f);
        }
    }

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static Vector3 Absolute(World world, Entity entity) => world.Origin().ToAbsolute(world.Get<Transform>(entity).LocalPosition);

    private static float ApartXZ(World world, Entity a, Entity b) =>
        SageMath.DistanceXZ(world.Get<Transform>(a).LocalPosition, world.Get<Transform>(b).LocalPosition);

    private static float ApartXZ(Vector3 a, Vector3 b) => SageMath.DistanceXZ(a, b);

    // The crypt: sleeping is refused at the start (the watcher is nine metres off), the door on the hill
    // takes you into an interior with no sun and no terrain, lit by three torches, where sleeping is allowed;
    // and the way out puts you on the steps with the clock where the night left it, not back at the main
    // scene's dusk.
    [Xunit.Fact]
    public void TheCryptIsAnInteriorLitByItsTorchesAndTheWayOutKeepsTheClock()
    {
        using var app = Boot(hour: 18.5);
        var world = app.World;
        var clock = WorldClock.Of(world);
        var player = SandboxScreensTests.Player(world);
        var environment = world.Resources.Get<RenderEnvironment>();

        Assert.False(Rest.Can(world, player, RestKind.Sleep, 8, out string reason));
        Assert.Equal("@rpg.rest.enemies", reason);
        Assert.Equal("watcher", Rest.EnemyNear(world, player, 25f).Name);
        Assert.True(Rest.Can(world, player, RestKind.Wait, 8, out _));

        Assert.True(Travel.Use(world, One(world, "crypt_door"), player));
        Step(world, 2);
        Assert.Equal(Sandbox("crypt"), Scenes.Current(world));
        Assert.Empty(world.Resources.Get<Terrain>().Sectors);
        Assert.Equal(Vector3.Zero, environment.SunColor);
        Assert.False(environment.DrawSky);
        Assert.Equal(new Vector3(0.05f, 0.045f, 0.05f), environment.AmbientSky);
        var torches = world.Query<PointLight>().Entities.ToEntityList().Where(e => e.GetComponent<PointLight>().Lit).ToList();
        Assert.Equal(3, torches.Count);
        Assert.All(torches, t => Assert.Equal("torch", t.Name));
        Assert.Empty(Named(world, "watcher"));                              // the clearing sleeps
        // At the map's `crypt_in` (0, -256, 8 in Quake units, 32 to the metre: 8 m south of the map's place).
        Assert.True(ApartXZ(new Vector3(512, 0, 6664), Absolute(world, player)) < 0.5f, $"the player is at {Absolute(world, player)}");

        Assert.True(Rest.Begin(world, player, RestKind.Sleep, 8, out _));  // no enemy in the crypt
        Step(world);
        Assert.Equal(2.5, clock.Hour, 3);

        Assert.True(Travel.Use(world, One(world, "crypt_exit"), player));
        Step(world, 2);
        Assert.Equal(Sandbox("main"), Scenes.Current(world));
        Assert.True(ApartXZ(world, player, One(world, "crypt_steps")) < 0.5f);
        Assert.Equal(2.5, clock.Hour, 3);                                  // not the scene's 18:30 again
        Assert.True(environment.DrawSky);
        Assert.NotEmpty(world.Resources.Get<Terrain>().Sectors);
        Assert.NotEmpty(Named(world, "watcher"));
        One(world, "smith");
    }

    // The smith: at the forge at dusk, and when two hours pass (to 20:30) he is home, where his routine has
    // him from eight — he had half an hour to walk eight metres.
    [Xunit.Fact]
    public void TheSmithKeepsTheHoursOfHisForge()
    {
        using var app = Boot(hour: 18.5);
        var world = app.World;
        var smith = One(world, "smith");
        Step(world, 60);
        Assert.True(ApartXZ(world, smith, One(world, "forge")) < 1.5f, $"he is {ApartXZ(world, smith, One(world, "forge")):F1} m from the forge");

        Assert.True(Time.Pass(world, 2, "wait"));
        Step(world, 2);
        Assert.True(ApartXZ(world, smith, One(world, "smith_home")) < 0.5f, $"he is {ApartXZ(world, smith, One(world, "smith_home")):F1} m from home");
        Step(world, 60);
        Assert.True(ApartXZ(world, smith, One(world, "smith_home")) < 1.5f);
    }

    // The road sign takes two hours to the wilds, a scene placed sector by sector: the camp a sector east is
    // there on arrival, the watcher two sectors east is not until you go past it, and then the road behind
    // you sleeps. Both waystones walked past are on the kit's map; the one asleep is a marker of its own, and
    // choosing the clearing from the list travels back, costing the distance over travel_speed.
    [Xunit.Fact]
    public void TheRoadLeadsToTheWildsWhichStreamAndTheMapTravelsBack()
    {
        using var app = Boot(hour: 9);
        var world = app.World;
        var clock = WorldClock.Of(world);
        var player = SandboxScreensTests.Player(world);
        var log = TravelLog.Of(world);
        Assert.Equal("Clearing", Assert.Single(log.Points).Label);          // the stone by the start

        Assert.True(Travel.Use(world, One(world, "road_sign"), player));
        Step(world, 4);
        Assert.Equal(Sandbox("wilds"), Scenes.Current(world));
        Assert.Equal(11, clock.Hour, 3);                                   // the road takes two hours
        Assert.True(ApartXZ(world, player, One(world, "wilds_start")) < 0.5f);
        One(world, "camp hopper");                                         // sector (5, 0): in the ring
        Assert.Empty(Named(world, "far watcher"));                         // sector (6, 0): not yet
        Assert.Empty(Named(world, "smith"));                               // the clearing sleeps
        Assert.Equal(new[] { "Clearing", "Old Road" }, log.Points.Select(p => p.Label).ToArray());

        world.Get<Transform>(player).LocalPosition = world.Origin().ToOrigin(new Vector3(7300, 30, 500));
        Step(world, 6);
        One(world, "far watcher");
        Assert.Empty(Named(world, "old_road_stone"));                      // sector (4, 0) asleep now, three behind

        var stack = SandboxScreensTests.Stack(world);
        var layer = stack.Open(new RecordId("sandbox", "map"), new UiBindContext(world, player));
        stack.Update(UiInput.Wait(0f));
        var map = Assert.IsType<MapView>(layer.Screen!.ViewModel);
        var road = Assert.Single(map.Markers, m => m.Label == "Old Road");  // asleep, so a marker of its own
        Assert.True(road.CanTravel);
        Assert.True(road.Entity.IsNull);
        Assert.Equal(MapView.TravelStyle, road.Style);
        Assert.True(road.X < 0.5f);                                        // west of the player
        Assert.DoesNotContain(map.Markers, m => m.Label == "Clearing");    // another scene: listed, not placed
        Assert.Equal(new[] { "Clearing", "Old Road" }, map.Destinations.Select(d => d.Label).ToArray());
        var clearing = map.Destinations[0];
        double hours = Travel.HoursTo(world, clearing.Point!);
        Assert.InRange(hours, 1.25, 1.45);                                  // about 6.8 km at 5 km an hour
        Assert.Equal(hours.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), clearing.TravelHours);

        var list = (ItemList)layer.Content.Find("destinations")!;
        var button = Enumerable.Range(0, list.ChildCount).Select(list.Child).Single(r => r.Data == clearing);
        Assert.Contains("Clearing", ((Label)button).Text);
        double from = clock.Elapsed;
        stack.Update(UiInput.Click(layer.Root.ToPixels(new Vector2(button.Rect.X + button.Rect.Width * 0.5f, button.Rect.Y + button.Rect.Height * 0.5f))));
        Step(world, 3);
        Assert.Equal(Sandbox("main"), Scenes.Current(world));
        Assert.Equal(from + hours, clock.Elapsed, 3);
        Assert.True(ApartXZ(world, player, One(world, "clearing_stone")) < 2f);
        One(world, "smith");
        Assert.Empty(Named(world, "far watcher"));
    }
}
