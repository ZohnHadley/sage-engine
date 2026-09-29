#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Declared systems (REDESIGN §3.3, issue #17): a stable id, a phase and an order by id from
// [System]; constraints that cross phases or name nothing are errors; a removed system lets go of its
// event readers; and a plugin can replace or disable a system it did not add, on the record.
public class SystemDeclarationTests
{
    public SystemDeclarationTests() { _ = TestEnv.UserRoot; }

    private struct Pinged { public int Value; }

    [System("test.decl.first", Phase.Gameplay)]
    private sealed class First : ISystem
    {
        private readonly List<string> _log;
        public First(List<string> log) { _log = log; }
        public void Run(in SystemContext ctx) => _log.Add("first");
    }

    [System("test.decl.second", Phase.Gameplay, After = new[] { "test.decl.first" })]
    private sealed class Second : ISystem
    {
        private readonly List<string> _log;
        public Second(List<string> log) { _log = log; }
        public void Run(in SystemContext ctx) => _log.Add("second");
    }

    [System("test.decl.ai", Phase.AI)]
    private sealed class InAI : ISystem { public void Run(in SystemContext ctx) { } }

    [System("test.decl.across", Phase.Gameplay, After = new[] { "test.decl.ai" })]
    private sealed class Across : ISystem { public void Run(in SystemContext ctx) { } }

    [System("test.decl.typo", Phase.Gameplay, After = new[] { "test.decl.frist" })]
    private sealed class Typo : ISystem { public void Run(in SystemContext ctx) { } }

    [System("test.decl.soft", Phase.Gameplay, After = new[] { "?othermod.not_installed" })]
    private sealed class Soft : ISystem { public void Run(in SystemContext ctx) { } }

    // Reads Pinged, and says when it is disposed: a system holding what its constructor was given.
    [System("test.decl.reader", Phase.Gameplay)]
    private sealed class Reader : ISystem, IDisposable
    {
        private readonly EventReader<Pinged> _pings;
        public int Seen;
        public bool Disposed;
        public Reader(World world) { _pings = world.Events.Reader<Pinged>(this); }
        public void Run(in SystemContext ctx) { foreach (ref readonly var _ in _pings.Read()) Seen++; }
        public void Dispose() => Disposed = true;
    }

    // What a mod would swap in for test.decl.reader: undeclared, so it takes the slot as it is.
    private sealed class Stand_in : ISystem
    {
        private readonly List<string> _log;
        public Stand_in(List<string> log) { _log = log; }
        public void Run(in SystemContext ctx) => _log.Add("stand-in");
    }

    // ---- ids, phases and order ----------------------------------------------------------------------

    [Fact]
    public void ADeclaredSystemTakesItsIdPhaseAndOrderFromTheAttribute()
    {
        using var world = new World("declared");
        var log = new List<string>();
        var second = world.AddSystem(new Second(log));   // added first, runs second
        world.AddSystem(new First(log));

        Assert.Equal("test.decl.second", second.Id);
        Assert.Equal(Phase.Gameplay, second.Phase);
        Assert.Equal("host", second.Owner);
        world.RunFixed(1f / 60f);
        Assert.Equal(new[] { "first", "second" }, log);
    }

    // Before issue #17 a constraint naming a system in another phase was dropped without a word. The
    // other system need not even be in this world: its declaration says where it runs.
    [Fact]
    public void AConstraintAcrossPhasesIsAnError()
    {
        using var world = new World("across");
        var ex = Assert.Throws<InvalidOperationException>(() => world.AddSystem(new Across()));
        Assert.Contains("test.decl.across (Gameplay) orders itself against 'test.decl.ai', which runs in AI", ex.Message);
        Assert.Empty(world.Systems);

        // The same from the other side: a system that orders itself against an id nothing has
        // declared yet (optional, so allowed), then an undeclared system given that id by hand in
        // another phase.
        using var other = new World("other");
        other.AddSystem(new Stand_in(new()), Phase.Gameplay, id: "test.decl.orders", after: new[] { "?test.decl.later" });
        var reverse = Assert.Throws<InvalidOperationException>(() =>
            other.AddSystem(new Stand_in(new()), Phase.AI, id: "test.decl.later"));
        Assert.Contains("test.decl.orders (Gameplay) orders itself against 'test.decl.later', which runs in AI", reverse.Message);
    }

