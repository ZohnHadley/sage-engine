#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

#pragma warning disable SAGE0127 // hit locations are phase 4e's experimental API

// The hitbox budget (issue #273, docs/design/16 "As built (hit locations)"): a creature farther than the
// `hitbox_budget` record's distance from every player, or past its nearest `maxCreatures`, has its hitboxes
// off (ColliderOff: no bodies), and a strike on it lands on its body; back within range they come back.
public class HitboxBudgetTests
{
    public HitboxBudgetTests() { _ = TestEnv.UserRoot; }

    internal static Entity Capsuled(World world, Vector3 at)
    {
        var npc = world.Spawn(new RecordId("hits", "capsuled"), at);
        Assert.False(npc.IsNull);
        return npc;
    }

    // The yard's own player (tests/games/skeletal), put at `at`. Its creatures stand round (0, 0, 0).
    internal static Entity Player(World world, Vector3 at)
    {
        var player = world.FindByName("player");
        Assert.False(player.IsNull);
        Assert.True(player.Tags.Has<PlayerControlled>());
        world.Get<Transform>(player).LocalPosition = at;
        return player;
    }

    // The yard (tests/games/skeletal) without its own creatures, so the budget is choosing among the
    // test's alone; its floor is 40 m square round the origin.
    internal static HeadlessApp EmptyYard()
    {
        var app = HitLocationTests.Yard();
        foreach (var name in new[] { "sentry", "trooper", "walker", "jogger", "runner" })
        {
            var e = app.World.FindByName(name);
            Assert.False(e.IsNull, $"no {name} in the yard");
            app.World.Destroy(e);
        }
        return app;
    }

    // How many of `owner`'s hitboxes have a body, and how many are switched off.
    internal static (int Bodies, int Off) BoxesOf(World world, Entity owner)
    {
        int bodies = 0, off = 0;
        foreach (var (boxes, entities) in world.Query<Hitbox>().Chunks)
        {
            var b = boxes.Span;
            for (int n = 0; n < b.Length; n++)
            {
                if (b[n].Owner != owner) continue;
                var box = entities.EntityAt(n);
                if (box.HasComponent<PhysicsBody>()) bodies++;
                if (box.Tags.Has<ColliderOff>()) off++;
            }
        }
        return (bodies, off);
    }

    // A pistol ray at `target`'s eye height from 5 m in front of it.
    private static HitResult ShootAtHead(World world, Entity shooter, Entity target)
    {
        var at = world.Get<Transform>(target).LocalPosition;
        Assert.True(Hits.Ray(world.Resources.Get<IPhysicsWorld>(), shooter, new Vector3(at.X, 1.62f, at.Z + 5f), -Vector3.UnitZ, 60f, out var result));
        return result;
    }

    // Acceptance: within 50 m of the player (the engine's budget) a head shot lands on `head`; walk 60 m off
    // and the creature's eleven boxes lose their bodies and the same shot lands on its body (its capsule);
    // walk back and they are on again. Between 50 and 55 m nothing switches (the hysteresis), either way.
    [Xunit.Fact]
    public void AFarCreaturesHitboxesAreOffAndAStrikeOnItLandsOnItsBody()
    {
        using var app = EmptyYard();
        var world = app.World;
        var target = Capsuled(world, new Vector3(12, 0, 14));
        var shooter = world.Create(Transform.At(new Vector3(0, 0, 30)), "shooter");
        var player = Player(world, new Vector3(12, 0, 24));
        NpcLocomotionTests.Step(world, 5);
        var head = new RecordId("skeletal", "head");

        Assert.Equal((11, 0), BoxesOf(world, target));
        Assert.Equal(head, ShootAtHead(world, shooter, target).Location);

        void PlayerAt(float distance)
        {
            world.Get<Transform>(player).LocalPosition = new Vector3(12, 0, 14 + distance);
            NpcLocomotionTests.Step(world, 2);
        }

        PlayerAt(53f);
        Assert.Equal((11, 0), BoxesOf(world, target));   // on, and inside the band it stays on
        PlayerAt(60f);
        Assert.Equal((0, 11), BoxesOf(world, target));
        var far = ShootAtHead(world, shooter, target);
        Assert.Equal(target, far.Target);
        Assert.True(far.Location.IsEmpty, $"a creature with its hitboxes off is struck on its body, not on {far.Location}");

        PlayerAt(53f);
        Assert.Equal((0, 11), BoxesOf(world, target));   // off, and inside the band it stays off
        PlayerAt(40f);
        Assert.Equal((11, 0), BoxesOf(world, target));
        Assert.Equal(head, ShootAtHead(world, shooter, target).Location);
    }

