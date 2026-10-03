#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Issue #281's done criterion: the `vault` scene of tests/games/scripted-sequence (vault.json) reproduces a Half-Life-style puzzle chain in data
// alone — logic_auto, a trigger_once with a filter, a random one-shot timer, a multisource, a
// multi_manager-style relay with delayed wires, a logic_case, a counter, a tween to the vault door, a
// looping tween sequence and a spawner whose template's wires are fixed up to each copy.
public class LogicPuzzleTests
{
    public LogicPuzzleTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static string Game => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "scripted-sequence");

    private static void Step(World world, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    private static string[] Said(World world) => world.Messages().Messages.ToArray().Select(m => m.Text).ToArray();

    [Fact]
    public void TheLogicPuzzleSceneValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Game,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("vault.json"));
    }

    [Fact]
    public void AHalfLifeStylePuzzleChainRuns_InAGameWithNoCode()
    {
        using var app = HeadlessApp.ForGame(Game).WithEngineContent().StartScene("scriptedsequence:vault").Boot();
        var world = app.World;
        Assert.Null(app.Engine.Modules.Game);
        Assert.Equal(0, app.Records.ErrorCount);
        var power = world.FindByName("power");
        var door = world.FindByName("vault door");
        var fan = world.FindByName("fan");
        var plate = world.FindByName("plate");
        Assert.False(power.IsNull || door.IsNull || fan.IsNull || plate.IsNull);
        float doorY = world.Get<Transform>(door).LocalPosition.Y;

        // The level starts: the room is armed, the fan turns, the generator counts.
        Step(world, 2);
        Assert.Contains("Restore the power.", Said(world));
        Assert.Equal(1, Vars.Of(world).Get("armed"));
        Assert.True(world.Get<Tween>(fan).Sequencing);
        Assert.True(world.Get<LogicTimer>(world.FindByName("generator")).Running);

        // Neither source alone opens anything; both (the crate lands, the generator runs) start the chain.
        int tick = 0;
        while (!world.Get<LogicMultisource>(power).Complete && tick < 240)
        {
            Assert.Equal(doorY, world.Get<Transform>(door).LocalPosition.Y);
            Assert.DoesNotContain("Power restored.", Said(world));
            Step(world);
            tick++;
        }
        Assert.True(world.Get<LogicMultisource>(power).Complete, "both sources should be set within four seconds");
        Assert.True(world.Get<LogicTrigger>(plate).Disabled);              // trigger_once: spent
        Step(world, 2);
        Assert.Contains("Power restored.", Said(world));

        // Half a second later (a delayed wire) the dial reads green (case 2), the lock hits 3 and the door
        // lifts over a second.
        var lockCounter = world.FindByName("lock");
        Step(world, 25);
        Assert.Equal(0f, world.Get<LogicCounter>(lockCounter).Value);
        Step(world, 10);
        Assert.Equal(3f, world.Get<LogicCounter>(lockCounter).Value);
        Assert.DoesNotContain("Bzzt.", Said(world));
        Step(world, 2);
        Assert.True(world.Get<Tween>(door).Playing);
        Assert.Equal(0, Vars.Of(world).Get("puzzle_done"));
        int wait = 0;
        while (world.Get<Tween>(door).Playing && wait < 120) { Step(world); wait++; }
        Assert.InRange(wait, 50, 62);                                       // the rest of a second
        Assert.Equal(doorY + 3f, world.Get<Transform>(door).LocalPosition.Y, 3);

        // The door arrived: the spawner placed its template once, with each copy's names and wires fixed up.
        Step(world, 3);
        Assert.Equal(1, Vars.Of(world).Get("puzzle_done"));
        Assert.False(world.FindByName("beacon#1").IsNull);
        Assert.False(world.FindByName("lamp#1").IsNull);
        Assert.Contains("The vault is open.", Said(world));
        Assert.Equal(1, world.Get<LogicSpawner>(world.FindByName("treasure")).Spawned);

        // And the fan is still going round: a looping sequence never says it is done.
        Assert.True(world.Get<Tween>(fan).Playing);
        Assert.True(world.Get<Tween>(fan).Played > 1);
    }
}
