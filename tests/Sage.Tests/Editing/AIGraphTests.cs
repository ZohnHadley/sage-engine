#nullable enable
using System.Linq;
using System.Text.Json.Nodes;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The AI graph view's model (issue #369): a state_machine, ai_schedule or routine open in the record
// browser, read as a tree of nodes, edited a node at a time through the document's undo history, and asked
// which nodes a running entity is in, in the play world while playing.
public class AIGraphTests
{
    public AIGraphTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // A guard (patrol, nested walk/look; alert; a transition from any state) and the scene it stands in.
    private const string Content = """
    [
      { "type": "state_machine", "id": "guard", "initial": "patrol",
        "states": {
          "patrol": { "initial": "walk",
                      "transitions": [ { "to": "alert", "on": "Alarm" } ],
                      "states": {
                        "walk": { "transitions": [ { "to": "look", "after": 0.5 } ] },
                        "look": { "tags": ["idle"], "transitions": [ { "to": "walk", "after": 0.5 } ] } } },
          "alert":  { "transitions": [ { "to": "patrol", "on": "Calm" } ] } },
        "transitions": [ { "to": "alert", "when": { "var": "alarm", "eq": 1 } } ] },
      { "type": "prefab", "id": "guard", "name": "guard", "parts": { "state_machine": { "machine": "guard" } } },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
      { "type": "placements", "id": "post", "place": [ { "prefab": "guard", "at": [3, 0, 0], "name": "guard" } ] },
      { "type": "scene", "id": "main", "player": { "prefab": "hero", "at": [0, 1, 0] }, "placements": ["post"] }
    ]
    """;

    private static readonly RecordId Guard = new("game", "guard");

