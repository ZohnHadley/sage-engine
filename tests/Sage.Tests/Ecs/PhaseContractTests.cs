#nullable enable
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase contracts (docs/design/03 §3.5, TODO R16). Review #48 was a system writing `PawnIntent` in a
// phase *after* the one that consumes it, so every creature acted on last tick's decision — and the
// comment beside it claimed the opposite, which is why a code review and a docs audit both missed it.
// These are the tests that would have caught it.
public class PhaseContractTests
{
    public PhaseContractTests() { _ = TestEnv.UserRoot; }

    // Writes to an entity's intent in whatever phase it is installed in.
    private sealed class IntentWriter : ISystem
    {
        private readonly Entity _entity;
        private float _yaw;

        public IntentWriter(Entity entity) => _entity = entity;

        public void Run(in SystemContext ctx) => ctx.World.Get<PawnIntent>(_entity).Yaw = _yaw += 0.1f;
    }

    private static (World World, Entity Pawn) NewWorld()
    {
        var world = new World("contracts");
        var pawn = world.Create(Transform.Identity, "pawn");
        world.Add(pawn, new PawnIntent());
        world.Contracts.FinalAfter<PawnIntent>(Phase.Commands);
        return (world, pawn);
    }

    [Xunit.Fact]
    public void WritingInTheDeclaredPhaseIsFine()
    {
        var (world, pawn) = NewWorld();
        using (world)
        {
            world.AddSystem(new IntentWriter(pawn), Phase.Commands);
            for (int i = 0; i < 5; i++) world.RunFixed(1f / 60f);

            Assert.Equal(0, world.Contracts.Violations);
        }
    }

    // The shape of review #48: the decision is made after the thing that acts on it has already run.
    [Xunit.Fact]
    public void WritingAfterTheDeclaredPhaseIsReported()
    {
        var (world, pawn) = NewWorld();
        using (world)
        {
            world.AddSystem(new IntentWriter(pawn), Phase.AI);
            world.RunFixed(1f / 60f);

            Assert.True(world.Contracts.Violations > 0,
                "a write in the AI phase to a component declared final after Commands should be reported");
        }
    }

    [Xunit.Fact]
    public void WritingBeforeTheDeclaredPhaseIsFine()
    {
        var (world, pawn) = NewWorld();
        using (world)
        {
            // Commands is the first Fixed phase, so "before" means the same phase with an earlier
            // system; what matters is that the snapshot is taken at the *end* of the phase.
            world.AddSystem(new IntentWriter(pawn), Phase.Commands);
            world.AddSystem(new IntentWriter(pawn), Phase.Commands);
            for (int i = 0; i < 3; i++) world.RunFixed(1f / 60f);

            Assert.Equal(0, world.Contracts.Violations);
        }
    }

    // An entity that appears mid-tick has no baseline to differ from, and must not be reported: a
    // spawn in the Gameplay phase is normal and has nothing to do with the contract.
    [Xunit.Fact]
    public void AnEntitySpawnedAfterTheSnapshotIsNotAViolation()
    {
        var (world, _) = NewWorld();
        using (world)
        {
            world.AddSystem(new Spawner(), Phase.Gameplay);
            world.RunFixed(1f / 60f);

            Assert.Equal(0, world.Contracts.Violations);
        }
    }

    private sealed class Spawner : ISystem
    {
        private bool _done;
        public void Run(in SystemContext ctx)
        {
            if (_done) return;
            _done = true;
            var e = ctx.World.Create(Transform.At(new Vector3(1, 0, 0)), "late");
            ctx.World.Add(e, new PawnIntent { Yaw = 2f });
        }
    }

    // A world with no contracts declared does no work and reports nothing — the feature is opt-in per
    // component, because snapshotting everything every tick would be the wrong trade.
    [Xunit.Fact]
    public void AWorldWithNoContractsChecksNothing()
    {
        using var world = new World("bare");
        var pawn = world.Create(Transform.Identity, "pawn");
        world.Add(pawn, new PawnIntent());
        world.AddSystem(new IntentWriter(pawn), Phase.AI);
        world.RunFixed(1f / 60f);

        Assert.Equal(0, world.Contracts.Count);
        Assert.Equal(0, world.Contracts.Violations);
    }
}
