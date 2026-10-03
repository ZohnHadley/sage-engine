#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The rest of the logic set (issue #281): counter arithmetic, multisource, logic_case (with its random
// picks), logic_auto, trigger_once / trigger_multiple, spawners and templates, timers' random bounds,
// TimerFire and TimerAdd, and tweens that loop, yoyo and play sequences.
public class LogicSetTests
{
    public LogicSetTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private sealed class Heard
    {
        public readonly List<(long Tick, string Parameter, Entity Activator)> List = new();
        public IEnumerable<string> Parameters => List.Select(a => a.Parameter);
        public int Count(string parameter) => List.Count(a => a.Parameter == parameter);
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static void Fire(HeadlessApp app, string command, int ticks = 2)
    {
        Assert.True(app.Engine.CVars.Execute(command, ExecSource.Code), command);
        Tick(app.World, ticks);
    }

    private const string Scene = """
    [
      { "type": "prefab", "id": "thing", "name": "thing" },
      { "type": "prefab", "id": "counter", "name": "counter", "parts": { "logic_counter": { "start": 2, "min": 0, "max": 100 } } },
      { "type": "prefab", "id": "colours", "name": "colours", "parts": { "logic_case": { "cases": ["red", "green", "3"], "seed": 11 } } },
      { "type": "prefab", "id": "dice", "name": "dice", "parts": { "logic_case": { "choices": 6, "seed": 5 } } },
      { "type": "prefab", "id": "plank", "name": "plank" },
      { "type": "prefab", "id": "pulley", "name": "pulley",
        "parts": { "tween": { "channel": "Position", "target": [0, 2, 0], "relative": true, "duration": 1, "ease": "Linear",
                              "loop": "PingPong", "loops": 3 } } },
      { "type": "prefab", "id": "lift", "name": "lift",
        "parts": { "tween": { "ease": "Linear", "loop": "Restart", "loops": 2,
                              "sequence": ["offset 0 1 0 0.5", "offset 1 0 0 0.5", "nonsense words"] } } },
      { "type": "scene", "id": "logic",
        "place": [
          { "prefab": "thing", "at": [0, 0, 0], "name": "log" },
          { "prefab": "counter", "at": [0, 0, 0], "name": "counter",
            "outputs": [ { "output": "OnChanged", "target": "log", "input": "Record" },
                         { "output": "OnHitMax", "target": "log", "input": "Record", "parameter": "max" } ] },
          { "prefab": "sage:logic_multisource", "at": [0, 0, 0], "name": "gate",
            "overrides": { "parts": { "logic_multisource": { "sources": 3 } } },
            "outputs": [ { "output": "OnAllSet", "target": "log", "input": "Record", "parameter": "all" },
                         { "output": "OnNotAllSet", "target": "log", "input": "Record", "parameter": "notall" },
                         { "output": "OnTrue", "target": "log", "input": "Record", "parameter": "true" },
                         { "output": "OnFalse", "target": "log", "input": "Record", "parameter": "false" } ] },
          { "prefab": "colours", "at": [0, 0, 0], "name": "colours",
            "outputs": [ { "output": "OnCase01", "target": "log", "input": "Record", "parameter": "case1" },
                         { "output": "OnCase02", "target": "log", "input": "Record", "parameter": "case2" },
                         { "output": "OnCase03", "target": "log", "input": "Record", "parameter": "case3" },
                         { "output": "OnDefault", "target": "log", "input": "Record" } ] },
          { "prefab": "dice", "at": [0, 0, 0], "name": "dice",
            "outputs": [ { "output": "OnCase01", "target": "log", "input": "Record", "parameter": "d1" },
                         { "output": "OnCase02", "target": "log", "input": "Record", "parameter": "d2" },
                         { "output": "OnCase03", "target": "log", "input": "Record", "parameter": "d3" },
                         { "output": "OnCase04", "target": "log", "input": "Record", "parameter": "d4" },
                         { "output": "OnCase05", "target": "log", "input": "Record", "parameter": "d5" },
                         { "output": "OnCase06", "target": "log", "input": "Record", "parameter": "d6" } ] },
          { "prefab": "sage:logic_auto", "at": [0, 0, 0], "name": "auto",
            "outputs": [ { "output": "OnMapSpawn", "target": "log", "input": "Record", "parameter": "spawned" } ] },
          { "prefab": "sage:logic_spawner", "at": [10, 0, 0], "yaw": 90, "name": "maker",
            "overrides": { "parts": { "spawner": {
              "limit": 2, "uniqueNames": true,
              "place": [
                { "prefab": "plank", "at": [0, 0, -2], "name": "left",
                  "outputs": [ { "output": "OnUse", "target": "right", "input": "Record", "parameter": "pair" },
                               { "output": "OnUse", "target": "log", "input": "Record", "parameter": "outside" } ] },
                { "prefab": "plank", "at": [0, 1, -2], "yaw": 45, "name": "right" }
              ] } } },
            "outputs": [ { "output": "OnSpawned", "target": "log", "input": "Record" } ] },
          { "prefab": "pulley", "at": [0, 0, 5], "name": "pulley",
            "outputs": [ { "output": "OnTweenLoop", "target": "log", "input": "Record" },
                         { "output": "OnTweenDone", "target": "log", "input": "Record", "parameter": "pulley done" } ] },
          { "prefab": "lift", "at": [0, 0, 8], "name": "lift",
            "outputs": [ { "output": "OnTweenStep", "target": "log", "input": "Record" },
                         { "output": "OnTweenDone", "target": "log", "input": "Record", "parameter": "lift done" } ] }
        ] }
    ]
    """;

    // The logic_auto's OnMapSpawn is heard on the first tick, then forgotten unless `keep`.
    private static HeadlessApp SceneApp(Heard log, bool keep = false)
    {
        var app = HeadlessApp.Bare().WithEngineContent().With(new PhysicsModule(), new EntityIOModule())
            .File("data/logic_set.json", Scene)
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
                log.List.Add((world.Tick, io.Self.Name == "log" ? io.Parameter : $"{io.Self.Name}:{io.Parameter}", io.Activator))))
            .StartScene("sage:logic").Boot("logic");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        Assert.Equal(0, app.Records.ErrorCount);
        Tick(app.World);
        if (!keep) log.List.Clear();
        return app;
    }

    // ---- counter arithmetic ---------------------------------------------------------------------------

    [Fact]
    public void ACounterMultipliesDividesAndTakesNewLimits()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        var counter = app.World.FindByName("counter");
        Fire(app, "ent_fire counter Multiply 5");
        Assert.Equal(10f, app.World.Get<LogicCounter>(counter).Value);
        Fire(app, "ent_fire counter Divide 4");
        Assert.Equal(2.5f, app.World.Get<LogicCounter>(counter).Value);
        Fire(app, "ent_fire counter Divide 0");                            // refused, not infinity
        Assert.Equal(2.5f, app.World.Get<LogicCounter>(counter).Value);
        Fire(app, "ent_fire counter Multiply 100");                        // clamped to max 100
        Assert.Equal(100f, app.World.Get<LogicCounter>(counter).Value);
        Assert.Equal(1, log.Count("max"));
        Fire(app, "ent_fire counter SetMaxValue 50");                      // the count follows the new max
        Assert.Equal(50f, app.World.Get<LogicCounter>(counter).Value);
        Assert.Equal(2, log.Count("max"));
        Fire(app, "ent_fire counter SetMinValue 60");                      // min above max: no limits
        Assert.False(app.World.Get<LogicCounter>(counter).IsLimited());
        Assert.Equal(new[] { "10", "2.5", "100", "max", "50", "max" }, log.Parameters);
    }

    // ---- multisource ----------------------------------------------------------------------------------

    [Fact]
    public void AMultisourceFiresWhenEverySourceIsSet_AndAgainWhenOneIsCleared()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        Fire(app, "ent_fire gate SetSource 1");
        Fire(app, "ent_fire gate SetSource 3");
        Fire(app, "ent_fire gate Test");
        Assert.Equal(new[] { "false" }, log.Parameters);
        Fire(app, "ent_fire gate SetSource 2");
        Fire(app, "ent_fire gate SetSource 2");                            // already set: no second edge
        Fire(app, "ent_fire gate Test");
        Assert.Equal(new[] { "false", "all", "true" }, log.Parameters);
        Fire(app, "ent_fire gate ToggleSource 3");
        Fire(app, "ent_fire gate ClearSource 1");
        Fire(app, "ent_fire gate SetSource 7");                            // no such source: refused
        Fire(app, "ent_fire gate ToggleSource 3");
        Fire(app, "ent_fire gate SetSource 1");
        Assert.Equal(new[] { "false", "all", "true", "notall", "all" }, log.Parameters);
        Fire(app, "ent_fire gate Reset");
        Assert.Equal(0u, app.World.Get<LogicMultisource>(app.World.FindByName("gate")).Set);
        Assert.Equal("notall", log.Parameters.Last());
    }

    // ---- case -----------------------------------------------------------------------------------------

    [Fact]
    public void ACaseFiresTheFirstMatch_AsTextOrNumber_ElseDefault()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        Fire(app, "ent_fire colours InValue GREEN");
        Fire(app, "ent_fire colours InValue 3.0");
        Fire(app, "ent_fire colours InValue blue");
        Fire(app, "ent_fire colours InValue red");
        Assert.Equal(new[] { "case2", "case3", "blue", "case1" }, log.Parameters);
        Assert.Equal("OnCase16", LogicGates.CaseOutput(15));
    }

    // PickRandom is the entity's own saved stream: two runs pick the same, and a save picks on as it would
    // have. PickRandomShuffle deals every choice once before any repeats.
    [Fact]
    public void RandomPicksAreDeterministic_AcrossRunsAndASave_AndAShuffleDealsEachOnce()
    {
        List<string> Run(bool saveAndLoad)
        {
            var log = new Heard();
            using var app = SceneApp(log);
            for (int i = 0; i < 10; i++) Fire(app, "ent_fire dice PickRandom");
            if (saveAndLoad)
            {
                Assert.True(app.Engine.Saves.Save("dice"));
                for (int i = 0; i < 5; i++) Fire(app, "ent_fire dice PickRandom");
                Assert.True(app.Engine.Saves.Load("dice"));
                log.List.RemoveRange(10, log.List.Count - 10);
            }
            for (int i = 0; i < 20; i++) Fire(app, "ent_fire dice PickRandom");
            Tick(app.World);
            return log.Parameters.ToList();
        }

        var first = Run(false);
        Assert.Equal(30, first.Count);
        Assert.True(first.Distinct().Count() >= 4, string.Join(",", first));
        Assert.Equal(first, Run(false));
        Assert.Equal(first, Run(true));

        var shuffled = new Heard();
        using var dealt = SceneApp(shuffled);
        for (int i = 0; i < 12; i++) Fire(dealt, "ent_fire dice PickRandomShuffle", 1);
        Tick(dealt.World);
        var picks = shuffled.Parameters.ToList();
        Assert.Equal(12, picks.Count);
        Assert.Equal(6, picks.Take(6).Distinct().Count());
        Assert.Equal(6, picks.Skip(6).Distinct().Count());
    }

    // ---- auto -----------------------------------------------------------------------------------------

    [Fact]
    public void ALogicAutoFiresOnce_AndNotAgainAfterALoad()
    {
        var log = new Heard();
        using var app = SceneApp(log, keep: true);
        Tick(app.World, 5);
        Assert.Equal(1, log.Count("spawned"));
        Assert.True(app.Engine.Saves.Save("auto"));
        Assert.True(app.Engine.Saves.Load("auto"));
        Tick(app.World, 5);
        Assert.Equal(1, log.Count("spawned"));
        Assert.True(app.World.Get<LogicAuto>(app.World.FindByName("auto")).Fired);
    }

    // ---- trigger --------------------------------------------------------------------------------------

    // trigger_once fires once and disables itself (Enable re-arms it); trigger_multiple waits between;
    // `requires` filters by what entered. Delivered as TriggerOutputSystem would, after OnStartTouch.
    [Fact]
    public void ATriggerFiresOnceOrAfterItsWait_ForWhatPassesItsFilter()
    {
        var log = new Heard();
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule())
            .File("data/t.json", """
                [{ "type": "prefab", "id": "once", "parts": { "trigger": { "once": true } } },
                 { "type": "prefab", "id": "multi", "parts": { "trigger": { "wait": 0.5, "requires": { "var": "alarm", "eq": 0 } } } }]
                """)
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
                log.List.Add((world.Tick, io.Parameter, io.Activator))))
            .Boot("t");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var once = world.Spawn(new RecordId("sage", "once"));
        var multi = world.Spawn(new RecordId("sage", "multi"));
        Assert.False(once.IsNull || multi.IsNull);
        world.Add(once, new IOConnections { Wires = new[] { new Connection { Output = "OnTrigger", Target = "!self", Input = "Record", Parameter = "once" } } });
        world.Add(multi, new IOConnections { Wires = new[] { new Connection { Output = "OnTrigger", Target = "!self", Input = "Record", Parameter = "multi" } } });
        var visitor = world.Create(Transform.Identity, "visitor");

        for (int i = 0; i < 3; i++)
        {
            LogicGates.Touched(world, once, visitor);
            LogicGates.Touched(world, multi, visitor);
            Tick(world, 10);
        }
        Assert.Equal(1, log.Count("once"));
        Assert.Equal(1, log.Count("multi"));                               // 20 ticks < 0.5 s wait
        Assert.Equal(visitor, log.List[0].Activator);
        Tick(world, 30);
        LogicGates.Touched(world, multi, visitor);
        Tick(world);
        Assert.Equal(2, log.Count("multi"));

        Vars.Of(world).Set("alarm", 1);                                    // the filter fails: nothing, no wait spent
        Tick(world, 40);
        LogicGates.Touched(world, multi, visitor);
        Tick(world);
        Assert.Equal(2, log.Count("multi"));
        Assert.Equal(0f, world.Get<LogicTrigger>(multi).Cooldown);

        world.IO().FireInput(once, "Enable");
        Tick(world);
        LogicGates.Touched(world, once, visitor);
        Tick(world);
        Assert.Equal(2, log.Count("once"));
    }

    // ---- spawner --------------------------------------------------------------------------------------

    [Fact]
    public void ASpawnerPlacesItsTemplateWhereItStands_WithNamesAndWiresFixedUp_UpToItsLimit_AndASaveKeepsIt()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        var world = app.World;
        Fire(app, "ent_fire maker Spawn");
        Fire(app, "ent_fire maker Spawn");
        Fire(app, "ent_fire maker Spawn");                                 // limit 2
        Assert.Equal(new[] { "1", "2" }, log.Parameters);
        var left1 = world.FindByName("left#1");
        Assert.False(left1.IsNull || world.FindByName("right#1").IsNull || world.FindByName("left#2").IsNull || world.FindByName("right#2").IsNull);
        Assert.True(world.FindByName("left").IsNull);
        Assert.Equal(left1, log.List[0].Activator);                        // OnSpawned's activator is what it spawned

        // Measured from the spawner, turned as it is: it faces -X (yaw 90), so its -Z is the world's -X.
        var at = world.Get<Transform>(left1).LocalPosition;
        Assert.Equal(8f, at.X, 3);
        Assert.Equal(0f, at.Z, 3);
        Assert.Equal(1f, world.Get<Transform>(world.FindByName("right#1")).LocalPosition.Y, 3);

        // The pair's wire reaches its own copy; a wire outside the template is left alone.
        world.FireOutput(world.FindByName("left#2"), "OnUse");
        Tick(world, 2);
        Assert.Contains("right#2:pair", log.Parameters);
        Assert.DoesNotContain("right#1:pair", log.Parameters);
        Assert.Contains("outside", log.Parameters);

        // Saved: what it spawned with its names and wires, and its count (so the limit holds).
        Assert.True(app.Engine.Saves.Save("made"));
        Assert.True(app.Engine.Saves.Load("made"));
        Assert.Equal(2, world.Get<LogicSpawner>(world.FindByName("maker")).Spawned);
        Assert.False(world.FindByName("right#1").IsNull);
        log.List.Clear();
        world.FireOutput(world.FindByName("left#1"), "OnUse");
        Tick(world, 2);
        Assert.Contains("right#1:pair", log.Parameters);
        Fire(app, "ent_fire maker Spawn");
        Assert.DoesNotContain("3", log.Parameters);
    }

    // ---- timers ---------------------------------------------------------------------------------------

    [Fact]
    public void ATimersRandomBoundsTimerFireAndTimerAdd()
    {
        var log = new Heard();
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule())
            .File("data/t.json", """[{ "type": "prefab", "id": "flicker", "parts": { "timer": { "randomMin": 0.2, "randomMax": 0.6, "seed": 3 } } }]""")
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
                log.List.Add((world.Tick, io.Parameter, io.Activator))))
            .Boot("t");
        var world = app.World;
        var flicker = world.Spawn(new RecordId("sage", "flicker"));
        var timer = world.Get<LogicTimer>(flicker);
        Assert.Equal(0.4f, timer.Interval, 4);
        Assert.Equal(0.2f, timer.Spread, 4);
        world.Add(flicker, new IOConnections { Wires = new[] { new Connection { Output = "OnTimer", Target = "!self", Input = "Record", Parameter = "tick" } } });

        world.IO().FireInput(flicker, "TimerStart");
        Tick(world, 600);
        var ticks = log.List.Select(a => a.Tick).ToList();
        var gaps = ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps.Count > 10);
        Assert.All(gaps, g => Assert.InRange(g, 12, 36));                  // 0.2 to 0.6 s
        Assert.True(gaps.Distinct().Count() > 3);

        world.IO().FireInput(flicker, "TimerStop");
        Tick(world);
        log.List.Clear();
        world.IO().FireInput(flicker, "TimerFire");                         // fires though stopped
        Tick(world, 2);
        Assert.Equal(1, log.Count("tick"));

        world.IO().FireInput(flicker, "TimerStart", "1");                  // a second's wait, then 2 more
        world.IO().FireInput(flicker, "TimerAdd", "2", delay: 0.01f);
        Tick(world, 150);
        Assert.Equal(1, log.Count("tick"));
        Tick(world, 40);
        Assert.Equal(2, log.Count("tick"));
    }

    // ---- tweens ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("offset 0 1 0 2 loop", TweenLoop.Restart)]
    [InlineData("pingpong", TweenLoop.PingPong)]
    [InlineData("YOYO linear", TweenLoop.PingPong)]
    [InlineData("1 once", TweenLoop.Once)]
    [InlineData("", TweenLoop.Restart)]
    public void TweenToReadsALoopWord(string parameter, TweenLoop expected)
    {
        var tween = new Tween { Duration = 1f, Loop = TweenLoop.Restart };
        Assert.True(Tweens.TryRead(parameter, tween, out _, out _, out _, out _, out _, out var loop), parameter);
        Assert.Equal(expected, loop);
    }

    // PingPong up and down, three plays in all: OnTweenLoop after the first two, OnTweenDone after the
    // third, at the top; a save half-way through the second play ends where and when it would have.
    [Fact]
    public void ATweenPingPongsForItsLoops_AndASaveKeepsItsPlace()
    {
        (List<float> Path, List<string> Heard) Run(bool saveAndLoad)
        {
            var log = new Heard();
            using var app = SceneApp(log);
            var world = app.World;
            var pulley = world.FindByName("pulley");
            world.IO().FireInput(pulley, "TweenTo");
            var path = new List<float>();
            for (int i = 0; i < 200; i++)
            {
                if (saveAndLoad && i == 90)
                {
                    long savedAt = world.Tick;
                    Assert.True(app.Engine.Saves.Save("pulley"));
                    Tick(world, 17);                                    // a future thrown away
                    Assert.True(app.Engine.Saves.Load("pulley"));
                    pulley = world.FindByName("pulley");
                    log.List.RemoveAll(h => h.Tick > savedAt);
                }
                Tick(world);
                path.Add(world.Get<Transform>(pulley).LocalPosition.Y);
            }
            return (path, log.Parameters.Where(p => p != "spawned").ToList());
        }

        var a = Run(false);
        Assert.Equal(new[] { "1", "2", "pulley done" }, a.Heard);
        Assert.Equal(0f, a.Path[0], 3);                                     // delivered, then moving
        Assert.Equal(2f, a.Path[60], 3);                                    // up in a second
        Assert.Equal(1f, a.Path[90], 1);                                    // half-way down
        Assert.Equal(0f, a.Path[120], 3);                                   // down again
        Assert.Equal(2f, a.Path[180], 3);                                   // and up: three plays
        Assert.Equal(2f, a.Path[^1], 3);
        var saved = Run(true);
        Assert.Equal(a.Path, saved.Path);
        Assert.Equal(a.Heard, saved.Heard);
    }

    // A sequence: each step from where the last left it, OnTweenStep as each arrives (an unreadable step
    // skipped), and the whole list again for `loops: 2`; then OnTweenDone. TweenStop ends it.
    [Fact]
    public void ATweenSequencePlaysItsStepsInOrder_AndLoops()
    {
        var log = new Heard();
        using var app = SceneApp(log);
        var world = app.World;
        var lift = world.FindByName("lift");
        Fire(app, "ent_fire lift TweenPlay", 1);
        Tick(world, 30);
        Assert.Equal(new Vector3(0, 1, 8), world.Get<Transform>(lift).LocalPosition);
        Tick(world, 30);
        Assert.Equal(new Vector3(1, 1, 8), world.Get<Transform>(lift).LocalPosition);
        Tick(world, 61);
        Assert.Equal(new Vector3(2, 2, 8), world.Get<Transform>(lift).LocalPosition);   // relative steps climb
        Tick(world, 5);
        Assert.Equal(new[] { "1", "2", "1", "2", "lift done" }, log.Parameters.Where(p => p != "spawned"));
        Assert.False(world.Get<Tween>(lift).Playing);

        Fire(app, "ent_fire lift TweenPlay loop", 1);
        Tick(world, 20);
        Fire(app, "ent_fire lift TweenStop", 1);
        var stopped = world.Get<Transform>(lift).LocalPosition;
        Tick(world, 60);
        Assert.Equal(stopped, world.Get<Transform>(lift).LocalPosition);
        Assert.False(world.Get<Tween>(lift).Sequencing);
    }

    [Fact]
    public void TheNewLogicEntitiesAreTheEngines_WithPrefabs()
    {
        using var bare = HeadlessApp.Bare().WithEngineContent().Boot("bare");
        var engine = bare.Engine;
        foreach (var input in new[] { "Multiply", "Divide", "SetMaxValue", "SetMinValue", "SetSource", "ClearSource", "ToggleSource",
                                      "InValue", "PickRandom", "PickRandomShuffle", "Spawn", "TimerFire", "TimerAdd", "TweenPlay" })
            Assert.True(engine.Inputs.Has(input), input);
        foreach (var output in new[] { "OnAllSet", "OnNotAllSet", "OnCase01", "OnCase16", "OnDefault", "OnMapSpawn", "OnSpawned",
                                       "OnTweenLoop", "OnTweenStep" })
            Assert.True(engine.Outputs.Has(output), output);
        foreach (var (prefab, has) in new (string, Func<World, Entity, bool>)[]
                 {
                     ("logic_multisource", (w, e) => w.Has<LogicMultisource>(e)), ("logic_case", (w, e) => w.Has<LogicCase>(e)),
                     ("logic_auto", (w, e) => w.Has<LogicAuto>(e)), ("logic_spawner", (w, e) => w.Has<LogicSpawner>(e)),
                 })
        {
            var entity = bare.World.Spawn(new RecordId("sage", prefab));
            Assert.True(has(bare.World, entity), prefab);
        }
        Assert.Equal("sage.core", engine.Registrations.OwnerOf("entity input", "Spawn@sage:spawner"));
        Assert.Equal(0, bare.Records.ErrorCount);
    }
}
