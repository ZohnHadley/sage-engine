#nullable enable
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// REDESIGN §5 phase 4b's exit criterion (issue #94): games with no C#. tests/games/scripted-sequence runs a
// chain (trigger, door, lift, NPC line, counter to 3, relay) from wiring alone, and tests/games/topics
// answers one conditional topic. Each is loaded from its directory headlessly, like camera-cut.
public class ExitGameTests
{
    public ExitGameTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static string Game(string name) => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", name);

    private static void Step(World world, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    private static string[] Said(World world) => world.Messages().Messages.ToArray().Select(m => m.Text).ToArray();

    private static void AssertValid(string game)
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Game(game),
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("scene.json") || w.Contains("topics.json"));
    }

    [Fact]
    public void TheScriptedSequenceGameValidates() => AssertValid("scripted-sequence");

    [Fact]
    public void TheTopicsGameValidates() => AssertValid("topics");

    // A trigger opens the door, the door's OnFullyOpen raises the lift, the lift's OnFullyOpen has the
    // keeper speak, and the counter every step feeds reaches 3 and fires the relay.
    [Fact]
    public void ATriggerDoorLiftNpcCounterAndRelayRun_InAGameWithNoCode()
    {
        using var app = HeadlessApp.ForGame(Game("scripted-sequence")).WithEngineContent().Boot();
        var world = app.World;
        Assert.Null(app.Engine.Modules.Game);
        var space = world.Resources.Get<IPhysicsWorld>();
        var plate = world.FindByName("plate");
        var door = world.FindByName("door");
        var lift = world.FindByName("lift");
        var steps = world.FindByName("steps");
        Assert.False(plate.IsNull || door.IsNull || lift.IsNull || steps.IsNull || world.FindByName("keeper").IsNull);

        // The crate falls through the plate: nothing has moved until then.
        int tick = 0;
        bool touched = false;
        while (!touched && tick < 180)
        {
            Assert.Equal(0f, world.Get<Mover>(door).Position);
            world.RunFixed(Dt);
            tick++;
            foreach (var overlap in space.TriggerEnter)
                if (overlap.Trigger == plate) touched = true;
            world.RunFrame(Dt, 1f);
        }
        Assert.True(touched, "the crate never fell through the plate");

        // The door opens (a second), and only then does the lift start (chained from OnFullyOpen).
        Step(world, 3);
        Assert.True(world.Get<Mover>(door).Direction > 0 || world.Get<Mover>(door).Position > 0);
        Assert.Equal(0f, world.Get<Mover>(lift).Position);
        Assert.Equal(1f, world.Get<LogicCounter>(steps).Value);

        int wait = 0;
        while (world.Get<Mover>(door).Position < 1f && wait < 300) { Step(world); wait++; }
        Assert.Equal(1f, world.Get<Mover>(door).Position);
        Step(world, 5);
        Assert.True(world.Get<Mover>(lift).Position > 0f, "the lift did not start when the door finished opening");
        Assert.Equal(2f, world.Get<LogicCounter>(steps).Value);
        Assert.Null(DialogueRules.Current(world));                 // nobody speaks until the lift arrives
        Assert.Equal(0, Vars.Of(world).Get("sequence_done"));

        wait = 0;
        while (world.Get<Mover>(lift).Position < 1f && wait < 400) { Step(world); wait++; }
        Assert.Equal(1f, world.Get<Mover>(lift).Position);
        Step(world, 6);

        // The keeper speaks, the counter hit 3 and the relay fired.
        Assert.Equal("The way up is open.", DialogueRules.Current(world)?.Text);
        Assert.Equal(3f, world.Get<LogicCounter>(steps).Value);
        Assert.Equal(1, Vars.Of(world).Get("sequence_done"));
        Assert.Contains(Said(world), m => m == "The sequence is done.");
    }

    // One topic, two answers: the first info whose `requires` holds. A relay in the scene raises the river.
    [Fact]
    public void AConditionalTopicAnswersByAVar_InAGameWithNoCode()
    {
        using var app = HeadlessApp.ForGame(Game("topics")).WithEngineContent().Boot();
        var world = app.World;
        Assert.Null(app.Engine.Modules.Game);
        var ferryman = world.FindByName("ferryman");
        var player = Scenes.Player(world);
        Assert.False(ferryman.IsNull || player.IsNull);
        var river = new RecordId("topicsgame", "the_river");

        Assert.Equal("Shallow. Cross when you like.", DialogueTopics.Ask(world, ferryman, player, river)?.Text);
        Assert.True(app.Engine.CVars.Execute("ent_fire rain Trigger", ExecSource.Code));
        Step(world, 3);
        Assert.Equal("Too high to cross today.", DialogueTopics.Ask(world, ferryman, player, river)?.Text);
        Assert.Equal(1, Vars.Of(world).Get("warned"));             // the answer's `then` ran
    }
}