    // A misspelt id is declared by nothing a loaded assembly has: an error in a dev build, where it is
    // found. "?id" is how a plugin says the other one may not be installed.
    [Fact]
    public void AConstraintNamingAnIdNothingDeclaresIsAnErrorUnlessMarkedOptional()
    {
        Assert.True(BuildInfo.IsDevBuild);
        using var world = new World("typo");
        var ex = Assert.Throws<InvalidOperationException>(() => world.AddSystem(new Typo()));
        Assert.Contains("'test.decl.frist', which no loaded assembly declares", ex.Message);

        world.AddSystem(new Soft());   // fine
        Assert.Single(world.Systems);
    }

    // Declared but not added — its plugin is off, or its module adds it only sometimes (the client's
    // map meshes): no constraint, and no error.
    [Fact]
    public void AConstraintNamingADeclaredSystemThatIsNotHereIsNoConstraint()
    {
        using var world = new World("absent");
        var log = new List<string>();
        world.AddSystem(new Second(log));   // after test.decl.first, which is never added
        world.RunFixed(1f / 60f);
        Assert.Equal(new[] { "second" }, log);
    }

    [Fact]
    public void OneIdOncePerWorld_AndADeclaredSystemCannotBeAddedAsUndeclared()
    {
        using var world = new World("twice");
        world.AddSystem(new First(new()));
        var twice = Assert.Throws<InvalidOperationException>(() => world.AddSystem(new First(new())));
        Assert.Contains("System id 'test.decl.first' is already in this world", twice.Message);

        var phase = Assert.Throws<InvalidOperationException>(() => world.AddSystem(new Second(new()), Phase.Late));
        Assert.Contains("is declared [System(\"test.decl.second\", Phase.Gameplay)]", phase.Message);

        var undeclared = Assert.Throws<InvalidOperationException>(() => world.AddSystem(new Stand_in(new())));
        Assert.Contains("has no [System", undeclared.Message);
    }

    // ---- what a system holds ------------------------------------------------------------------------

    // The bug in issue #17: a removed system's cursor stayed registered, so its queue kept every event
    // until ev_maxage dropped them with a warning naming a system that was gone.
    [Fact]
    public void RemovingASystemReleasesItsEventReadersAndDisposesIt()
    {
        using var world = new World("remove");
        var reader = new Reader(world);
        world.AddSystem(reader);
        var queue = world.Events.Queue<Pinged>();
        Assert.Equal(1, queue.ReaderCount);

        Assert.True(world.RemoveSystem(reader));
        Assert.Equal(0, queue.ReaderCount);
        Assert.True(reader.Disposed);

        world.Events.Send(new Pinged { Value = 1 });
        world.RunFixed(1f / 60f);
        Assert.Equal(0, queue.Count);   // nobody reads it now, so nothing is kept for anybody
        Assert.False(world.RemoveSystem(reader));
    }

    // ---- replace and disable ------------------------------------------------------------------------

    [Fact]
    public void ReplaceKeepsTheSlotAndRetiresTheOldSystem()
    {
        using var world = new World("replace");
        var log = new List<string>();
        var old = new Reader(world);
        world.AddSystem(new Second(log));
        world.AddSystem(old);
        world.AddSystem(new First(log));

        Assert.True(world.Systems.Replace("test.decl.reader", new Stand_in(log)));
        var info = world.Systems.Find("test.decl.reader")!;
        Assert.Equal("Stand_in", info.Name);
        Assert.Equal(Phase.Gameplay, info.Phase);
        Assert.Equal("host", info.ReplacedBy);
        Assert.True(old.Disposed);
        Assert.Equal(0, world.Events.Queue<Pinged>().ReaderCount);

        world.RunFixed(1f / 60f);
        Assert.Equal(new[] { "stand-in", "first", "second" }, log);   // where the old one ran

        Assert.False(world.Systems.Replace("test.decl.nothing_here", new Stand_in(log)));
        Assert.Throws<InvalidOperationException>(() => world.Systems.Replace("test.decl.first", new InAI()));
    }