    // Acceptance: past `maxCreatures`, the nearest keep their hitboxes and the rest go off; the player
    // walking over to the far one turns the choice round. With no player in the world nothing is off.
    [Xunit.Fact]
    public void OnlyTheNearestMaxCreaturesKeepTheirHitboxes()
    {
        using var app = EmptyYard();
        var world = app.World;
        app.Records.Get<HitboxBudgetRecord>(app.World.Conventions().HitboxBudget.Id).MaxCreatures = 2;
        var a = Capsuled(world, new Vector3(0, 0, 0));
        var b = Capsuled(world, new Vector3(6, 0, 0));
        var c = Capsuled(world, new Vector3(12, 0, 0));
        var player = Player(world, new Vector3(-4, 0, 3));
        NpcLocomotionTests.Step(world, 3);
        Assert.Equal((11, 0), BoxesOf(world, a));
        Assert.Equal((11, 0), BoxesOf(world, b));
        Assert.Equal((0, 11), BoxesOf(world, c));

        world.Get<Transform>(player).LocalPosition = new Vector3(16, 0, 3);
        NpcLocomotionTests.Step(world, 2);
        Assert.Equal((0, 11), BoxesOf(world, a));
        Assert.Equal((11, 0), BoxesOf(world, b));
        Assert.Equal((11, 0), BoxesOf(world, c));

        player.RemoveTag<PlayerControlled>();   // nobody to budget for: everything on
        NpcLocomotionTests.Step(world, 2);
        Assert.All(new[] { a, b, c }, npc => Assert.Equal((11, 0), BoxesOf(world, npc)));
    }
}

// The budget allocates nothing: four creatures and a player pacing past them, so the choice of the
// nearest two changes as it goes, measured whole (the step included, now that it allocates nothing).
[Xunit.Collection(MeasurementsCollection.Name)]
public class HitboxBudgetAllocationTests
{
    public HitboxBudgetAllocationTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void TheBudgetAllocatesNothingPerTick()
    {
        using var app = HitboxBudgetTests.EmptyYard();
        var world = app.World;
        app.Records.Get<HitboxBudgetRecord>(app.World.Conventions().HitboxBudget.Id).MaxCreatures = 2;
        var npcs = new Entity[4];
        for (int i = 0; i < npcs.Length; i++) npcs[i] = HitboxBudgetTests.Capsuled(world, new Vector3(i * 8 - 12, 0, 0));
        var player = HitboxBudgetTests.Player(world, new Vector3(0, 0, 4));
        int tick = 0;
        void Step()
        {
            tick++;
            world.Get<Transform>(player).LocalPosition = new Vector3(14 * MathF.Sin(tick * 0.02f), 0, 4);
            world.RunFixed(NpcLocomotionTests.Dt);
            world.RunFrame(NpcLocomotionTests.Dt, 1f);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 400; i++) Step();   // warm: every creature switched off and on at least once
        int on = 0;
        foreach (var npc in npcs) on += HitboxBudgetTests.BoxesOf(world, npc).Bodies;
        Assert.Equal(2 * 11, on);
        AllocationProbe.AssertNone(320, Step);   // a full pace, switching on the way
    }
}
