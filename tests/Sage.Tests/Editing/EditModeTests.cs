#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The editor mode of the host (issue #219), the half with no window in it: the edit world (no Fixed
// system runs, transforms and Frame systems do, no player), what `-edit <name>` opens, and the log
// panel's filter.
public class EditModeTests
{
    public EditModeTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "prefab", "id": "rock", "name": "rock" },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
      { "type": "placements", "id": "yard_things", "place": [ { "prefab": "rock", "at": [0, 0, 9], "name": "yard rock" } ] },
      { "type": "placements", "id": "loose", "place": [ { "prefab": "rock", "at": [1, 0, 1] } ] },
      { "type": "scene", "id": "main",
        "player": { "prefab": "hero", "at": [0, 1, 0] },
        "placements": ["yard_things"],
        "place": [ { "prefab": "rock", "at": [2, 0, 0] }, { "prefab": "rock", "at": [-2, 0, 0] } ] },
      { "type": "scene", "id": "cave", "place": [ { "prefab": "rock", "at": [0, 0, 0], "name": "cave rock" } ] }
    ]
    """;

    private static HeadlessApp NewGame()
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", Content);
        files.Mount("game", "game");
        return HeadlessApp.Bare().Mount(files).StartScene("game:main").Build();
    }

    private static Entity[] Players(World world) =>
        world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList().ToArray();

    private sealed class Counter : ISystem
    {
        public int Runs;
        public void Run(in SystemContext ctx) => Runs++;
    }

    // The point of an edit world: the document on screen, and nothing in it moving by itself. Not even a
    // system that asked to run while paused; the Frame schedule (cameras, extraction, drawing) runs as in
    // any world, and so does transform propagation, or an edited placement would never move on screen.
    [Fact]
    public void AnEditWorldRunsNoFixedSystemButItsFrameSystemsAndTransformsRun()
    {
        using var app = NewGame();
        var world = app.App.CreateEditWorld("edit");
        var gameplay = new Counter();
        var always = new Counter();
        var frame = new Counter();
        world.AddSystem(gameplay, Phase.Gameplay);
        world.AddSystem(always, Phase.Late, RunCondition.Always);
        world.AddSystem(frame, Phase.FrameUpdate);

        var rock = world.FindByName("yard rock");
        Assert.False(rock.IsNull);
        world.Get<Transform>(rock).LocalPosition = new Vector3(5, 0, 5);   // what an editor's move does

        for (int i = 0; i < 10; i++) world.RunFixed(1f / 60f);
        world.RunFrame(1f / 60f, 1f);

        Assert.True(world.Editing);
        Assert.Equal(0, gameplay.Runs);
        Assert.Equal(0, always.Runs);
        Assert.Equal(1, frame.Runs);
        Assert.Equal(new Vector3(5, 0, 5), world.Get<GlobalTransform>(rock).Current.Position);
    }

    // The scene as its files say, and nothing the rules would add: no player.
    [Fact]
    public void AnEditWorldIsPlacedWithTheSceneButHasNoPlayer()
    {
        using var app = NewGame();
        var world = app.App.CreateEditWorld("edit");

        Assert.Equal(new RecordId("game", "main"), world.Resources.Get<ActiveScene>().Id);
        Assert.False(world.FindByName("yard rock").IsNull);
        Assert.Empty(Players(world));
        Assert.Equal(new Vector3(0, 1, 0), world.PlayerStart());   // the editor's camera starts here

        app.Records.Reload();                                        // a hot reload places it again, still unplayed
        Assert.False(world.FindByName("yard rock").IsNull);
        Assert.Empty(Players(world));

        var cave = app.App.CreateEditWorld("cave", new RecordId("game", "cave"));
        Assert.False(cave.FindByName("cave rock").IsNull);
        Assert.True(cave.FindByName("yard rock").IsNull);
    }

    // Physics is mirrored in an edit world but not stepped: colliders get bodies (`sage.physics.sync` runs
    // EvenWhenEditing), so the editor picks by raycast, while a dynamic crate hangs where it was placed.
    [Fact]
    public void TheEditorPicksCollidersInAnEditWorldWhereNothingFalls()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule()).Build();
        var world = app.App.CreateEditWorld("edit");
        var wall = world.Create(Transform.At(new Vector3(0, 0, -5)), "wall");
        world.Add(wall, Collider.Box(new Vector3(2, 2, 2)));
        var crate = world.Create(Transform.At(new Vector3(0, 5, -5)), "crate");
        world.Add(crate, Collider.Box(new Vector3(1, 1, 1)));
        world.Add(crate, RigidBody.Dynamic(10f));

        for (int i = 0; i < 30; i++) world.RunFixed(1f / 60f);

        Assert.True(world.Has<PhysicsBody>(crate));
        Assert.Equal(new Vector3(0, 5, -5), world.Get<Transform>(crate).LocalPosition);   // not stepped
        Assert.Equal(wall, EditorPicking.Pick(world, new EditorRay(Vector3.Zero, -Vector3.UnitZ))!.Value.Entity);
        Assert.Equal(crate, EditorPicking.Pick(world, new EditorRay(new Vector3(0, 5, 0), -Vector3.UnitZ))!.Value.Entity);
    }

    // A game run is untouched: a world beside the edit world plays, with its player and its systems.
    [Fact]
    public void APlayWorldBesideAnEditWorldStillPlays()
    {
        using var app = NewGame();
        var edit = app.App.CreateEditWorld("edit");
        var play = app.CreateWorld("main");
        var counter = new Counter();
        play.AddSystem(counter, Phase.Gameplay);

        play.RunFixed(1f / 60f);

        Assert.False(play.Editing);
        Assert.Equal(1, counter.Runs);
        Assert.Single(Players(play));
        Assert.Empty(Players(edit));
    }

    // `-edit`, `-edit <scene>`, `-edit <placements>`: a scene opens with its first document, a document
    // in the scene that names it; a name that is neither is refused, saying so.
    [Fact]
    public void TheEditArgumentNamesASceneOrADocument()
    {
        using var app = NewGame();
        var records = app.Records;
        var start = new RecordId("game", "main");

        Assert.True(EditTarget.TryResolve(records, start, null, out var target, out _));
        Assert.Equal(new EditTarget(start, new RecordId("game", "yard_things")), target);

        Assert.True(EditTarget.TryResolve(records, start, "cave", out target, out _));
        Assert.Equal(new EditTarget(new RecordId("game", "cave"), default), target);

        Assert.True(EditTarget.TryResolve(records, start, "game:yard_things", out target, out _));
        Assert.Equal(new EditTarget(start, new RecordId("game", "yard_things")), target);

        Assert.True(EditTarget.TryResolve(records, start, "loose", out target, out _));   // no scene names it: the start scene's
        Assert.Equal(new EditTarget(start, new RecordId("game", "loose")), target);

        Assert.False(EditTarget.TryResolve(records, start, "nowhere", out _, out string error));
        Assert.Contains("'nowhere'", error);
    }

    private static readonly LogCat Records = LogCat.Records, Editor = LogCat.Editor;

    private static void Write(RingBufferLogSink ring, LogCat category, LogLevel level, string message) =>
        ring.Write(new LogEntry(DateTime.UtcNow, 0, 0, 0, 1, "test", category, level, message, default, default, default, null, 0));

    // The log panel: the ring's lines at or above a level, without the categories hidden, worked out
    // again only when the ring or the filter changes.
    [Fact]
    public void TheLogPanelFiltersTheRingByLevelAndCategory()
    {
        var ring = new RingBufferLogSink(4);
        var view = new LogView(ring);
        Write(ring, Records, LogLevel.Debug, "parsed");
        Write(ring, Records, LogLevel.Warn, "unknown field");
        Write(ring, Editor, LogLevel.Info, "opened");

        Assert.True(view.Refresh());
        Assert.Equal(new[] { "unknown field", "opened" }, view.Lines.Select(l => l.Message));   // Info and up
        Assert.Equal(new[] { "Editor", "Records" }, view.Categories);
        Assert.False(view.Refresh());                                                           // nothing changed

        view.Show("records", false);
        Assert.True(view.Refresh());
        Assert.Equal(new[] { "opened" }, view.Lines.Select(l => l.Message));

        view.Only("Records");
        view.MinLevel = LogLevel.Trace;
        view.Refresh();
        Assert.Equal(new[] { "parsed", "unknown field" }, view.Lines.Select(l => l.Message));

        // Bounded by the ring: the oldest line goes when a fifth arrives.
        Write(ring, Records, LogLevel.Error, "a");
        Write(ring, Records, LogLevel.Error, "b");
        Assert.True(view.Refresh());
        Assert.Equal(new[] { "unknown field", "a", "b" }, view.Lines.Select(l => l.Message));
    }
}
