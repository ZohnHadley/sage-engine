#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

#pragma warning disable SAGE0134 // ragdolls, their settling and their saves are what these test

// Ragdolls that settle, and ragdolls in saves (issue #248, docs/design/12 "As built (settling and saves,
// issue #248)"), on tests/games/skeletal's mannequin.
public class RagdollSaveTests
{
    public RagdollSaveTests() { _ = TestEnv.UserRoot; }

    internal static HeadlessApp App(string? savesRoot = null)
    {
        var app = NpcLocomotionTests.Skeletal();
        app.Engine.Saves.Root = savesRoot ?? Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    internal static Entity Npc(World world, Vector3 at, string name)
    {
        var npc = world.Spawn(NpcLocomotionTests.Npc, at);
        Assert.False(npc.IsNull);
        npc.Name = name;
        return npc;
    }

    // A counter named `name` that the entity's OnSettled adds one to.
    private static void CountSettling(World world, Entity npc, string name)
    {
        var counter = world.Create(Transform.Identity, name);
        world.Add(counter, new LogicCounter());
        world.Add(npc, new IOConnections { Wires = new[] { new Connection { Output = Ragdolls.OnSettled, Target = name, Input = "Add", Parameter = "1" } } });
    }

    private static float Count(World world, string name) => world.Get<LogicCounter>(world.FindByName(name)).Value;

    internal static Pose[] Bodies(World world, Entity npc)
    {
        var physics = world.Resources.Get<IPhysicsWorld>();
        return RagdollTests.Instance(world, npc).Bodies.Select(b => physics.PoseOf(b)).ToArray();
    }

    private static Vector3 Pelvis(World world, Entity npc) => Bodies(world, npc)[0].Position;

    private static bool AllAsleep(World world, Entity npc)
    {
        var physics = world.Resources.Get<IPhysicsWorld>();
        return RagdollTests.Instance(world, npc).Bodies.All(b => !physics.IsAwake(b));
    }

    // Steps until it settles (at most `seconds`), and says how many ticks it took.
    internal static int UntilSettled(World world, Entity npc, float seconds = 8f)
    {
        for (int t = 0; t < seconds * 60; t++)
        {
            if (Ragdolls.IsSettled(world, npc)) return t;
            Step(world);
        }
        throw new Xunit.Sdk.XunitException($"it did not settle in {seconds} s");
    }

    // Acceptance: a mannequin shoved over and saved a third of a second into its fall, loaded in a fresh
    // app and stepped as long as the run that never stopped, lands where that one did: its bodies come back
    // where they were and moving as they were, not standing.
    [Xunit.Fact]
    public void ASaveMidFallLoadsMidFall_AndLandsWhereTheUninterruptedRunDid()
    {
        using var first = App();
        var world = first.World;
        var npc = Npc(world, new Vector3(12, 0, 14), "faller");
        Step(world, 3);
        Assert.True(Ragdolls.Start(world, npc, new Vector3(0, 0, 80)));
        Step(world, 20);
        var saved = Bodies(world, npc);
        var moving = world.Resources.Get<IPhysicsWorld>().VelocityOf(RagdollTests.Instance(world, npc).Bodies[0]);
        Assert.True(moving.Length() > 0.5f, $"the pelvis moves at only {moving.Length():F2} m/s: not mid-fall");
        Assert.True(first.Engine.Saves.Save("mid"));

        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(first.Engine.Saves.Root, "mid", "world_main.json")))!;
        var entry = file["entities"]!.AsArray().Single(e => (string?)e!["name"] == "faller")!;
        var ragdoll = entry["components"]!["sage:ragdoll"]!["data"]!;
        Assert.Equal(11, ragdoll["Bodies"]!.AsArray().Count);
        Assert.False((bool?)ragdoll["Settled"] ?? false);         // a diff against the prefab: false is left out

        Step(world, 300);
        var landed = Pelvis(world, npc);
        Assert.True(Ragdolls.IsSettled(world, npc));

        using var second = App(first.Engine.Saves.Root);
        var again = second.World;
        Assert.True(second.Engine.Saves.Load("mid"));
        npc = again.FindByName("faller");
        Step(again);
        Assert.True(Ragdolls.IsActive(again, npc));
        // Where they were saved, not standing: the load's own tick puts them back (the step before it had none).
        var back = Bodies(again, npc);
        for (int i = 0; i < back.Length; i++)
        {
            Assert.True(Vector3.Distance(back[i].Position, saved[i].Position) < 0.005f, $"body {i} is at {back[i].Position}, saved at {saved[i].Position}");
            Assert.True(MathF.Abs(Quaternion.Dot(back[i].Rotation, saved[i].Rotation)) > 0.9999f, $"body {i} turned");
        }
        Assert.Equal(moving.Z, again.Resources.Get<IPhysicsWorld>().VelocityOf(RagdollTests.Instance(again, npc).Bodies[0]).Z, 2);

