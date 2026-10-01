#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Play-in-editor (issue #226): Play builds a second, real world from the document, the game plays in it,
// and Stop throws it away, leaving the document and the edit world as they were.
public class PlayInEditorTests
{
    public PlayInEditorTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // A blinker (a timer that starts on its own) and a door in the scene's document; the player starts a
    // metre above the ground.
    private const string Content = """
    [
      { "type": "prefab", "id": "thing", "name": "thing" },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
      { "type": "prefab", "id": "blinker", "name": "blinker",
        "parts": { "timer": { "interval": 0.1, "repeat": true, "startOn": true } } },
      { "type": "placements", "id": "yard", "place": [
          { "prefab": "blinker", "at": [0, 0, 0], "name": "blinker" },
          { "prefab": "thing", "at": [5, 0, 0], "name": "door" } ] },
      { "type": "placements", "id": "loose", "place": [ { "prefab": "thing", "at": [1, 0, 1], "name": "loose thing" } ] },
      { "type": "scene", "id": "main",
        "player": { "prefab": "hero", "at": [0, 1, 0] },
        "placements": ["yard"],
        "place": [ { "prefab": "thing", "at": [2, 0, 0], "name": "post" } ] }
    ]
    """;

    private static readonly RecordId Yard = new("game", "yard");

    // What arrived at a `Record` input, and in which world.
    private sealed class Arrivals
    {
        public readonly List<(World World, string Parameter)> List = new();
    }

