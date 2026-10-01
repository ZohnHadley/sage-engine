#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 10a's exit (issue #228, REDESIGN §5 row 10): **a designer builds a level from prefabs (place, tune,
// wire, undo, play, save) without touching JSON, and the file they saved loads in the game.**
//
// tests/games/editor is a data-only game with a pressure plate, a door, a crate and a light, and an empty
// level whose placements document holds a hand-written comment and nothing else. The test drives the
// editor only through its console commands, as a person at the console does, on a copy of the game in a
// temporary folder (the save writes into it). The commands are registered the way the host's DevTools
// registers them, minus the windows; the edit world is made as `-edit level` makes it.
public class EditorExitTests
{
    public EditorExitTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const string Comment = "// Written by hand, before the editor ever opened this file";
    private static readonly RecordId Level = new("editor", "level_placements");

    // Where the plate and the door end up, after the moves and turns.
    private static readonly Vector3 PlateAt = new(0, 0.5f, 2);
    private static readonly Vector3 DoorAt = new(0, 1.5f, -4);

    private static string Game() => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "editor");

    // The game, copied, so a save never touches the repository.
    private static string CopyOfTheGame()
    {
        string from = Game(), to = Path.Combine(TestEnv.NewTempDir(), "editor");
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return to;
    }

    private static string DocumentFile(string game) => Path.Combine(game, "content", "data", "level_placements.json");

    // The editor on a game folder, headless: what `Sage.Host -game <folder> -edit <name>` builds, with the
    // same console commands (DevTools.RegisterDocumentCommands) and no windows.
    private sealed class Editor : IDisposable
    {
        public readonly HeadlessApp App;
        public readonly World World;
        public readonly EditDocument Document;
        public readonly EditorSelection Selection;
        public readonly PlaySession Play;
        public readonly EditTarget Target;   // what `-edit <name>` opens (DevTools.BeginEditing opens its document; doc_open does here)

        public Editor(string game, string edit)
        {
            App = HeadlessApp.ForGame(game).WithEngineContent().Build();
            var engine = App.Engine;
            Assert.True(EditTarget.TryResolve(engine.Records, engine.Scenes.Start, edit, out Target, out string error), error);
            World = App.App.CreateEditWorld("edit", Target.Scene);
            Document = new EditDocument(World);
            Selection = new EditorSelection(Document);
            Play = new PlaySession(Document);

            var cvars = App.CVars;
            EditorCommands.Register(cvars, () => Document);       // doc_*, ed_undo/redo/history, ed_palette/place, ed_wire...
            InspectorCommands.Register(cvars, () => Document);    // ed_set, ed_revert, ed_inspect
            ViewportTools.Register(cvars, () => Selection);       // ed_select, ed_move, ed_rotate, ...
            PlayCommands.Register(cvars, () => Play);             // ed_play, ed_stop
            ProblemCommands.Register(cvars, engine, () => Document);   // ed_problems
        }

        // One line at the console; every command here exists, so Execute says it ran.
        public void Press(string line) => Assert.True(App.CVars.Execute(line), line);

        public Placement Placed(string name) => Document.Find(name) ?? throw new Xunit.Sdk.XunitException($"no placement '{name}'");
        public Entity Entity(string name) => Document.EntityOf(Placed(name));

        public string Json() => JsonSerializer.Serialize(Document.Record, App.Records.Json);

        public void Dispose() => App.Dispose();
    }

    private static void Step(World world, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    // The player steps onto the plate (put there, the way a walk would end), and the door opens. Returns
    // how many ticks the door took from the step to fully open.
    private static int StepOnThePlateAndWaitForTheDoor(World world)
    {
        var player = Scenes.Player(world);
        Assert.False(player.IsNull, "the game has no player");
        var door = world.FindByName("door");
        Assert.False(door.IsNull);

        Step(world, 10);                                                   // standing about at the start: nothing opens
        Assert.Equal(0f, world.Get<Mover>(door).Position);
        Assert.Equal(0, world.Get<Mover>(door).Direction);

        world.Teleport(player, Transform.At(PlateAt + new Vector3(0, 0.5f, 0)));
        int ticks = 0;
        while (world.Get<Mover>(door).Position < 1f && ticks < 600) { Step(world); ticks++; }
        Assert.Equal(1f, world.Get<Mover>(door).Position);
        Assert.Equal(DoorAt + new Vector3(2, 0, 0), world.Get<Transform>(door).LocalPosition);
        return ticks;
    }

    // The plate and the door as the build below leaves them, in the document and in the world it spawned.
    private static void AssertTheLevel(PlacementsRecord record, World world)
    {
        Assert.Equal(2, record.Place.Count);
        var plate = Assert.Single(record.Place, p => p.Name == "plate");
        var door = Assert.Single(record.Place, p => p.Name == "door");
        Assert.Equal(new RecordId("editor", "plate"), plate.Prefab.Id);
        Assert.Equal(PlateAt, plate.At);
        Assert.Equal(90f, plate.Yaw);
        Assert.Equal(new RecordId("editor", "door"), door.Prefab.Id);
        Assert.Equal(DoorAt, door.At);
        Assert.Equal(180f, door.Yaw);

        // The door's speed is an override of the placement; the prefab still says 2 seconds.
        Assert.Null(plate.Overrides);
        Assert.NotNull(door.Overrides?.Parts);
        Assert.Equal(0.5, door.Overrides!.Parts!["mover"]!["seconds"]!.GetValue<double>(), 6);
        Assert.Null(door.Overrides.Components);

        var wire = Assert.Single(plate.Outputs);
        Assert.Equal(("OnStartTouch", "door", "Open"), (wire.Output, wire.Target, wire.Input));
        Assert.Empty(door.Outputs);

        // What it spawned: the door's mover takes half a second, the plate carries the wire.
        var doorEntity = world.FindByName("door");
        Assert.Equal(0.5f, world.Get<Mover>(doorEntity).Seconds);
        Assert.Equal(DoorAt, world.Get<Transform>(doorEntity).LocalPosition);
        var plateEntity = world.FindByName("plate");
        Assert.True(world.Has<PhysicsBody>(plateEntity) || world.Has<Collider>(plateEntity));
        Assert.Equal("Open", Assert.Single(world.Get<IOConnections>(plateEntity).Wires!).Input);
    }

    // The exit, end to end: open the empty level, place, move, turn, tune, wire, undo and redo, play (the
    // plate opens the door), stop (nothing the play did is kept), save (the comment stays), and boot the
    // saved game with no editor: the same level, and the plate opens the door there too.
    [Fact]
    public void ADesignerBuildsALevelFromPrefabsInTheEditorAndTheGameLoadsWhatTheySaved()
    {
        string game = CopyOfTheGame();
        string before = File.ReadAllText(DocumentFile(game));
        Assert.Contains(Comment, before);
        string saved;

        using (var editor = new Editor(game, "level"))
        {
            var world = editor.World;
            Assert.True(world.Editing);

            // Open the level: an empty document, in the scene that names it.
            editor.Press("doc_open level_placements");
            Assert.True(editor.Document.IsOpen);
            Assert.Equal(Level, editor.Document.Id);
            Assert.Empty(editor.Document.Record.Place);
            Assert.False(editor.Document.Dirty);

            // Place the plate and the door from the palette, somewhere to start with.
            editor.Press("ed_place plate 1 0.5 1 0 plate");
            editor.Press("ed_place door 3 1.5 -3 0 door");
            Assert.Equal(2, editor.Document.Record.Place.Count);
            Assert.False(editor.Entity("door").IsNull);

            // Move and turn them where they belong.
            editor.Press("ed_move plate 0 0.5 2");
            editor.Press("ed_rotate plate 90");
            editor.Press("ed_select door");
            editor.Press("ed_move 0 1.5 -4");                              // the selection
            editor.Press("ed_rotate 180");
            Assert.Equal(DoorAt, world.Get<Transform>(editor.Entity("door")).LocalPosition);

            // Tune: the door opens in half a second rather than the prefab's two. Then wire the plate to it.
            Assert.Equal(2f, world.Get<Mover>(editor.Entity("door")).Seconds);
            editor.Press("ed_set door mover.seconds 0.5");                // the part's field, not the read-only component it builds
            Assert.Equal(0.5f, world.Get<Mover>(editor.Entity("door")).Seconds);
            editor.Press("ed_wire plate OnStartTouch door Open");
            AssertTheLevel(editor.Document.Record, world);
            Assert.Equal(8, editor.Document.History.Position);
            Assert.True(editor.Document.Dirty);

            // Undo the wire and the speed, and redo them: each is one step, and the world follows.
            editor.Press("ed_undo 2");
            Assert.Empty(editor.Placed("plate").Outputs);
            Assert.Null(editor.Placed("door").Overrides);
            Assert.Equal(2f, world.Get<Mover>(editor.Entity("door")).Seconds);
            Assert.False(world.Has<IOConnections>(editor.Entity("plate")));
            editor.Press("ed_redo 2");
            AssertTheLevel(editor.Document.Record, world);
            Assert.Equal(8, editor.Document.History.Position);

            // Nothing in the edit world moves by itself: the door stays shut however long it waits.
            Step(world, 30);
            Assert.Equal(0f, world.Get<Mover>(editor.Entity("door")).Position);

            // Play: the level as the editor has it (unsaved), with the game's player. Step on the plate and
            // the door opens, in about half a second.
            string json = editor.Json(), history = EditorCommands.History(editor.Document);
            int entities = world.EntityCount;
            var doorEntity = editor.Entity("door");
            editor.Press("ed_play");
            var play = editor.Play.World;
            Assert.NotNull(play);
            Assert.False(play!.Editing);
            int ticks = StepOnThePlateAndWaitForTheDoor(play);
            Assert.InRange(ticks, 25, 40);                                 // 0.5 s (30 ticks), not the prefab's 2 s
            play.Get<Transform>(play.FindByName("plate")).LocalPosition = new Vector3(9, 9, 9);   // play's business

            // Stop: the play world is gone, and the document, its history and the edit world are as they were.
            editor.Press("ed_stop");
            Assert.False(editor.Play.IsPlaying);
            Assert.DoesNotContain(play, editor.App.Engine.Worlds);
            Assert.Equal(json, editor.Json());
            Assert.Equal(history, EditorCommands.History(editor.Document));
            Assert.Equal(entities, world.EntityCount);
            Assert.True(world.IsAlive(doorEntity));
            Assert.Equal(0f, world.Get<Mover>(doorEntity).Position);
            Assert.Equal(DoorAt, world.Get<Transform>(doorEntity).LocalPosition);
            Assert.Equal(PlateAt, world.Get<Transform>(editor.Entity("plate")).LocalPosition);
            Assert.True(editor.Document.Dirty);

            // Save: into the file the document came from, and nothing is wrong with it.
            editor.Press("doc_save");
            Assert.False(editor.Document.Dirty);
            Assert.Equal(Path.GetFullPath(DocumentFile(game)), Path.GetFullPath(editor.Document.Path));
            using (var problems = new ProblemList(editor.App.Engine, editor.Document))
            {
                Assert.Equal(0, problems.Errors);
                Assert.DoesNotContain(problems.Problems, p => p.File.Contains("level", StringComparison.Ordinal));
            }
            saved = editor.Json();
        }

        // The file: the comment the designer's colleague wrote is still there, above the list it now fills.
        string text = File.ReadAllText(DocumentFile(game));
        Assert.Contains(Comment, text);
        Assert.True(text.IndexOf(Comment, StringComparison.Ordinal) < text.IndexOf("\"place\"", StringComparison.Ordinal));
        Assert.DoesNotContain("\"Prefab\"", text);                       // the record store's dialect, not C#'s

        // A plain game boot, no editor: the scene places the saved document, with its overrides and its wire.
        using var app = HeadlessApp.ForGame(game).WithEngineContent().Boot();
        Assert.True(app.Records.TryGet(Level, out PlacementsRecord loaded));
        Assert.Equal(saved, JsonSerializer.Serialize(loaded, app.Records.Json));
        AssertTheLevel(loaded, app.World);
        Assert.InRange(StepOnThePlateAndWaitForTheDoor(app.World), 25, 40);
    }

    // The exit's game is content a designer can trust: it validates with no error, and its own files with
    // no warning; it offers its four prefabs on the palette; and its level starts empty.
    [Fact]
    public void TheEditorGameValidatesAndOffersItsPrefabs()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Game(),
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("prefabs.json") || w.Contains("level"));

        using var editor = new Editor(Game(), "level_placements");
        Assert.Equal(new EditTarget(new RecordId("editor", "level"), Level), editor.Target);   // the document, in the scene naming it
        var palette = new PrefabPalette(editor.App.Records) { Search = "editor:" };
        var names = palette.Groups().SelectMany(g => g.Prefabs).Select(p => p.Name).ToArray();
        foreach (string prefab in new[] { "plate", "door", "crate", "light" }) Assert.Contains(prefab, names);
        Assert.True(editor.App.Records.TryGet(Level, out PlacementsRecord level));
        Assert.Empty(level.Place);
    }

    // A level built from nothing (`doc_level`, not a file the game had): placed, wired, saved, and the new
    // scene that names it boots in a plain game with the crate and the light where the editor put them.
    [Fact]
    public void ANewLevelMadeInTheEditorIsASceneTheGameBoots()
    {
        string game = CopyOfTheGame();
        using (var editor = new Editor(game, "level"))
        {
            editor.Press("doc_level storeroom");
            Assert.True(editor.Document.IsOpen);
            editor.Press("ed_place crate 1 0.5 0 0 crate");
            editor.Press("ed_place light 0 3 0 0 lamp");
            editor.Press("ed_set lamp light.range 12");
            editor.Press("ed_duplicate crate");
            editor.Press("ed_move crate_2 -1 0.5 0");
            editor.Press("doc_save");
            Assert.False(editor.Document.Dirty);
        }

        using var app = HeadlessApp.ForGame(game).WithEngineContent().StartScene("editor:storeroom").Boot();
        var world = app.World;
        Assert.Equal(new Vector3(1, 0.5f, 0), world.Get<Transform>(world.FindByName("crate")).LocalPosition);
        Assert.Equal(new Vector3(-1, 0.5f, 0), world.Get<Transform>(world.FindByName("crate_2")).LocalPosition);
        Assert.Equal(12f, world.Get<PointLight>(world.FindByName("lamp")).Range);
    }
}
