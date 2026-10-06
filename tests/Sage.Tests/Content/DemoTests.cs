#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Demos (issue #333, docs/design/08 §7): one PlayerCommand per tick recorded to a `.sagedemo` with a header
// and the save it starts from, and played back through PlayerInput. In tests/games/saves (a player with a
// sword, goblins to hit): a session recorded in one app replays in a fresh one to the same world hash; a
// demo from another build or with other mods is refused, saying why, and nothing changes; a demo cut short
// plays what is whole of it, and one cut short before its first tick is refused.
#pragma warning disable SAGE0131   // saves you can trust (phase 4i)
public class DemoTests
{
    public DemoTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private static readonly RecordId Sword = new("saves", "sword");
    private static readonly RecordId Health = new("sage", "health");

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "saves");

    private static HeadlessApp Boot(string demos)
    {
        var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        app.Engine.Demos.Root = demos;
        Step(app.World, 3);
        return app;
    }

    // A tick and a frame, as the host runs them.
    private static void Step(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunFixed(Dt);
            world.RunFrame(Dt, 1f);
        }
    }

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity Goblin(World world) =>
        Assert.Single(world.QueryAll().Entities.ToEntityList().Where(e => e.Name == "doomed goblin").ToArray());

    // What the host does before each tick: the command for it, sampled.
    private static void Command(HeadlessApp app, Vector2 move, float yaw, string? press = null, string? hold = null)
    {
        var world = app.World;
        var input = world.Resources.Get<PlayerInput>();
        input.HasCommand = true;
        input.Command = new PlayerCommand
        {
            Tick = world.Tick + 1, Move = move, ViewYaw = yaw, ViewPitch = -0.2f,
            Pressed = press == null ? default : default(ActionMask).With(app.Engine.Actions.Get(press)),
            Held = hold == null ? default : default(ActionMask).With(app.Engine.Actions.Get(hold)),
        };
        Step(world);
    }

    // A scripted session: a walk south-west, a turn, a walk at the goblin and swings at it, and standing.
    private static void Play(HeadlessApp app)
    {
        for (int i = 0; i < 40; i++) Command(app, new Vector2(0.4f, 1f), 0.6f, hold: i < 10 ? "Crouch" : null);
        for (int i = 0; i < 30; i++) Command(app, Vector2.Zero, 0.6f - i * 0.02f);
        var world = app.World;
        for (int i = 0; i < 40; i++)
        {
            var to = world.Get<Transform>(Goblin(world)).LocalPosition - world.Get<Transform>(Player(world)).LocalPosition;
            Command(app, to.Length() > 1.6f ? new Vector2(0, 1) : Vector2.Zero, SageMath.YawOf(to));
        }
        for (int swing = 0; swing < 3; swing++)
        {
            var to = world.Get<Transform>(Goblin(world)).LocalPosition - world.Get<Transform>(Player(world)).LocalPosition;
            Command(app, Vector2.Zero, SageMath.YawOf(to), press: "Attack");
            for (int i = 0; i < 60; i++) Command(app, Vector2.Zero, SageMath.YawOf(to));
        }
    }

    // Set up in front of the goblin with the sword, then recorded: the demo and the state it ended in.
    private static (ulong Hash, Vector3 Player, float Goblin, long Ticks) Record(string demos, string name)
    {
        using var app = Boot(demos);
        var world = app.World;
        var player = Player(world);
        Assert.True(world.Equip(player, Sword));
        var goblin = world.Get<Transform>(Goblin(world)).LocalPosition;
        world.Teleport(player, Transform.At(new Vector3(goblin.X + 3, 0.1f, goblin.Z + 4)));
        Step(world, 10);

        Assert.True(app.CVars.Execute($"record {name}"));
        Assert.True(app.Engine.Demos.IsRecording, app.Engine.Demos.LastError);
        Play(app);
        Assert.True(app.CVars.Execute("stop"));
        Assert.False(app.Engine.Demos.IsRecording);
        return (WorldHash.Of(world), world.Get<Transform>(Player(world)).LocalPosition,
                world.Attribute(Goblin(world), Health), world.Tick);
    }

    // Plays `name` in `app` to its end, a tick and a frame at a time as the host would.
    private static DemoResult PlayToEnd(HeadlessApp app, string name)
    {
        Assert.True(app.Engine.Demos.Play(name), app.Engine.Demos.LastError);
        for (int i = 0; i < 100_000 && app.Engine.Demos.IsPlaying; i++) Step(app.World);
        Assert.False(app.Engine.Demos.IsPlaying);
        return Assert.IsType<DemoResult>(app.Engine.Demos.LastResult);
    }

    // The issue's done criterion: a recorded session replays, in a fresh app that had been doing something
    // else entirely, to the same world hash — and to the same places and the same hurt goblin, so the hash
    // is not equal by having missed what the input did.
    [Xunit.Fact]
    public void ARecordedSessionReplaysToTheSameWorldHash()
    {
        string demos = TestEnv.NewTempDir();
        var recorded = Record(demos, "duel");
        Assert.True(File.Exists(Path.Combine(demos, "duel.sagedemo")));
        Assert.Empty(Directory.GetDirectories(demos));   // the start's folder is not left behind

        var file = DemoFile.Read(Path.Combine(demos, "duel.sagedemo"));
        Assert.True(file.Complete);
        Assert.Equal(recorded.Hash, file.Hash);
        Assert.Equal(40 + 30 + 40 + 3 * 61, file.Ticks.Count);
        Assert.Equal(Dt, file.Header.Dt);
        Assert.Equal("main", file.Header.World);
        Assert.Contains(file.Start, f => f.Name == "header.json");
        Assert.True(recorded.Goblin < 30f, "the swings landed");

        using var replay = Boot(demos);
        var world = replay.World;
        // Somewhere else, doing something else: the demo's start puts it back.
        world.Teleport(Player(world), Transform.At(new Vector3(-5, 0.1f, -5)));
        Step(world, 20);
        Assert.NotEqual(recorded.Hash, WorldHash.Of(world));

        Assert.True(replay.CVars.Execute("playdemo duel"));
        Assert.True(replay.Engine.Demos.IsPlaying, replay.Engine.Demos.LastError);
        for (int i = 0; i < 1000 && replay.Engine.Demos.IsPlaying; i++) Step(world);
        var result = Assert.IsType<DemoResult>(replay.Engine.Demos.LastResult);

        Assert.True(result.Matched, $"recorded {result.Expected:x16}, replayed {result.Actual:x16}");
        Assert.Equal(file.Ticks.Count, result.Played);
        Assert.Equal(recorded.Hash, WorldHash.Of(world));
        Assert.Equal(recorded.Player, world.Get<Transform>(Player(world)).LocalPosition);
        Assert.Equal(recorded.Goblin, world.Attribute(Goblin(world), Health));

        // And again, from wherever that left it: a demo is repeatable, not a one-off.
        Step(world, 15);
        Assert.True(PlayToEnd(replay, "duel").Matched);
    }

    // The Sandbox, with its AI, physics and hopping toys: a demo recorded from launch (`+record`, as a
    // regression run does) replays from launch in another app to the same hash. From launch, because what
    // a save does not keep — the order entities were made in, an AI's thinking between saves — is then the
    // same in both; recorded later in a session, the hash check is what says when it is not.
    [Xunit.Fact]
    public void ADemoRecordedAtLaunchReplaysInTheSandbox()
    {
        string demos = TestEnv.NewTempDir();
        string sandbox = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
        HeadlessApp Launch()
        {
            var app = HeadlessApp.ForGame(sandbox, new global::Sandbox.SandboxModule()).WithEngineContent().Boot();
            app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
            app.Engine.Demos.Root = demos;
            return app;
        }

        ulong recorded;
        using (var app = Launch())
        {
            Assert.True(app.CVars.Execute("record launch"));
            for (int i = 0; i < 120; i++) Command(app, new Vector2(MathF.Sin(i * 0.05f), 1f), i * 0.01f, press: i == 60 ? "Jump" : null);
            Assert.True(app.CVars.Execute("stop"));
            recorded = WorldHash.Of(app.World);
        }
        using (var app = Launch())
        {
            var result = PlayToEnd(app, "launch");
            Assert.True(result.Matched, $"recorded {result.Expected:x16}, replayed {result.Actual:x16}");
            Assert.Equal(recorded, WorldHash.Of(app.World));
        }
    }

    // The hash is over saved state: the same world twice is one hash, a moved player another.
    [Xunit.Fact]
    public void TheWorldHashFollowsTheSavedState()
    {
        using var app = Boot(TestEnv.NewTempDir());
        var world = app.World;
        ulong before = WorldHash.Of(world);
        Assert.Equal(before, WorldHash.Of(world));
        world.Teleport(Player(world), Transform.At(new Vector3(2, 0.1f, 2)));
        Assert.NotEqual(before, WorldHash.Of(world));
    }

    // A demo recorded with another build and other mods is refused, with both reasons, before anything is
    // loaded: the world is as it was.
    [Xunit.Fact]
    public void ADemoFromAnotherBuildOrWithOtherModsIsRefused()
    {
        string demos = TestEnv.NewTempDir();
        using var app = Boot(demos);
        var world = app.World;
        var header = DemoHeader.Current(app.Engine, world, Dt);
        header.Build = "0.0.1-elsewhere Debug";
        header.Mods.Add(new SavedMod("bigger_goblins", "1.0.0"));
        using (var writer = new DemoWriter(app.Engine.Demos.PathOf("foreign"), header, Array.Empty<(string, byte[])>()))
        {
            for (int i = 0; i < 10; i++) writer.Write(new DemoTick(true, new PlayerCommand { Move = new Vector2(0, 1) }));
            writer.Finish(123);
        }
        ulong before = WorldHash.Of(world);

        Assert.False(app.Engine.Demos.Play("foreign"));
        Assert.False(app.Engine.Demos.IsPlaying);
        string error = Assert.IsType<string>(app.Engine.Demos.LastError);
        Assert.Contains("0.0.1-elsewhere", error);
        Assert.Contains("bigger_goblins 1.0.0", error);
        Assert.Equal(before, WorldHash.Of(world));

        // Another game is refused too.
        header = DemoHeader.Current(app.Engine, world, Dt);
        header.Game = "another_game";
        new DemoWriter(app.Engine.Demos.PathOf("other_game"), header, Array.Empty<(string, byte[])>()).Finish(null);
        Assert.False(app.Engine.Demos.Play("other_game"));
        Assert.Contains("another_game", app.Engine.Demos.LastError);
    }

    // A demo cut short in its ticks (a crash while recording) plays the ticks it has whole and checks no
    // hash; one cut short before its first tick, or that is not a demo at all, is refused, saying so.
    [Xunit.Fact]
    public void ATruncatedDemoPlaysWhatIsWholeOrIsRefused()
    {
        string demos = TestEnv.NewTempDir();
        using var app = Boot(demos);
        var demosService = app.Engine.Demos;
        Assert.True(demosService.Record("cut"));
        for (int i = 0; i < 30; i++) Command(app, new Vector2(i % 2, 1), 0.1f * i);   // no repeats: 65 bytes a tick
        Assert.True(demosService.Stop());
        string path = demosService.PathOf("cut");
        byte[] whole = File.ReadAllBytes(path);
        var complete = DemoFile.Read(path);
        Assert.Equal(30, complete.Ticks.Count);

        // The end and half of the last tick gone: 29 ticks, no hash.
        File.WriteAllBytes(path, whole[..(whole.Length - 18 - 30)]);
        var cut = DemoFile.Read(path);
        Assert.False(cut.Complete);
        Assert.Null(cut.Hash);
        Assert.Equal(29, cut.Ticks.Count);
        var result = PlayToEnd(app, "cut");
        Assert.Equal(29, result.Played);
        Assert.Null(result.Expected);
        Assert.False(result.Matched);

        // Cut inside the start: refused, and nothing is played.
        File.WriteAllBytes(path, whole[..200]);
        Assert.False(demosService.Play("cut"));
        Assert.Contains("cut short before its first tick", demosService.LastError);
        Assert.False(demosService.IsPlaying);

        // Not a demo.
        File.WriteAllText(path, "not a demo at all");
        Assert.False(demosService.Play("cut"));
        Assert.Contains("not a demo", demosService.LastError);

        // And none by that name.
        Assert.False(demosService.Play("never_recorded"));
        Assert.Contains("no demo 'never_recorded'", demosService.LastError);
    }

    // One thing at a time: a recording refuses a second recording and a playback until `stop`.
    [Xunit.Fact]
    public void ARecordingRefusesAnotherUntilStopped()
    {
        string demos = TestEnv.NewTempDir();
        using var app = Boot(demos);
        var demosService = app.Engine.Demos;
        Assert.True(demosService.Record("one"));
        Assert.False(demosService.Record("two"));
        Assert.Contains("stop", demosService.LastError);
        Assert.False(demosService.Play("one"));
        Command(app, new Vector2(0, 1), 0f);
        Assert.True(demosService.Stop());
        Assert.False(demosService.Stop());
        Assert.Single(DemoFile.Read(demosService.PathOf("one")).Ticks);
        Assert.False(File.Exists(demosService.PathOf("two")));
    }
}