        Step(again, 299);
        var there = Pelvis(again, npc);
        Assert.True(Vector3.Distance(there, landed) < 0.05f, $"the loaded fall landed at {there}, the uninterrupted one at {landed}");
        Assert.True(Ragdolls.IsSettled(again, npc));
    }

    // Acceptance: a ragdoll comes to rest, says so once (OnSettled), and sleeps; saved settled and loaded
    // in a fresh app, it is still down where it lay, settled and asleep, and stays there.
    [Xunit.Fact]
    public void ASettledRagdollSleeps_AndLoadsDownSettledAndAsleep()
    {
        using var first = App();
        var world = first.World;
        var npc = Npc(world, new Vector3(12, 0, 14), "corpse");
        CountSettling(world, npc, "settles");
        Step(world, 3);
        Assert.True(Ragdolls.Start(world, npc));
        Assert.False(Ragdolls.IsSettled(world, npc));
        int ticks = UntilSettled(world, npc);
        Assert.True(ticks > 60, $"it settled after {ticks} ticks: before it could have fallen");
        Step(world, 2);                                             // the wire's delivery
        Assert.True(AllAsleep(world, npc));
        Assert.True(world.Get<Ragdoll>(npc).Settled);
        Assert.Equal(1f, Count(world, "settles"));
        Step(world, 120);
        Assert.Equal(1f, Count(world, "settles"));                  // once
        Assert.True(RagdollTests.Instance(world, npc).Quiet);       // nothing to do while it sleeps
        var lying = Bodies(world, npc);
        Assert.True(lying[0].Position.Y < 0.4f, $"the pelvis is {lying[0].Position.Y:F2} m up");
        Assert.True(first.Engine.Saves.Save("down"));

        using var second = App(first.Engine.Saves.Root);
        var again = second.World;
        Assert.True(second.Engine.Saves.Load("down"));
        npc = again.FindByName("corpse");
        Step(again, 2);
        Assert.True(Ragdolls.IsSettled(again, npc));
        Assert.True(AllAsleep(again, npc));
        Step(again, 120);
        Assert.True(AllAsleep(again, npc));
        var back = Bodies(again, npc);
        for (int i = 0; i < back.Length; i++)
            Assert.True(Vector3.Distance(back[i].Position, lying[i].Position) < 0.005f, $"body {i} is at {back[i].Position}, lay at {lying[i].Position}");
        // The pose and root are the ragdoll's, not the animator's standing one.
        var root = again.Get<Transform>(npc).LocalPosition;
        Assert.Equal(back[0].Position.X, root.X, 2);
        Assert.Equal(back[0].Position.Z, root.Z, 2);
        Assert.True(Animators.TryGetPose(again, npc, out var pose));
        var head = Vector3.Transform(pose.ModelSpace[pose.Skeleton.IndexOf("head")].Translation, Pose.FromLocal(again.Get<Transform>(npc)).ToMatrix());
        Assert.True(head.Y < 0.5f, $"the posed head is {head.Y:F2} m up");
    }

    // A shove wakes a settled ragdoll and unsettles it; once it lies still again it settles, and says so, again.
    [Xunit.Fact]
    public void AShoveUnsettlesIt_AndItSettlesAgain()
    {
        using var app = App();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14), "corpse");
        CountSettling(world, npc, "settles");
        Step(world, 3);
        Assert.True(Ragdolls.Start(world, npc));
        UntilSettled(world, npc);
        Step(world, 2);
        Assert.Equal(1f, Count(world, "settles"));

        Assert.True(Ragdolls.ApplyImpulse(world, npc, new Vector3(0, 30, 40)));
        Step(world);
        Assert.False(Ragdolls.IsSettled(world, npc));
        Assert.False(AllAsleep(world, npc));
        UntilSettled(world, npc);
        Step(world, 2);
        Assert.Equal(2f, Count(world, "settles"));
        Assert.True(AllAsleep(world, npc));

        // Stop forgets it all: not a ragdoll, nothing settled, no body states to save.
        Assert.True(Ragdolls.Stop(world, npc));
        Assert.False(Ragdolls.IsSettled(world, npc));
        Assert.Null(world.Get<Ragdoll>(npc).Bodies);
    }

    // A save whose bodies do not fit the ragdoll any more (its record has changed since) is rebuilt from the
    // pose, with a warning, rather than putting the wrong bodies in the wrong places.
    [Xunit.Fact]
    public void ASaveWithTheWrongNumberOfBodiesIsRebuiltFromThePose()
    {
        using var first = App();
        var world = first.World;
        var npc = Npc(world, new Vector3(12, 0, 14), "faller");
        Step(world, 3);
        Assert.True(Ragdolls.Start(world, npc));
        Step(world, 30);
        Assert.True(first.Engine.Saves.Save("short"));
        string path = Path.Combine(first.Engine.Saves.Root, "short", "world_main.json");
        var file = JsonNode.Parse(File.ReadAllText(path))!;
        var entry = file["entities"]!.AsArray().Single(e => (string?)e!["name"] == "faller")!;
        entry["components"]!["sage:ragdoll"]!["data"]!["Bodies"]!.AsArray().RemoveAt(10);
        File.WriteAllText(path, file.ToJsonString());

        using var second = App(first.Engine.Saves.Root);
        var again = second.World;
        using var log = new CaptureSink();
        Assert.True(second.Engine.Saves.Load("short"));
        npc = again.FindByName("faller");
        Step(again);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("saved ragdoll has 10 bodies"));
        Assert.True(Ragdolls.IsActive(again, npc));
        Assert.Equal(11, again.Get<Ragdoll>(npc).Bodies!.Length);
        Step(again, 240);
        Assert.True(Pelvis(again, npc).Y < 0.4f);
    }

    // Golden ragdoll save: tests/Sage.Tests/Content/Saves/ragdoll was written (format 4) by
    // WriteTheGoldenRagdollSaveWhenAsked with a mannequin settled on the floor and another a third of a
    // second into a fall. It must load: the one down still down, settled and asleep; the other falling on
    // from where it was, to lie on the floor.
    [Xunit.Fact]
    public void AGoldenSaveWithRagdollsLoads()
    {
        string golden = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "tests", "Sage.Tests", "Content", "Saves", "ragdoll");
        using var app = App(golden);
        var world = app.World;
        using var log = new CaptureSink();
        Assert.True(app.Engine.Saves.Load("golden"));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warn && e.Message.Contains("ragdoll"));
        var corpse = world.FindByName("corpse");
        var faller = world.FindByName("faller");
        var savedFaller = JsonNode.Parse(File.ReadAllText(Path.Combine(golden, "golden", "world_main.json")))!["entities"]!.AsArray()
            .Single(e => (string?)e!["name"] == "faller")!["components"]!["sage:ragdoll"]!["data"]!["Bodies"]![0]!["Position"]!;
        Step(world);
        Assert.True(Ragdolls.IsSettled(world, corpse));
        Assert.True(AllAsleep(world, corpse));
        Assert.True(Pelvis(world, corpse).Y < 0.4f);
        Assert.True(Ragdolls.IsActive(world, faller));
        Assert.False(Ragdolls.IsSettled(world, faller));
        Assert.Equal((float)savedFaller[1]!, Pelvis(world, faller).Y, 3);
        UntilSettled(world, faller);
        Assert.True(Pelvis(world, faller).Y < 0.4f);
    }

    // Writes the golden ragdoll save, only when asked: run with SAGE_WRITE_GOLDEN_RAGDOLL_SAVE=<folder> and
    // commit what it writes there as tests/Sage.Tests/Content/Saves/ragdoll.
    [Xunit.Fact]
    public void WriteTheGoldenRagdollSaveWhenAsked()
    {
        string? target = Environment.GetEnvironmentVariable("SAGE_WRITE_GOLDEN_RAGDOLL_SAVE");
        if (string.IsNullOrEmpty(target)) return;
        using var app = App(target);
        var world = app.World;
        var corpse = Npc(world, new Vector3(8, 0, 14), "corpse");
        var faller = Npc(world, new Vector3(14, 0, 14), "faller");
        Step(world, 3);
        Assert.True(Ragdolls.Start(world, corpse));
        UntilSettled(world, corpse);
        Assert.True(Ragdolls.Start(world, faller, new Vector3(0, 0, 80)));
        Step(world, 20);
        Assert.True(app.Engine.Saves.Save("golden"));
    }
}

