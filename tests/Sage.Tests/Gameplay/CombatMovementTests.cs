#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Combat movement and squads (docs/design/16 §3.4, issue #388): strafe, retreat to range, take cover, flee,
// heal self, flank and call for help as tasks, and a squad's shared blackboard — all put together in data.
public class CombatMovementTests
{
    public CombatMovementTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "damageType": "physical",
           "aiProfile": "default_ai", "costAttribute": "mana",
           "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "melee_attack", "castSpell": "cast_spell", "holdGround": "hold_ground" } },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "mana", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "effect", "id": "damage", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },
         { "type": "attack", "id": "claws", "damage": 5, "reach": 2.2, "radius": 0.45, "windupTime": 0.3, "recoverTime": 0.2, "cooldown": 1.0 },

         { "type": "tag", "id": "cooldown.bolt" },
         { "type": "effect", "id": "bolt_cooldown", "duration": "Timed", "time": 1.0, "grantTags": ["cooldown.bolt"], "modifiers": [] },
         { "type": "ability", "id": "bolt", "name": "bolt", "cost": 0, "cooldown": "bolt_cooldown", "targeting": "Touch", "range": 12, "width": 0.3,
           "castTime": 0.2, "damage": 3, "damageType": "physical" },
         { "type": "tag", "id": "cooldown.mend" },
         { "type": "effect", "id": "mend_cooldown", "duration": "Timed", "time": 1.0, "grantTags": ["cooldown.mend"], "modifiers": [] },
         { "type": "effect", "id": "mend_heal", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": 10 } ] },
         { "type": "ability", "id": "mend", "name": "mend", "cost": 0, "cooldown": "mend_cooldown", "targeting": "Self", "castTime": 0.1, "effects": ["mend_heal"] },

         { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 0.5 }, { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "cast_spell", "tasks": ["FaceTarget", { "task": "CastSpell", "giveUpAfter": 2 }, { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "hold_ground", "tasks": ["FaceTarget", { "task": "Wait", "seconds": 0.3 }], "interrupts": ["LostEnemy", "NoEnemy"] },

         // The archer: the engine's choice (cast in range, close otherwise), and backing off whenever the
         // enemy is nearer than 7 m — more urgently the nearer it is.
         { "type": "ai_profile", "id": "archer", "sightRange": 25, "thinkRate": 20,
           "utility": [ { "schedule": "back_off", "considerations": [ { "when": ["SeeEnemy"], "measure": "target_distance", "from": 7, "to": 5, "add": 4 } ] } ] },
         { "type": "ai_schedule", "id": "back_off", "tasks": [{ "task": "RetreatToRange", "distance": 9 }] },

         // The pack: shout, take a side, close and bite.
         { "type": "ai_profile", "id": "pack", "sightRange": 10, "thinkRate": 20, "meleeRange": 1.8,
           "rules": [ { "when": ["EnemyInMeleeRange", "CanMelee"], "schedule": "melee_attack" },
                      { "when": ["SeeEnemy"], "schedule": "hunt" },
                      { "when": ["RememberEnemy"], "schedule": "hunt" } ] },
         { "type": "ai_schedule", "id": "hunt", "tasks": [{ "task": "CallForHelp", "distance": 30 }, { "task": "Flank", "distance": 3 }, { "task": "MoveToTarget", "distance": 1.6 }],
           "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "prefab", "id": "wolf", "name": "wolf",
           "components": { "ai_state": { "profile": "pack" }, "squad": { "name": "pack" } },
           "parts": { "character": { "layer": "enemy" }, "attributes": {}, "melee": { "attack": "claws" } } },

         // The coward: below half health it runs, then mends itself.
         { "type": "ai_profile", "id": "coward", "sightRange": 25, "thinkRate": 20,
           "utility": [ { "schedule": "run_and_mend", "considerations": [ { "measure": "health", "from": 0.5, "to": 0.2, "add": 3 } ] } ] },
         { "type": "ai_schedule", "id": "run_and_mend", "tasks": [{ "task": "Flee", "distance": 12 }, { "task": "HealSelf", "giveUpAfter": 1 }, { "task": "Wait", "seconds": 0.2 }] },

         // The skulker: out of sight of what it has seen.
         { "type": "ai_profile", "id": "skulker", "sightRange": 25, "thinkRate": 20, "memorySeconds": 20,
           "rules": [ { "when": ["SeeEnemy"], "schedule": "hide" }, { "when": ["RememberEnemy"], "schedule": "hide" } ] },
         { "type": "ai_schedule", "id": "hide", "tasks": [{ "task": "TakeCover", "distance": 8 }, { "task": "Wait", "seconds": 10 }] },

         // The dancer: sidesteps round what it sees.
         { "type": "ai_profile", "id": "dancer", "sightRange": 25, "thinkRate": 20, "rules": [ { "when": ["SeeEnemy"], "schedule": "dance" } ] },
         { "type": "ai_schedule", "id": "dance", "tasks": [{ "task": "Strafe", "seconds": 3 }] }]
        """;

    private const byte EnemyLayer = 2, PlayerLayer = 1;

    private static HeadlessApp NewGame()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay().File("data/combat.json", Records).Boot("combat");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message)));
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    private static RecordId Id(string name) => new("sage", name);

    // A creature made in code with `profile` (placed facing -Z).
    private static Entity Creature(World world, Vector3 position, string profile, string name = "creature", bool claws = true)
    {
        var entity = world.Create(Transform.At(position), name);
        world.AddCharacter(entity, EnemyLayer);
        world.Add(entity, new AIState { Profile = Id(profile) });
        if (claws) world.Add(entity, Melee.With(Id("claws")));
        world.AddAttributes(entity);
        world.Add(entity, new Persistent { Id = PersistentId.FromName(name) });
        return entity;
    }

    private static Entity Player(World world, Vector3 position)
    {
        var entity = world.Create(Transform.At(position), "player");
        world.Add(entity, Collider.Standing(0.35f, 1.8f, PlayerLayer));
        world.Add(entity, RigidBody.Kinematic());
        world.AddAttributes(entity);
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    private static Vector3 At(World world, Entity entity) => world.Get<Transform>(entity).LocalPosition;

    private static float Apart(World world, Entity a, Entity b) => SageMath.DistanceXZ(At(world, a), At(world, b));

    private static void SetHealth(World world, Entity entity, float value)
    {
        int index = world.Resources.Get<GameplayRegistries>().Attribute(Conventional.Health);
        world.Get<Attributes>(entity).Values.SetBase(index, value);
    }

    // The issue's first "done": a ranged creature keeps its distance, from data alone. A player who walks up
    // to it is backed away from — twice — while it shoots from where it stands.
    [Fact]
    public void ARangedCreatureKeepsItsDistance()
    {
        using var app = NewGame();
        var world = app.World;
        var archer = Creature(world, new Vector3(0, 0.1f, 0), "archer", "archer", claws: false);
        world.Teach(archer, Id("bolt"));
        var player = Player(world, new Vector3(0, 0.1f, -3));
        var casts = new EventProbe<AbilityCast>(world);

        Tick(world, 60 * 3);
        Assert.True(Apart(world, archer, player) >= 6.5f, $"it backed off to its range (it is {Apart(world, archer, player):F1} m away)");

        // Walk up to it again.
        var toward = Vector3.Normalize(At(world, archer) - At(world, player));
        world.Get<Transform>(player).LocalPosition = At(world, archer) - toward * 3f;
        Tick(world, 60 * 3);
        Assert.True(Apart(world, archer, player) >= 6.5f, $"it backed off again (it is {Apart(world, archer, player):F1} m away)");
        Assert.True(Apart(world, archer, player) <= 12f, "and did not run away: it stays in its spell's range");

        Tick(world, 60 * 2);
        Assert.Contains(casts.All, c => c.Caster == archer);
        Assert.True(world.Attribute(player, Conventional.Health) < 100f, "it shot from where it stood");
    }

    // The issue's second "done": a pack of two flanks, from data alone. One wolf sees the player; the other,
    // too far to see, is told by the squad; they spread to either side of the player and close from there.
    [Fact]
    public void APackOfTwoFlanks()
    {
        using var app = NewGame();
        var world = app.World;
        var player = Player(world, new Vector3(0, 0.1f, 0));
        var a = world.Spawn(Id("wolf"), new Vector3(-1, 0.1f, 9));
        var b = world.Spawn(Id("wolf"), new Vector3(1.5f, 0.1f, 13));
        Assert.False(a.IsNull || b.IsNull);
        var noises = new EventProbe<Noise>(world);

        Tick(world, 10);
        var squad = Squads.Of(world).Find("pack");
        Assert.NotNull(squad);
        Assert.Equal(new[] { a, b }, squad!.Members);
        Assert.Equal(player, squad.Target);
        Assert.Equal(player, world.Get<AIState>(b).Target);   // told by the squad...
        Assert.False(((AICondition)world.Get<AIState>(b).Conditions).HasFlag(AICondition.SeeEnemy), "...without seeing it");
        Assert.Equal(2, squad.EngagedCount);
        Assert.Contains(noises.All, n => n.Kind == NoiseKind.Alert && n.Source == a);   // the hunt starts with a shout

        float widest = 0f;
        for (int i = 0; i < 60 * 8; i++)
        {
            Tick(world);
            var p = At(world, player);
            if (Apart(world, a, player) > 3.6f || Apart(world, b, player) > 3.6f) continue;
            var da = At(world, a) - p;
            var db = At(world, b) - p;
            float angle = MathF.Abs(SageMath.WrapPi(SageMath.YawOf(da) - SageMath.YawOf(db))) * 180f / MathF.PI;
            widest = MathF.Max(widest, angle);
        }
        Assert.True(widest >= 90f, $"the two came at it from either side (at most {widest:F0} degrees apart)");
        Assert.True(world.Attribute(player, Conventional.Health) < 100f, "and bit");
    }

    // A call for help reaches a squadmate the squad's own sharing does not (it is outside the squad's range).
    [Fact]
    public void ACallForHelpBringsASquadmateOutOfRange()
    {
        using var app = NewGame();
        var world = app.World;
        var player = Player(world, new Vector3(0, 0.1f, 0));
        var caller = Creature(world, new Vector3(0, 0.1f, 8), "pack", "caller");
        var far = Creature(world, new Vector3(0, 0.1f, 22), "pack", "far");
        world.Add(caller, new Squad { Name = "pair", Range = 5 });
        world.Add(far, new Squad { Name = "pair", Range = 5 });

        Tick(world, 3);
        Assert.Equal(player, world.Get<AIState>(caller).Target);
        Tick(world, 6);
        Assert.Equal(player, world.Get<AIState>(far).Target);
        Assert.True(Squads.Of(world).Find("pair")!.CalledAt > 0f);
    }

    // Wounded, the coward runs (utility on `health`) and mends itself with its self-cast heal.
    [Fact]
    public void AWoundedCreatureRunsAndHealsItself()
    {
        using var app = NewGame();
        var world = app.World;
        var coward = Creature(world, new Vector3(0, 0.1f, 0), "coward", "coward", claws: false);
        world.Teach(coward, Id("mend"));
        SetHealth(world, coward, 20);
        var player = Player(world, new Vector3(0, 0.1f, -3));
        var casts = new EventProbe<AbilityCast>(world);

        float furthest = 0f;
        for (int i = 0; i < 60 * 6; i++)
        {
            Tick(world);
            furthest = MathF.Max(furthest, Apart(world, coward, player));
        }
        Assert.True(furthest >= 11.5f, $"it ran ({furthest:F1} m at most)");
        Assert.Contains(casts.All, c => c.Caster == coward && c.Ability == Id("mend"));
        Assert.True(world.Attribute(coward, Conventional.Health) > 20f, "it healed itself");
    }

    // It makes for the far side of a wall from what it saw, and the player cannot see it there.
    [Fact]
    public void ACreatureTakesCoverBehindAWall()
    {
        using var app = NewGame();
        var world = app.World;
        var wall = world.Create(Transform.At(new Vector3(0, 1.25f, -2)), "wall");
        world.Add(wall, Collider.Box(new Vector3(3, 2.5f, 0.4f)));
        var skulker = Creature(world, new Vector3(3, 0.1f, 0), "skulker", "skulker");
        var player = Player(world, new Vector3(0, 0.1f, -8));
        var space = world.Resources.Get<IPhysicsWorld>();
        var eye = At(world, player) + Vector3.UnitY * 1.4f;

        bool hidden = false;
        for (int i = 0; i < 60 * 5 && !hidden; i++)
        {
            Tick(world);
            var chest = At(world, skulker) + Vector3.UnitY;
            var look = chest - eye;
            var hit = space.Raycast(eye, Vector3.Normalize(look), look.Length(), LayerMask.All.Except(space.Layers.Enemy).Except(space.Layers.Player));
            hidden = hit.Hit && hit.Entity == wall;
        }
        Assert.True(hidden, $"it got behind the wall (it is at {At(world, skulker)})");
    }

    // It steps sideways round what it sees, keeping its distance and its face toward it.
    [Fact]
    public void ACreatureStrafesRoundItsTarget()
    {
        using var app = NewGame();
        var world = app.World;
        var dancer = Creature(world, new Vector3(0, 0.1f, 0), "dancer", "dancer");
        var player = Player(world, new Vector3(0, 0.1f, -6));
        var start = At(world, dancer);

        Tick(world, 90);
        var now = At(world, dancer);
        Assert.True(MathF.Abs(now.X - start.X) >= 1f, $"it stepped sideways (x {start.X:F1} -> {now.X:F1})");
        Assert.InRange(Apart(world, dancer, player), 4.5f, 8f);
        float facing = world.Get<PawnIntent>(dancer).Yaw;
        Assert.True(MathF.Abs(SageMath.WrapPi(SageMath.YawTo(now, At(world, player)) - facing)) < 0.5f, "it kept facing the player");
    }

    // A squad is its members' `squad` components: saved with them, and the squad is whole again after a load.
    [Fact]
    public void ASquadSurvivesASave()
    {
        using var app = NewGame();
        var world = app.World;
        var a = Creature(world, new Vector3(0, 0.1f, 0), "pack", "alpha");
        var b = Creature(world, new Vector3(3, 0.1f, 0), "pack", "beta");
        world.Add(a, new Squad { Name = "pack", Role = Squad.Leader, Range = 12 });
        world.Add(b, new Squad { Name = "pack" });
        Tick(world, 3);
        Assert.Equal(a, Squads.Of(world).Find("pack")!.Leader);
        Assert.True(app.Engine.Saves.Save("squad"));

        world.Remove<Squad>(b);
        Assert.True(app.Engine.Saves.Load("squad"));
        Tick(world, 2);
        var alpha = world.Resolve(PersistentId.FromName("alpha"));
        var beta = world.Resolve(PersistentId.FromName("beta"));
        Assert.Equal("pack", world.Get<Squad>(beta).Name);
        Assert.Equal(12f, world.Get<Squad>(alpha).Range);
        var squad = Squads.Of(world).Find("pack")!;
        Assert.Equal(2, squad.Members.Count);
        Assert.Equal(alpha, squad.Leader);
    }
}