    private static HeadlessApp NewGame(Arrivals? arrivals = null)
    {
        var files = new MountFixture();
        files.Write("game", "data/scene.json", Content);
        files.Mount("game", "game");
        var builder = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).StartScene("game:main");
        if (arrivals != null)
            builder = builder.OnRegistered(app => app.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
                arrivals.List.Add((world, io.Parameter))));
        return builder.Build();
    }

    private static Entity[] Players(World world) =>
        world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList().ToArray();

    private static string Json(HeadlessApp app, EditDocument document) => JsonSerializer.Serialize(document.Record, app.Records.Json);

    private static void Tick(World world, int times)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    // The acceptance test: play, change things in play, stop. The document (its record, its history, its
    // dirty state), the record store and the edit world's entities are as they were; the play world is gone.
    // An unsaved placement played, and the player stood on the ground below the camera.
    [Fact]
    public void PlayThenStopLeavesTheDocumentAndTheEditWorldAsTheyWere()
    {
        using var app = NewGame();
        var edit = app.App.CreateEditWorld("edit");
        var document = new EditDocument(edit);
        Assert.True(document.Open(Yard));
        Assert.NotNull(Placing.Place(document, new RecordId("game", "thing"), new Vector3(1, 0, 1), 0f, "fresh"));   // never saved

        string json = Json(app, document), history = EditorCommands.History(document);
        int entities = edit.EntityCount;
        var door = document.EntityOf(document.Find("door")!);
        Assert.True(document.Dirty);

        var session = new PlaySession(document);
        World? started = null;
        session.Started += w => started = w;
        bool stopped = false;
        session.Stopped += () => stopped = true;

        var play = session.Play(new PlayStart(new Vector3(4, 6, 4), 90f));
        Assert.NotNull(play);
        Assert.Same(play, started);
        Assert.True(session.IsPlaying);
        Assert.False(play!.Editing);
        Assert.Contains(play, app.Engine.Worlds);
        Assert.Null(session.Play());                                     // one play world at a time

        // The game's world: the scene, the document as the editor has it, and the rules' player, put on the
        // ground below the camera, as high as the scene's start stands (1 m), facing the camera's way.
        Assert.False(play.FindByName("post").IsNull);
        Assert.False(play.FindByName("fresh").IsNull);
        var player = Assert.Single(Players(play));
        var transform = play.Get<Transform>(player);
        Assert.Equal(new Vector3(4, 1, 4), transform.LocalPosition);
        Assert.Equal(90f, SageMath.YawOf(transform.LocalRotation) * 180f / System.MathF.PI, 3);
        Assert.Empty(Players(edit));

        // Whatever happens in play stays in play.
        Tick(play, 30);
        play.Destroy(play.FindByName("door"));
        play.Get<Transform>(play.FindByName("fresh")).LocalPosition = new Vector3(9, 9, 9);

        Assert.True(session.Stop());
        Assert.True(stopped);
        Assert.False(session.IsPlaying);
        Assert.Null(session.World);
        Assert.DoesNotContain(play, app.Engine.Worlds);
        Assert.False(session.Stop());

        Assert.Equal(json, Json(app, document));
        Assert.Equal(history, EditorCommands.History(document));
        Assert.True(document.Dirty);
        Assert.Equal(entities, edit.EntityCount);
        Assert.True(edit.IsAlive(door));
        Assert.Equal(new Vector3(1, 0, 1), edit.Get<Transform>(edit.FindByName("fresh")).LocalPosition);
        Assert.True(app.Records.TryGet(Yard, out PlacementsRecord stored));
        Assert.Equal(2, stored.Place.Count);                              // the store has the file's, still
    }

    // A wire made in the editor, never saved, fires in play — and not in the edit world, where nothing ticks.
    // Pressed through the console, as a person does.
    [Fact]
    public void AWireMadeInTheEditorFiresInPlay()
    {
        var arrivals = new Arrivals();
        using var app = NewGame(arrivals);
        var edit = app.App.CreateEditWorld("edit");
        var document = new EditDocument(edit);
        Assert.True(document.Open(Yard));
        var session = new PlaySession(document);
        PlayCommands.Register(app.CVars, () => session);

        var blinker = document.Find("blinker")!;
        document.Execute(new SetOutputs(document, blinker, new[]
        {
            new Connection { Output = "OnTimer", Target = "door", Input = "Record", Parameter = "blink" },
        }));
        Tick(edit, 30);
        Assert.Empty(arrivals.List);

        Assert.True(app.CVars.Execute("ed_play"));
        var play = session.World;
        Assert.NotNull(play);
        Assert.Equal(new Vector3(0, 1, 0), play!.Get<Transform>(Assert.Single(Players(play))).LocalPosition);   // the scene's start, headless
        Tick(play, 30);   // half a second of a 0.1 s timer
        Assert.True(arrivals.List.Count >= 3, $"{arrivals.List.Count} blink(s)");
        Assert.All(arrivals.List, a => Assert.Same(play, a.World));
        Assert.All(arrivals.List, a => Assert.Equal("blink", a.Parameter));

        Assert.True(app.CVars.Execute("ed_stop"));
        Assert.False(session.IsPlaying);
        Assert.Single(document.Find("blinker")!.Outputs);                 // the wire is still the document's
        Assert.True(app.Records.TryGet(Yard, out PlacementsRecord stored));
        Assert.Empty(stored.Place[0].Outputs ?? new List<Connection>());   // and not the store's: it was never saved
    }

    // A document the scene does not name plays beside the scene, as the editor showed it; and play is the
    // editor's: a world that is not an edit world refuses it.
    [Fact]
    public void ADocumentTheSceneDoesNotNamePlaysBesideIt()
    {
        using var app = NewGame();
        var edit = app.App.CreateEditWorld("edit");
        var document = new EditDocument(edit);
        Assert.True(document.Open(new RecordId("game", "loose")));
        var session = new PlaySession(document);

        var play = session.Play()!;
        Assert.Equal(new RecordId("game", "main"), session.Scene);
        Assert.False(play.FindByName("loose thing").IsNull);
        Assert.False(play.FindByName("door").IsNull);                     // the scene's own document, from the store
        Assert.False(play.FindByName("post").IsNull);
        session.Stop();

        var game = app.CreateWorld("main");
        var notEditing = new PlaySession(new EditDocument(game));
        PlayCommands.Register(app.CVars, () => notEditing);
        app.CVars.Execute("ed_play");
        Assert.False(notEditing.IsPlaying);
    }
}