    [Fact]
    public void DisableTurnsASystemOffForGoodAndLetsGoOfItsReaders()
    {
        using var world = new World("disable");
        var reader = new Reader(world);
        world.AddSystem(reader);

        Assert.True(world.Systems.Disable("test.decl.reader"));
        var info = world.Systems.Find("test.decl.reader")!;
        Assert.False(info.Enabled);
        Assert.Equal("host", info.DisabledBy);
        Assert.Equal(0, world.Events.Queue<Pinged>().ReaderCount);

        world.Events.Send(new Pinged { Value = 1 });
        world.RunFixed(1f / 60f);
        Assert.Equal(0, reader.Seen);
        Assert.Equal(0, world.Events.Queue<Pinged>().Count);
        Assert.False(world.Systems.Disable("test.decl.nothing_here"));
    }

    // What "logged against the calling plugin" means: a mod's OnWorldCreated replaces one system and
    // disables another, and the world, the ledger and `sys_list` all say it was that mod.
    [Plugin("test.decl.mod", "1.0.0")]
    [RequiresPlugin("sage.gameplay.movers", ">=0.1")]
    [RequiresPlugin("sage.gameplay.attributes", ">=0.1")]
    private sealed class ModThatSwapsSystems : IModule
    {
        public void Init(ModuleContext ctx) { }

        public void OnWorldCreated(World world)
        {
            world.Systems.Replace("sage.movers.move", new Stand_in(new()));
            world.Systems.Disable("sage.effects.tick");
        }
    }

    [Fact]
    public void ReplaceAndDisableAreRecordedAgainstThePluginThatDidThem()
    {
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay().With(new ModThatSwapsSystems()).Boot();
        var moved = app.World.Systems.Find("sage.movers.move")!;
        Assert.Equal("sage.gameplay.movers", moved.Owner);
        Assert.Equal("test.decl.mod", moved.ReplacedBy);
        Assert.Equal("test.decl.mod", app.World.Systems.Find("sage.effects.tick")!.DisabledBy);
        Assert.Equal("test.decl.mod", app.Engine.Registrations.OwnerOf("system", "sage.movers.move"));
        Assert.Equal("test.decl.mod", app.Engine.Registrations.OwnerOf("disabled system", "sage.effects.tick"));
        Assert.Equal("sage.gameplay.attributes", app.Engine.Registrations.OwnerOf("system", "sage.effects.tick"));

        app.CVars.Execute("sys_list");
        app.CVars.Execute("sys_toggle sage.effects.tick");   // a plugin turned it off: stays off
        Assert.False(app.World.Systems.Find("sage.effects.tick")!.Enabled);
        var lines = capture.Entries.Select(e => e.Message).ToList();
        Assert.Contains(lines, l => l.Contains("sage.movers.move") && l.Contains("Stand_in [sage.gameplay.movers, replaced by test.decl.mod]"));
        Assert.Contains(lines, l => l.Contains("sage.effects.tick") && l.Contains("(disabled by test.decl.mod)"));
        Assert.Contains(lines, l => l.Contains("MoverSystem (from sage.gameplay.movers) replaced by Stand_in (from test.decl.mod)"));
    }

    // ---- every system the engine and the Sandbox ship ----------------------------------------------

    // Issue #17's acceptance: every system is declared, so every one has an id, and each was added by
    // the plugin it belongs to rather than by "host".
    [Fact]
    public void EverySystemInTheSandboxIsDeclaredAndOwned()
    {
        using var app = HeadlessApp.ForGame(SandboxDirectory, new Sandbox.SandboxModule()).Boot();
        var systems = app.World.Systems.ToList();
        Assert.True(systems.Count >= 25, $"only {systems.Count} systems");
        Assert.All(systems, s => Assert.NotNull(s.Id));
        Assert.All(systems, s => Assert.NotEqual("host", s.Owner));
        Assert.Equal("sandbox", app.World.Systems.Find("sandbox.hop")!.Owner);
        Assert.Equal("sage.gameplay.ai", app.World.Systems.Find("sage.ai.think")!.Owner);

        // The one cross-plugin constraint in the simulation still holds, by id.
        var prePhysics = app.World.Systems.Where(s => s.Phase == Phase.PrePhysics).Select(s => s.Id).ToList();
        Assert.True(prePhysics.IndexOf("sage.character.move") < prePhysics.IndexOf("sage.physics.sync"));
        Assert.True(prePhysics.IndexOf("sage.physics.terrain") < prePhysics.IndexOf("sage.physics.sync"));
    }

    private static string SandboxDirectory => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
}
