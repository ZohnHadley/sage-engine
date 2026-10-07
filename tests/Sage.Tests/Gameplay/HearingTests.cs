#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Hearing (docs/design/16 §3.4, issue #386): a Noise event, the engine's noises made from shots, blows and
// steps (NoiseSystem), and a creature that hears one, turns to it and walks over to look. Headless.
public class HearingTests
{
    public HearingTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Investigate = new("sage", "investigate");
    private static readonly RecordId Rifle = new("sage", "rifle");
    private static readonly RecordId Popgun = new("sage", "popgun");

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "damageType": "physical",
           "aiProfile": "default_ai",
           "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "melee_attack", "castSpell": "idle", "holdGround": "idle", "investigate": "investigate" },
           "noise": { "gunshotRadius": 60, "footstepRadius": 6 } },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "effect", "id": "damage", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },
         { "type": "attack", "id": "claws", "damage": 12, "reach": 2.2, "windupTime": 0.4, "recoverTime": 0.2, "cooldown": 1.0 },
         { "type": "attack", "id": "rifle", "damage": 30, "delivery": "ray", "range": 200 },
         { "type": "attack", "id": "popgun", "damage": 1, "delivery": "ray", "range": 200, "noiseRadius": 10 },

         { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 0.2 }], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "investigate",
           "tasks": ["FaceNoise", { "task": "MoveToNoise", "distance": 1.5 }, { "task": "Wait", "seconds": 1 }, "ForgetNoise"],
           "interrupts": ["SeeEnemy", "HearNoise"] }]
        """;

    private const byte EnemyLayer = 2, PlayerLayer = 1;

    private static Engine NewEngine() => HeadlessApp.Gameplay().File("data/hearing.json", Records).Build().Engine;

    private static World NewWorld(Engine engine)
    {
        var world = engine.CreateWorld("hearing");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return world;
    }

    // Placed facing -Z, so +Z is behind it.
    private static Entity Creature(World world, Vector3 position)
    {
        var entity = world.Create(Transform.At(position), "creature");
        world.AddCharacter(entity, EnemyLayer);
        world.Add(entity, new AIState { Schedule = Conventional.Idle });
        world.Add(entity, Melee.With(new RecordId("sage", "claws")));
        world.AddAttributes(entity);
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

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

#pragma warning disable SAGE0127   // WeaponFired is phase 4e's experimental API
    private static void Fire(World world, Entity shooter, RecordId attack) => world.Events.Send(new WeaponFired(shooter, attack));
#pragma warning restore SAGE0127

    private static float FacingError(World world, Entity creature, Vector3 toward) =>
        MathF.Abs(SageMath.WrapPi(SageMath.YawTo(world.Get<Transform>(creature).LocalPosition, toward) - world.Get<PawnIntent>(creature).Yaw));

    private static AICondition Conditions(World world, Entity creature) => (AICondition)world.Get<AIState>(creature).Conditions;

    // The issue's "done": a shot fired behind it and out of its sight range — it cannot see the shooter —
    // turns it round, and it walks toward where the shot came from.
    [Fact]
    public void ACreatureTurnsTowardAGunshotOutOfSightAndInvestigates()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, 40));     // behind it, and beyond its 25 m of sight
        Tick(world, 10);
        Assert.Equal(Conventional.Idle, world.Get<AIState>(creature).Schedule);
        Vector3 shot = world.Get<Transform>(player).LocalPosition;
        float before = FacingError(world, creature, shot);
        Assert.True(before > 3f, $"it starts with its back to the shot ({before:F2} rad)");

        Fire(world, player, Rifle);
        Tick(world, 10);

        var state = world.Get<AIState>(creature);
        Assert.True(Conditions(world, creature).HasFlag(AICondition.Suspicious));
        Assert.False(Conditions(world, creature).HasFlag(AICondition.SeeEnemy));
        Assert.Equal(Investigate, state.Schedule);
        Assert.True(state.Target.IsNull, "it heard something; it does not know who");
        Assert.Equal(player, state.HeardSource);
        Assert.InRange(state.Heard.Z, 39f, 41f);

        Tick(world, 60);
        Assert.True(FacingError(world, creature, shot) < 0.3f, $"it turned toward the shot ({FacingError(world, creature, shot):F2} rad off)");

        Tick(world, 120);
        float walked = world.Get<Transform>(creature).LocalPosition.Z;
        Assert.True(walked > 3f, $"it walked toward the shot ({walked:F1} m)");
        Assert.Equal(Investigate, world.Get<AIState>(creature).Schedule);
    }

    // An attack's own `noiseRadius` wins over the conventions' gunshot, and a creature further off than
    // it carries hears nothing.
    [Fact]
    public void ANoiseBeyondItsReachIsNotHeard()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, 30));
        Tick(world, 5);

        Fire(world, player, Popgun);   // heard to 10 m, from 30 m away
        Tick(world, 20);
        Assert.False(Conditions(world, creature).HasFlag(AICondition.Suspicious));
        Assert.Equal(Conventional.Idle, world.Get<AIState>(creature).Schedule);
        Vector3 shot = world.Get<Transform>(player).LocalPosition;
        float before = FacingError(world, creature, shot);
        Assert.True(before > 3f, $"it starts with its back to the shot ({before:F2} rad)");

        Fire(world, player, Rifle);    // the conventions' 60 m
        Tick(world, 20);
        Assert.True(Conditions(world, creature).HasFlag(AICondition.Suspicious));
    }

    // A wall between them leaves `occludedHearing` (half) of its hearing: a noise heard in the open at
    // 8 m of a 12 m radius is not heard through a wall.
    [Fact]
    public void AWallMufflesANoise()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var wall = world.Create(Transform.At(new Vector3(0, 2, 4)), "wall");
        world.Add(wall, Collider.Box(new Vector3(30, 4, 1)));
        Tick(world, 1);
        var muffled = Creature(world, new Vector3(0, 0.1f, 0));
        var open = Creature(world, new Vector3(20, 0.1f, 0));
        Tick(world, 5);

        Noises.Make(world, default, new Vector3(0, 1.4f, 8), radius: 12f, loudness: 1f);
        Noises.Make(world, default, new Vector3(20, 1.4f, 8), radius: 12f, loudness: 1f);
        Tick(world, 10);

        Assert.False(Conditions(world, muffled).HasFlag(AICondition.Suspicious), "through the wall");
        Assert.True(Conditions(world, open).HasFlag(AICondition.Suspicious), "in the open");
    }

    // Having walked over and looked round it lets it go (ForgetNoise) and goes back to what it was doing.
    [Fact]
    public void HavingLookedItForgetsTheNoiseAndGoesBackToIdling()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        Tick(world, 5);

        Noises.Make(world, default, new Vector3(0, 0.5f, 6), radius: 20f, loudness: 0.8f, kind: NoiseKind.Impact);
        Tick(world, 10);
        Assert.Equal(Investigate, world.Get<AIState>(creature).Schedule);

        bool arrived = false;
        for (int i = 0; i < 60 * 8 && world.Get<AIState>(creature).Schedule == Investigate; i++)
        {
            Tick(world, 1);
            if (SageMath.DistanceXZ(world.Get<Transform>(creature).LocalPosition, new Vector3(0, 0, 6)) <= 2f) arrived = true;
        }
        Assert.True(arrived, "it walked over to the noise");
        var state = world.Get<AIState>(creature);
        Assert.Equal(Conventional.Idle, state.Schedule);
        Assert.Equal(0f, state.HeardUntil);
        Assert.False(Conditions(world, creature).HasFlag(AICondition.Suspicious));
    }

    // Footsteps by speed: a step's radius is the conventions' at walking speed, scaled by how fast the
    // walker is going — a run carries further, a creep less far. A shot is a Weapon noise from the muzzle.
    [Fact]
    public void FootstepsCarryFurtherTheFasterTheWalkerGoes()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var walker = Player(world, new Vector3(0, 0.1f, 0));
        world.AddCharacter(walker, PlayerLayer);
        var noises = world.Events.Reader<Noise>("test");
        Tick(world, 1);
        foreach (ref readonly var _ in noises.Read()) { }

        float walk = MovementProfileRecord.Fallback.WalkSpeed;
        var heard = new List<Noise>();
        foreach (float speed in new[] { walk * 0.5f, walk, walk * 2f })
        {
            world.Get<CharacterController>(walker).Velocity = new Vector3(speed, 0, 0);
            world.Events.Send(new Footstep(walker, Vector3.Zero, default));
            world.RunFixed(1f / 60f);
            foreach (ref readonly var noise in noises.Read())
                if (noise.Kind == NoiseKind.Footstep) heard.Add(noise);
            world.Get<CharacterController>(walker).Velocity = Vector3.Zero;
        }

        Assert.Equal(3, heard.Count);
        Assert.Equal(walker, heard[0].Source);
        Assert.True(heard[0].Radius < heard[1].Radius && heard[1].Radius < heard[2].Radius,
            $"{heard[0].Radius:F1} < {heard[1].Radius:F1} < {heard[2].Radius:F1} m");

        Fire(world, walker, Rifle);
        world.RunFixed(1f / 60f);
        var shots = new List<Noise>();
        foreach (ref readonly var noise in noises.Read())
            if (noise.Kind == NoiseKind.Weapon) shots.Add(noise);
        var shot = Assert.Single(shots);
        Assert.Equal(60f, shot.Radius);
        Assert.Equal(walker, shot.Source);
    }

    // The footsteps of somebody it has no quarrel with are not news; a shot from the same place is.
    [Fact]
    public void AFriendsFootstepsAreNotNewsButItsShotIs()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        var friend = Creature(world, new Vector3(0, 0.1f, 4));
        Assert.False(Factions.AreEnemies(world, creature, friend));
        Tick(world, 5);

        Noises.Make(world, friend, new Vector3(0, 0.1f, 4), radius: 10f, loudness: 0.5f, kind: NoiseKind.Footstep);
        Tick(world, 10);
        Assert.False(Conditions(world, creature).HasFlag(AICondition.Suspicious));

        Noises.Make(world, friend, new Vector3(0, 1.4f, 4), radius: 30f, loudness: 1f, kind: NoiseKind.Weapon);
        Tick(world, 10);
        Assert.True(Conditions(world, creature).HasFlag(AICondition.Suspicious));
    }
}