// Twenty ragdolls that have settled cost nothing per tick: asleep, they are left alone, and allocate nothing
// apart from the physics step's own.
[Xunit.Collection(MeasurementsCollection.Name)]
public class RagdollSettledAllocationTests
{
    public RagdollSettledAllocationTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void TwentySettledRagdollsAllocateNothingPerTick()
    {
        using var app = NpcLocomotionTests.Skeletal();
        var world = app.World;
        var npcs = new Entity[20];
        for (int i = 0; i < npcs.Length; i++)
            npcs[i] = world.Spawn(NpcLocomotionTests.Npc, new Vector3(-12 + (i % 10) * 2.5f, 0, 12 + (i / 10) * 4f));
        NpcLocomotionTests.Step(world, 3);
        for (int i = 0; i < npcs.Length; i++) Assert.True(Ragdolls.Start(world, npcs[i]));

        void Step()
        {
            world.RunFixed(NpcLocomotionTests.Dt);
            world.RunFrame(NpcLocomotionTests.Dt, 1f);
            Profiler.EndFrame();
        }
        for (int t = 0; t < 600 && !npcs.All(n => Ragdolls.IsSettled(world, n)); t++) Step();
        Assert.All(npcs, n => Assert.True(Ragdolls.IsSettled(world, n)));
        for (int i = 0; i < 10; i++) Step();
        Assert.All(npcs, n => Assert.True(RagdollTests.Instance(world, n).Quiet, "a settled ragdoll is still being written"));

        long physicsBefore = ScopeBytes("Fixed.Physics");
        var allocated = AllocationProbe.Measure(120, Step);
        long physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(allocated.Bytes - physics == 0, allocated.ToString());
        Assert.All(npcs, n => Assert.True(Ragdolls.IsSettled(world, n)));
    }

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }
}