    private static HeadlessApp NewGame()
    {
        var files = new MountFixture();
        files.Write("game", "data/guard.json", Content);
        files.Mount("game", "game");
        return HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).StartScene("game:main").Build();
    }

    private static string[] Names(AIGraph graph) => graph.Nodes.Select(n => n.Name).ToArray();

    [Fact]
    public void AStateMachineIsATreeOfStatesWithItsTransitionsAsEdges()
    {
        using var app = NewGame();
        var graph = AIGraph.Of(RecordDocument.Open(app.Engine, "state_machine", Guard))!;
        Assert.Equal(AIGraphKind.StateMachine, graph.Kind);
        Assert.Equal(new[] { "patrol", "walk", "look", "alert" }, Names(graph));

        var patrol = graph.Find("patrol")!;
        Assert.True(patrol.IsInitial);
        Assert.Equal(0, patrol.Depth);
        Assert.Equal(new[] { "states.patrol.states.walk", "states.patrol.states.look" }, patrol.Children);
        Assert.Equal(new AIGraphEdge("alert", "on Alarm"), Assert.Single(patrol.Edges));
        var walk = graph.Find("WALK")!;                                           // names ignore case
        Assert.Equal(1, walk.Depth);
        Assert.Equal("states.patrol", walk.Parent);
        Assert.True(walk.IsInitial);
        Assert.Equal("look  [idle]", graph.Find("look")!.Label);
        Assert.Equal(new AIGraphEdge("look", "after 0.5s"), Assert.Single(walk.Edges));
        Assert.Equal("alert", Assert.Single(graph.AnyStateEdges).To);
        Assert.Null(AIGraph.Of(RecordDocument.Open(app.Engine, "prefab", Guard)));   // a prefab is not an AI graph
    }

    // The acceptance test's edit half: add a node, move it, undo both, and the record is as it was.
    [Fact]
    public void AStateIsAddedAndMovedAndBothAreUndone()
    {
        using var app = NewGame();
        var document = RecordDocument.Open(app.Engine, "state_machine", Guard)!;
        var graph = AIGraph.Of(document)!;
        var original = (JsonObject)document.Working.DeepClone();

        Assert.True(graph.Add("patrol", "listen"));                             // the view follows the document
        Assert.Equal(new[] { "patrol", "walk", "look", "listen", "alert" }, Names(graph));
        Assert.Equal("states.patrol", graph.Find("listen")!.Parent);
        Assert.True(document.Dirty);

        Assert.True(graph.Move("listen", 0));
        Assert.Equal(new[] { "patrol", "listen", "walk", "look", "alert" }, Names(graph));
        Assert.True(graph.Find("walk")!.IsInitial);                              // a move does not change the initial
        Assert.Equal(2, document.History.Position);
        Assert.Contains("Move state 'listen'", document.History.Entries[1].Description);

        Assert.False(graph.Add(null, "Walk"));                                   // names are unique across the machine
        Assert.Equal(2, document.History.Position);

        Assert.True(document.Undo());
        Assert.Equal(new[] { "patrol", "walk", "look", "listen", "alert" }, Names(graph));
        Assert.True(document.Undo());
        Assert.Equal(new[] { "patrol", "walk", "look", "alert" }, Names(graph));
        Assert.True(JsonNode.DeepEquals(original, document.Working));
        Assert.False(document.Dirty);
        Assert.True(document.Redo());
        Assert.Equal("listen", graph.Nodes[3].Name);
    }

    [Fact]
    public void AStateIsReparentedRenamedAndRemovedKeepingTheMachineLoadable()
    {
        using var app = NewGame();
        var document = RecordDocument.Open(app.Engine, "state_machine", Guard)!;
        var graph = AIGraph.Of(document)!;

        // Into a plain state: it becomes compound, with the moved state its initial.
        Assert.True(graph.Reparent("look", "alert"));
        Assert.Equal("states.alert.states.look", graph.Find("look")!.Path);
        Assert.True(graph.Find("look")!.IsInitial);
        Assert.Equal("look", (string?)document.Get("states.alert.initial"));
        Assert.False(graph.Reparent("patrol", "walk"));                          // not inside itself

        // A rename follows every transition and initial that named it.
        Assert.True(graph.Rename("walk", "stroll"));
        Assert.Equal("stroll", (string?)document.Get("states.patrol.initial"));
        Assert.Equal("stroll", (string?)document.Get("states.alert.states.look.transitions[0].to"));

        // Removing a state takes the transitions to it with it, and the machine's initial moves on.
        Assert.True(graph.Remove("alert"));
        Assert.Equal(new[] { "patrol", "stroll" }, Names(graph));
        Assert.Empty(graph.Find("patrol")!.Edges);
        Assert.Empty(graph.AnyStateEdges);
        Assert.True(graph.Remove("patrol"));
        Assert.Equal("", graph.Initial);
        Assert.True(graph.Add(null, "idle"));
        Assert.Equal("idle", graph.Initial);

        // Saved and loaded again by the record loader, without an error.
        using var log = new CaptureSink();
        Assert.True(document.Save());
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Equal("idle", app.Engine.Records.Get<StateMachineRecord>(Guard).Initial);
        for (int i = 0; i < 5; i++) Assert.True(document.Undo());
        Assert.Equal(new[] { "patrol", "walk", "look", "alert" }, Names(graph));
    }

    // A move is an order of keys, which a save writes too (the file's object is written in its new order).
    [Fact]
    public void AMovedStateIsSavedInItsNewPlace()
    {
        using var app = NewGame();
        var document = RecordDocument.Open(app.Engine, "state_machine", Guard)!;
        var graph = AIGraph.Of(document)!;
        Assert.True(graph.Move("alert", 0));
        Assert.True(graph.Add(null, "rest", index: 1));
        Assert.True(document.Save());
        Assert.Equal(0, app.Records.ErrorCount);
        var states = app.Engine.Records.RawJson("state_machine", Guard)!["states"]!.AsObject();
        Assert.Equal(new[] { "alert", "rest", "patrol" }, states.Select(kv => kv.Key).ToArray());
        Assert.Equal(new[] { "walk", "look" }, states["patrol"]!["states"]!.AsObject().Select(kv => kv.Key).ToArray());
    }

    // The acceptance test's play half: the guard in the play world is in patrol > walk, then alert; the
    // edit world's guard, never simulated, is in nothing.
    [Fact]
    public void WhilePlayingTheViewShowsTheStatesTheGuardIsIn()
    {
        using var app = NewGame();
        var edit = app.App.CreateEditWorld("edit");
        var document = new EditDocument(edit);
        Assert.True(document.Open(new RecordId("game", "post")));
        var session = new PlaySession(document);

        var records = new RecordEditor(app.Engine);
        var cvars = app.Engine.CVars;
        records.Register(cvars);
        AIGraphCommands.Register(cvars, records, () => session.World ?? edit);
        Assert.True(cvars.Execute("ed_rec_open state_machine guard"));
        var graph = AIGraph.Of(records.Current)!;

        var editGuard = Assert.Single(graph.Runners(edit));
        Assert.Empty(graph.Active(edit, editGuard));

        var play = session.Play()!;
        var guard = Assert.Single(graph.Runners(play));
        play.RunFixed(Dt);
        Assert.Equal(new[] { "states.patrol", "states.patrol.states.walk" }, graph.Active(play, guard));

        Assert.True(StateMachines.SetState(play, guard, "alert"));
        Assert.Equal(new[] { "states.alert" }, graph.Active(play, guard));
        using (var log = new CaptureSink())
        {
            cvars.Execute("ed_ai_active guard");
            Assert.Contains(log.Entries.Select(e => e.Message), m => m.Contains(": alert"));
        }
        Assert.Empty(graph.Active(edit, editGuard));
        session.Stop();
    }

    [Fact]
    public void TheConsoleAddsMovesAndUndoesANodeOfTheOpenRecord()
    {
        using var app = NewGame();
        var records = new RecordEditor(app.Engine);
        var cvars = app.Engine.CVars;
        records.Register(cvars);
        AIGraphCommands.Register(cvars, records, () => null);
        Assert.True(cvars.Execute("ed_rec_open state_machine guard"));

        cvars.Execute("ed_ai_add - search 1");
        cvars.Execute("ed_ai_reparent search patrol");
        var graph = AIGraph.Of(records.Current)!;
        Assert.Equal(new[] { "patrol", "walk", "look", "search", "alert" }, Names(graph));
        using (var log = new CaptureSink())
        {
            cvars.Execute("ed_ai_tree");
            // The capture sees every parallel test's log, so pick the tree out by its content.
            string tree = log.Entries.Select(e => e.Message).Single(m => m.Contains("* patrol"));
            Assert.Contains("* patrol", tree);
            Assert.Contains("    search", tree);
            Assert.Contains("-> alert  on Alarm", tree);
        }
        cvars.Execute("ed_rec_undo 2");
        Assert.Equal(new[] { "patrol", "walk", "look", "alert" }, Names(AIGraph.Of(records.Current)!));
    }

    private const string Schedules = """
    [
      { "type": "ai_schedule", "id": "patrol", "tasks": [ "FaceTarget", { "task": "Wait", "seconds": 1.5 }, { "task": "MoveToTarget", "distance": 2 } ],
        "interrupts": ["SeeEnemy"] },
      { "type": "routine", "id": "smith", "entries": [ { "from": 8, "to": 20, "schedule": "patrol", "at": "forge" } ] },
      { "type": "prefab", "id": "watcher", "name": "watcher", "components": { "ai_state": { "schedule": "patrol", "taskIndex": 2 } } }
    ]
    """;

    [Fact]
    public void AScheduleIsAListOfTasksEditedWithUndoAndTheTaskAnAgentIsOnIsActive()
    {
        using var app = HeadlessApp.Gameplay().File("data/ai.json", Schedules).Boot();
        var document = RecordDocument.Open(app.Engine, "ai_schedule", new RecordId("sage", "patrol"))!;
        var graph = AIGraph.Of(document)!;
        Assert.False(graph.IsTree);
        Assert.Equal(new[] { "FaceTarget", "Wait", "MoveToTarget" }, Names(graph));
        Assert.Equal("Wait  seconds 1.5", graph.Nodes[1].Label);
        Assert.Equal("tasks[2]", graph.Find("#2")!.Path);

        var watcher = app.World.Spawn(new RecordId("sage", "watcher"));
        Assert.Equal(new[] { watcher }, graph.Runners(app.World));
        Assert.Equal(new[] { "tasks[2]" }, graph.Active(app.World, watcher));

        Assert.True(graph.Add(null, "FaceTarget", index: 0));
        Assert.True(graph.Move("2", 1));                                         // Wait, to second
        Assert.True(graph.Rename("0", "Turn"));
        Assert.Equal(new[] { "Turn", "Wait", "FaceTarget", "MoveToTarget" }, Names(graph));
        Assert.Equal(1.5, (double?)document.Get("tasks[1].seconds"));
        Assert.False(graph.Reparent("1", "0"));                                   // a list has no parents
        Assert.True(graph.Remove("#0"));
        Assert.Equal(new[] { "Wait", "FaceTarget", "MoveToTarget" }, Names(graph));
        for (int i = 0; i < 4; i++) Assert.True(document.Undo());
        Assert.Equal(new[] { "FaceTarget", "Wait", "MoveToTarget" }, Names(graph));
        Assert.False(document.Dirty);

        var routine = AIGraph.Of(RecordDocument.Open(app.Engine, "routine", new RecordId("sage", "smith")))!;
        Assert.Equal("08:00-20:00  patrol @ forge", Assert.Single(routine.Nodes).Label);
        Assert.True(routine.Add(null, "patrol"));
        Assert.Equal(24, (int?)routine.Document.Get("entries[1].to"));
    }
}
