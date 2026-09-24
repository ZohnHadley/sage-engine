#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Who counts as an enemy (docs/design/16 §3.5, TODO F24).
//
// The question three systems were guessing at: what an AI hunts, what a blast is allowed to catch, and
// what killing somebody does to your name. All simulation, so all of it runs headless.
public class FactionTests
{
    public FactionTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "effect", "id": "damage", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },
         { "type": "attack", "id": "claws", "damage": 12, "damageType": "physical", "reach": 2.2,
           "radius": 0.45, "windupTime": 0.2, "recoverTime": 0.1, "cooldown": 0.5 },

         { "type": "ai_profile", "id": "default_ai", "sightRange": 30, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": ["Wait:1.5"], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": ["MoveToTarget:1.6"], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", "MeleeAttack:0.2", "Wait:0.4"], "interrupts": ["LostEnemy", "NoEnemy"] },

         { "type": "faction", "id": "wolves", "label": "wolves", "standing": -50,
           "relations": [ { "faction": "sage:sheep", "stance": "Hostile" } ] },
         { "type": "faction", "id": "sheep", "label": "sheep", "standing": 0, "killCost": 30,
           "relations": [ { "faction": "sage:wolves", "stance": "Hostile" },
                          { "faction": "sage:shepherds", "stance": "Ally" } ] },
         { "type": "faction", "id": "shepherds", "label": "shepherds", "standing": 10 },
         { "type": "faction", "id": "hermits", "label": "hermits", "standing": 0, "default": "Neutral" }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static (Engine, World) NewWorld()
    {
        var cvars = new CVarRegistry();
        var engine = new Engine(cvars, CoreCVars.Register(cvars));
        engine.Modules.Add(new PhysicsModule());
        engine.Modules.AddGameplay();
        engine.Modules.InitAll();

        var fixture = new MountFixture();
        fixture.Write("engine", "data/factions.json", Records);
        fixture.Mount("engine", "sage");
        engine.Records.Load(fixture.Vfs);
        engine.Modules.StartAll();

        var world = engine.CreateWorld("factions");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return (engine, world);
    }

    private static Entity Creature(World world, Vector3 at, string faction, string name = "creature")
    {
        var entity = world.Create(Transform.At(at), name);
        world.AddCharacter(entity, 2);
        world.Add(entity, new AIState { Schedule = AIThinkSystem.Schedules.Idle });
        world.Add(entity, Melee.With(Id("claws")));
        world.AddAttributes(entity);
        if (faction.Length > 0) world.Add(entity, new Faction { Id = Id(faction) });
        return entity;
    }

    private static Entity Player(World world, Vector3 at)
    {
        var entity = world.Create(Transform.At(at), "player");
        world.Add(entity, Collider.Standing(0.35f, 1.8f, 1));
        world.Add(entity, RigidBody.Kinematic());
        world.AddAttributes(entity);
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    // The table, and the two things it says without being told: a faction is its own ally, and anything
    // it has no opinion about gets the default.
    [Fact]
    public void AFactionIsItsOwnAllyAndHasADefaultForEverybodyElse()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var records = engine.Records;
            Assert.Equal(Stance.Ally, Factions.Between(records, Id("wolves"), Id("wolves")));
            Assert.Equal(Stance.Hostile, Factions.Between(records, Id("wolves"), Id("sheep")));
            Assert.Equal(Stance.Ally, Factions.Between(records, Id("sheep"), Id("shepherds")));
            Assert.Equal(Stance.Neutral, Factions.Between(records, Id("wolves"), Id("hermits")));
            Assert.Equal(Stance.Neutral, Factions.Between(records, Id("wolves"), default));
        }
    }

    // The behaviour F24 is *for*: two creatures that have never heard of the player, fighting each other.
    [Fact]
    public void CreaturesOfHostileFactionsHuntEachOther()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            // The sheep is facing away, which is the ordinary case: sight is a cone, and the wolf is
            // behind it.
            var wolf = Creature(world, new Vector3(0, 0, 0), "wolves", "wolf");
            var sheep = Creature(world, new Vector3(0, 0, -8), "sheep", "sheep");
            Tick(world, 60);

            Assert.Equal(sheep, world.Get<AIState>(wolf).Target);
            Assert.True(world.Get<AIState>(sheep).Target.IsNull, "the sheep saw through the back of its head");

            // Six seconds later the wolf has crossed the field and bitten it, and *that* is how the
            // sheep finds out: a creature notices whoever hits it, whatever it was looking at.
            Tick(world, 60 * 6);
            Assert.True(world.Attribute(sheep, AttributeRecord.Health) < 100f,
                "the wolf never reached the sheep");
            Assert.Equal(wolf, world.Get<AIState>(sheep).Target);
        }
    }

    // Allies leave each other alone, even standing in reach.
    [Fact]
    public void AlliesDoNotFightEachOther()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var sheep = Creature(world, new Vector3(0, 0, 0), "sheep", "sheep");
            var shepherd = Creature(world, new Vector3(0, 0, -2), "shepherds", "shepherd");
            Tick(world, 60 * 4);

            Assert.True(world.Get<AIState>(sheep).Target.IsNull);
            Assert.Equal(100f, world.Attribute(shepherd, AttributeRecord.Health), 1);
        }
    }

    // Content that says nothing about factions behaves exactly as it did before there were any: a
    // creature with no faction still comes for the player. Every test written before F24 relies on it.
    [Fact]
    public void ACreatureWithNoFactionStillHuntsThePlayer()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var creature = Creature(world, new Vector3(0, 0, 0), faction: "");
            var player = Player(world, new Vector3(0, 0, -6));
            Tick(world, 60);

            Assert.Equal(player, world.Get<AIState>(creature).Target);
        }
    }

    // A faction that starts hating you comes for you; one that does not, does not. Same creature, same
    // world, one number apart.
    [Fact]
    public void WhetherACreatureHuntsYouIsWhatItsFactionThinksOfYou()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var wolf = Creature(world, new Vector3(0, 0, 0), "wolves", "wolf");          // standing -50
            var hermit = Creature(world, new Vector3(6, 0, 0), "hermits", "hermit");     // standing 0
            var player = Player(world, new Vector3(0, 0, -6));
            Tick(world, 60);

            Assert.Equal(player, world.Get<AIState>(wolf).Target);
            Assert.True(world.Get<AIState>(hermit).Target.IsNull, "a neutral hermit came for the player");

            // Rob the hermits blind and they change their minds.
            Factions.Change(world, Id("hermits"), -60f);
            Tick(world, 60);
            Assert.Equal(player, world.Get<AIState>(hermit).Target);
        }
    }

    // Killing somebody costs you with their faction, and a little with everyone who liked them.
    [Fact]
    public void KillingSomebodyCostsYouWithTheirFriends()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var sheep = Creature(world, new Vector3(0, 0, -2), "sheep", "sheep");
            var player = Player(world, new Vector3(0, 0, 0));
            var changes = new EventProbe<ReputationChanged>(world);

            Assert.Equal(0f, Factions.StandingWith(world, Id("sheep")), 1);
            Assert.Equal(10f, Factions.StandingWith(world, Id("shepherds")), 1);

            Combat.ApplyDamage(world, new DamageInfo(player, sheep, Id("physical"), 500f, Vector3.Zero, Vector3.UnitZ));
            Tick(world, 2);

            // The sheep's own cost, and a fifth of it to the shepherds who liked them. The wolves, who
            // hated them, think slightly better of you.
            Assert.Equal(-30f, Factions.StandingWith(world, Id("sheep")), 1);
            Assert.Equal(10f - 6f, Factions.StandingWith(world, Id("shepherds")), 1);
            Assert.Equal(-50f + 6f, Factions.StandingWith(world, Id("wolves")), 1);
            Assert.Contains(changes.All, c => c.Faction == Id("sheep") && c.Change < 0f);
        }
    }

    // A creature that dies to another creature is nobody's business but theirs: reputation is the
    // player's alone, and a wolf eating a sheep does not make the shepherds hate *you*.
    [Fact]
    public void OnlyWhatThePlayerDoesChangesReputation()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var wolf = Creature(world, new Vector3(0, 0, 0), "wolves", "wolf");
            var sheep = Creature(world, new Vector3(0, 0, -2), "sheep", "sheep");

            Combat.ApplyDamage(world, new DamageInfo(wolf, sheep, Id("physical"), 500f, Vector3.Zero, Vector3.UnitZ));
            Tick(world, 2);

            Assert.Equal(0f, Factions.StandingWith(world, Id("sheep")), 1);
        }
    }

    // Who a hit is *allowed* to land on. A creature's own kind is spared; the player's aim is their
    // business and their reputation's problem.
    [Fact]
    public void AllyFireIsSparedButThePlayerMayHitAnybody()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var wolf = Creature(world, new Vector3(0, 0, 0), "wolves", "wolf");
            var packmate = Creature(world, new Vector3(0, 0, -2), "wolves", "packmate");
            var sheep = Creature(world, new Vector3(0, 0, -4), "sheep", "sheep");
            var player = Player(world, new Vector3(0, 0, 4));

            Assert.False(Factions.MayHurt(world, wolf, packmate));
            Assert.True(Factions.MayHurt(world, wolf, sheep));
            Assert.True(Factions.MayHurt(world, player, sheep));
            Assert.True(Factions.MayHurt(world, player, packmate));
        }
    }
}
