#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Engine-owned scenes (issue #29): the `scene` record, the start scene game.json names, the spawn on
// world start, respawn on hot reload that keeps the player, `scene_load`, and the one placement format
// shared with the editor's `placements` documents.
public class SceneTests
{
    public SceneTests() { _ = TestEnv.UserRoot; }

    private static string SceneOnlyGame => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "scene-only");

    private static Entity[] FromScene(World world) =>
        world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList().ToArray();

    private static Entity[] Players(World world) =>
        world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList().ToArray();

    private static Vector3 At(Entity entity) => entity.GetComponent<Transform>().LocalPosition;

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");

    // The acceptance test: a game that is only game.json and records — no assembly, no IGameModule —
    // boots into its scene with its prefabs placed and a player to control.
    [Fact]
    public void AGameWithNoCodeBootsIntoItsScene()
    {
        using var app = HeadlessApp.ForGame(SceneOnlyGame).Boot();
        var world = app.World;

        Assert.Null(app.Engine.Modules.Game);                        // no C# of its own
        Assert.Equal(new RecordId("sceneonly", "yard"), app.Engine.Scenes.Start);
        Assert.Equal(new RecordId("sceneonly", "yard"), world.Resources.Get<ActiveScene>().Id);
        Assert.IsType<DefaultGameRules>(world.Resources.Get<GameRules>());

        Assert.Equal(4, FromScene(world).Length);                    // the floor and three crates
        Assert.Contains(FromScene(world), e => e.Name == "crate by the door");

        var player = Assert.Single(Players(world));
        Assert.True(world.Has<CharacterController>(player));
        Assert.False(player.Tags.Has<FromScene>());                  // the rules', not the scene's
        Near(new Vector3(100, 1, 106), At(player));                  // "origin" + "at"
        Near(new Vector3(100.7f, 1.5f, 100), At(FromScene(world).First(e => At(e).Y > 1f)));

        for (int i = 0; i < 30; i++) world.RunFixed(1f / 60f);       // and it runs
        Assert.True(world.IsAlive(player));
    }

    // `sage validate` on it is clean: every placement's prefab exists, and the start scene is a scene.
    [Fact]
    public void AGameWithNoCodeValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = SceneOnlyGame,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
    }

    private const string Content = """
    [
      { "type": "prefab", "id": "rock", "name": "rock" },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
      { "type": "scene", "id": "main",
        "player": { "prefab": "hero", "at": [0, 1, 0] },
        "place": [ { "prefab": "rock", "at": [2, 0, 0] }, { "prefab": "rock", "at": [-2, 0, 0] } ] },
      { "type": "scene", "id": "cave", "origin": [50, 0, 0], "relativeTo": "Origin",
        "player": { "prefab": "hero", "at": [0, 0, 5] },
        "place": [ { "prefab": "rock", "at": [0, 0, 0] } ] }
    ]
    """;

    private static (HeadlessApp App, MountFixture Files) NewGame(string content = Content)
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", content);
        files.Mount("game", "game");
        return (HeadlessApp.Bare().Mount(files).StartScene("game:main").Boot(), files);
    }

    // Hot reload places the scene again from the new records: the old entities go, none are doubled,
    // and the player is the *same* entity, where it was — however many times the file is saved.
    [Fact]
    public void HotReloadRespawnsTheSceneWithoutDuplicatingOrLosingThePlayer()
    {
        var (app, files) = NewGame();
        using (app)
        {
            var world = app.World;
            var player = Assert.Single(Players(world));
            world.Teleport(player, Transform.At(new Vector3(7, 1, 7)));   // walked somewhere
            var before = FromScene(world);
            Assert.Equal(2, before.Length);

            app.Records.Reload();
            app.Records.Reload();

            Assert.Equal(2, FromScene(world).Length);
            Assert.True(before.All(e => !world.IsAlive(e)), "the scene should have been rebuilt, not kept");
            Assert.Equal(player, Assert.Single(Players(world)));
            Assert.Equal(new Vector3(7, 1, 7), At(player));

            // An edit: a third rock. It arrives on the reload; the player still does not double.
            files.Write("game", "data/scene.json", Content.Replace(
                "{ \"prefab\": \"rock\", \"at\": [-2, 0, 0] }",
                "{ \"prefab\": \"rock\", \"at\": [-2, 0, 0] }, { \"prefab\": \"rock\", \"at\": [0, 0, 9], \"name\": \"new\" }"));
            app.Records.Reload();

            Assert.Equal(3, FromScene(world).Length);
            Assert.Equal(player, Assert.Single(Players(world)));
        }
    }

    // A player that is gone by the reload (destroyed, or never spawned) comes back from the scene.
    [Fact]
    public void HotReloadPutsBackAPlayerThatIsGone()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            world.Destroy(Assert.Single(Players(world)));

            app.Records.Reload();

            Assert.Equal(new Vector3(0, 1, 0), At(Assert.Single(Players(world))));
        }
    }

    // `scene_load <id>`: another scene in place of this one; the player stays and moves to its start.
    [Fact]
    public void SceneLoadReplacesTheSceneAndMovesThePlayer()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            var player = Assert.Single(Players(world));

            app.CVars.Execute("sv_cheats 1");   // like map_load, it is a cheat
            app.CVars.Execute("scene_load cave");

            Assert.Equal(new RecordId("game", "cave"), world.Resources.Get<ActiveScene>().Id);
            var placed = Assert.Single(FromScene(world));
            Assert.Equal(new Vector3(50, 0, 0), At(placed));
            Assert.Equal(player, Assert.Single(Players(world)));
            Assert.Equal(new Vector3(50, 0, 5), At(player));
            Assert.Equal(new Vector3(50, 0, 5), world.PlayerStart());

            // And a reload now respawns the cave, not the scene the world started in.
            app.Records.Reload();
            Assert.Equal(new Vector3(50, 0, 0), At(Assert.Single(FromScene(world))));
        }
    }

    // game.json's "scene" must name a scene: a typo is a load error, not a world that starts empty.
    [Fact]
    public void AStartSceneThatIsNotAScene_IsALoadError()
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", Content);
        files.Mount("game", "game");
        var ex = Assert.Throws<InvalidDataException>(() => HeadlessApp.Bare().Mount(files).StartScene("game:mian").Build());
        Assert.Contains("game:mian", ex.Message);
        Assert.Contains("game:main", ex.Message);   // it lists the scenes there are
    }

    // A placement with no prefab is a content error at its line, like any other.
    [Fact]
    public void APlacementWithNoPrefab_IsAContentError()
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", """[ { "type": "scene", "id": "main", "place": [ { "at": [1, 2, 3] } ] } ]""");
        files.Mount("game", "game");
        using var log = new CaptureSink();
        using var app = HeadlessApp.Bare().Mount(files).Build();
        Assert.Equal(1, app.Records.ErrorCount);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("scene game:main: a placement needs a \"prefab\""));
    }

    // No start scene (a test, a game whose rules place everything): worlds start empty, as before.
    [Fact]
    public void WithNoStartSceneAWorldStartsEmpty()
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", Content);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot();
        Assert.Empty(FromScene(app.World));
        Assert.Empty(Players(app.World));
        Assert.True(app.World.Resources.Get<ActiveScene>().Id.IsEmpty);
    }

    // A save restores what the scene placed without the `FromScene` tag (it is transient); the scene
    // takes them back by their persistent ids, so a reload after a load does not place a second copy.
    [Fact]
    public void AfterASaveLoadsAReloadStillDoesNotDuplicate()
    {
        var (app, _) = NewGame();
        using (app)
        {
            app.Engine.Saves.Root = TestEnv.NewTempDir();
            var world = app.World;
            Assert.True(app.Engine.Saves.Save("scene"));
            Assert.True(app.Engine.Saves.Load("scene"));

            Assert.Equal(2, FromScene(world).Length);
            var player = Assert.Single(Players(world));

            app.Records.Reload();

            Assert.Equal(2, world.QueryAll().Entities.ToEntityList().Count(e => e.Name == "rock"));
            Assert.Equal(player, Assert.Single(Players(world)));
        }
    }

    // One placement format (issue #29): `at` in the frame `relativeTo` names — absolute by default, from
    // the record's `origin`, or across from it and above the ground — for scenes and documents alike.
    [Fact]
    public void APlacementIsMeasuredInTheFrameItNames()
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", """
        [
          { "type": "prefab", "id": "rock", "name": "rock" },
          { "type": "scene", "id": "main", "origin": [10, 3, 20], "relativeTo": "Origin",
            "place": [
              { "prefab": "rock", "at": [1, 0, 1], "name": "origin" },
              { "prefab": "rock", "at": [1, 0, 1], "name": "world", "relativeTo": "World" },
              { "prefab": "rock", "at": [1, 2, 1], "name": "ground", "relativeTo": "Ground" }
            ] }
        ]
        """);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).StartScene("game:main").Build();
        var world = app.CreateWorld("frames");

        Vector3 Named(string name) => At(FromScene(world).Single(e => e.Name == name));
        Assert.Equal(new Vector3(11, 3, 21), Named("origin"));
        Assert.Equal(new Vector3(1, 0, 1), Named("world"));
        Assert.Equal(new Vector3(11, 5, 21), Named("ground"));   // no terrain: the origin's height is the ground
    }

    // The weather a scene starts in is the world's at once, not rolled in.
    [Fact]
    public void ASceneSetsTheWeatherItStartsIn()
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", """
        [
          { "type": "weather", "id": "fog", "label": "fog" },
          { "type": "scene", "id": "main", "environment": { "weather": "fog" } }
        ]
        """);
        files.Mount("game", "game");
        // `weather` is the client's record type; a headless test registers it as `sage validate` does.
        using var app = HeadlessApp.Bare().Mount(files).StartScene("game:main")
            .OnRegistered(a => a.Records.Register<WeatherRecord>()).Boot();

        var weather = app.World.Resources.Get<Weather>();
        Assert.Equal(new RecordId("game", "fog"), weather.Showing);
        Assert.True(weather.Settled);
    }
}
