#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Game rules and AI (docs/design/16 §3.1, §3.4; TODO F22). Headless, through the modules, the way a
// server would run them.
public class GameRulesTests
{
    public GameRulesTests() { _ = TestEnv.UserRoot; }

    private sealed class TestRules : GameRules
    {
        public World? Started;
        public int PlayersSpawned;

        public override void OnWorldStarted(World world)
        {
            Started = world;
            SpawnPlayer(world);
        }

        public override Entity SpawnPlayer(World world)
        {
            PlayersSpawned++;
            var player = world.Create(Transform.At(Vector3.Zero), "player");
            player.AddTag<PlayerControlled>();
            return player;
        }
    }

    private sealed class RulesModule : IModule
    {
        public readonly TestRules Rules = new();
        public void Init(ModuleContext ctx) { }
        public void OnWorldCreated(World world) => world.Resources.Set<GameRules>(Rules);
    }

    [Fact]
    public void TheEngineStartsTheGamesRulesOnceEveryModuleHasSeenTheWorld()
    {
        var cvars = new CVarRegistry();
        using var engine = new Engine(cvars, CoreCVars.Register(cvars));
        var module = new RulesModule();
        engine.Modules.Add(new PhysicsModule());
        engine.Modules.Add(new GameplayModule());
        engine.Modules.Add(module);
        engine.Modules.InitAll();
        engine.Modules.StartAll();

        var world = engine.CreateWorld("rules");

        Assert.Same(world, module.Rules.Started);
        Assert.Equal(1, module.Rules.PlayersSpawned);
        Assert.Equal(1, world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Count);
    }

    [Fact]
    public void AWorldWithoutAGamesRulesGetsTheDefaultOnes()
    {
        var cvars = new CVarRegistry();
        using var engine = new Engine(cvars, CoreCVars.Register(cvars));
        engine.Modules.Add(new PhysicsModule());
        engine.Modules.Add(new GameplayModule());
        engine.Modules.InitAll();
        engine.Modules.StartAll();

        var world = engine.CreateWorld("rules");
        Assert.IsType<DefaultGameRules>(world.Resources.Get<GameRules>());
    }
}

public class AITests
{
    public AITests() { _ = TestEnv.UserRoot; }

    // The engine's own AI records, as the Sandbox would load them from engine_content.
    private const string AiRecords = """
        [{ "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20, "attackCooldown": 1.0 },
         { "type": "ai_schedule", "id": "idle", "tasks": ["Wait:1.5"], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": ["MoveToTarget:1.6"], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", "MeleeAttack:0.2", "Wait:0.4"], "interrupts": ["LostEnemy", "NoEnemy"] }]
        """;

    private static Engine NewEngine(string? idleTask = null)
    {
        var cvars = new CVarRegistry();
        var engine = new Engine(cvars, CoreCVars.Register(cvars));
        engine.Modules.Add(new PhysicsModule());
        engine.Modules.Add(new GameplayModule());
        engine.Modules.InitAll();

        var fixture = new MountFixture();
        fixture.Write("engine", "data/ai.json", AiRecords);
        if (idleTask != null)   // patch the idle schedule, the way a mod would
            fixture.Write("engine", "data/patch.json",
                "[{ \"type\": \"ai_schedule\", \"id\": \"idle\", \"patch\": true, \"tasks\": [\"" + idleTask + "\"] }]");
        fixture.Mount("engine", "sage");
        engine.Records.Load(fixture.Vfs);

        engine.Modules.StartAll();
        return engine;
    }

    private static World NewWorld(Engine engine)
    {
        var world = engine.CreateWorld("ai");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return world;
    }

    private static Entity Creature(World world, Vector3 position)
    {
        var entity = world.Create(Transform.At(position), "creature");
        world.AddCharacter(entity, EnemyLayer);
        world.Add(entity, new AIState { Schedule = AIThinkSystem.Schedules.Idle });
        return entity;
    }

    private const byte EnemyLayer = 2, PlayerLayer = 1;

    private static Entity Player(World world, Vector3 position)
    {
        var entity = world.Create(Transform.At(position), "player");
        world.Add(entity, Collider.Standing(0.35f, 1.8f, PlayerLayer));
        world.Add(entity, RigidBody.Kinematic());
        entity.AddTag<PlayerControlled>();
        return entity;
    }

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static float Distance(World world, Entity a, Entity b) =>
        Vector3.Distance(world.Get<Transform>(a).LocalPosition, world.Get<Transform>(b).LocalPosition);

    [Fact]
    public void WithNothingToChaseItIdles()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, Vector3.Zero);

        Tick(world, 60);

        var state = world.Get<AIState>(creature);
        Assert.Equal(AIThinkSystem.Schedules.Idle, state.Schedule);
        Assert.True(((AICondition)state.Conditions).HasFlag(AICondition.NoEnemy));
        Assert.Equal(Vector2.Zero, world.Get<PawnIntent>(creature).Move);
    }

    [Fact]
    public void ItSeesThePlayerAndChasesUntilItIsInRange()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, -12));

        Tick(world, 10);
        var state = world.Get<AIState>(creature);
        Assert.True(((AICondition)state.Conditions).HasFlag(AICondition.SeeEnemy));
        Assert.Equal(AIThinkSystem.Schedules.Chase, state.Schedule);
        Assert.Equal(player, state.Target);

        float before = Distance(world, creature, player);
        Tick(world, 180);
        float after = Distance(world, creature, player);

        Assert.True(after < before - 5f, $"it should have closed the distance ({before:F1} → {after:F1} m)");
        Assert.True(after < 2.5f, $"and reached melee range (it is {after:F1} m away)");
    }

    // The profile has carried a sight angle since F22, but nothing read it, so creatures noticed the
    // player through the backs of their heads (review #50).
    [Fact]
    public void ItDoesNotNoticeAPlayerBehindItsBack()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));    // placed facing -Z
        Player(world, new Vector3(0, 0.1f, 12));                    // directly behind it

        Tick(world, 30);
        Assert.False(((AICondition)world.Get<AIState>(creature).Conditions).HasFlag(AICondition.SeeEnemy),
            "a 200° cone leaves a blind spot behind it");
        Assert.Equal(AIThinkSystem.Schedules.Idle, world.Get<AIState>(creature).Schedule);

        // Turn it round: same distance, same clear line of sight, now inside the cone.
        ref var intent = ref world.Get<PawnIntent>(creature);
        intent.Yaw = MathF.PI;
        Tick(world, 30);

        Assert.True(((AICondition)world.Get<AIState>(creature).Conditions).HasFlag(AICondition.SeeEnemy));
    }

    [Fact]
    public void AWallBreaksTheLineOfSight()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        Player(world, new Vector3(0, 0.1f, -12));

        var wall = world.Create(Transform.At(new Vector3(0, 2, -6)), "wall");
        world.Add(wall, Collider.Box(new Vector3(30, 4, 1)));

        Tick(world, 30);

        var state = world.Get<AIState>(creature);
        Assert.False(((AICondition)state.Conditions).HasFlag(AICondition.SeeEnemy));
        Assert.Equal(AIThinkSystem.Schedules.Idle, state.Schedule);
    }

    [Fact]
    public void InMeleeRangeItSwitchesToAttacking_AndAttacksOnItsCooldown()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, -1.2f));
        var events = world.Resources.Get<AIEvents>();

        int attacks = 0;
        for (int i = 0; i < 180; i++)   // three seconds
        {
            world.RunFixed(1f / 60f);
            foreach (var attack in events.Attacks)
                if (attack.Attacker == creature && attack.Target == player) attacks++;
        }

        Assert.Equal(AIThinkSystem.Schedules.Attack, world.Get<AIState>(creature).Schedule);
        Assert.InRange(attacks, 2, 4);   // once per ~1 s cooldown, not every tick
    }

    [Fact]
    public void AScheduleWithAnUnknownTaskIsReportedAndStops()
    {
        using var engine = NewEngine(idleTask: "NoSuchTask");
        var world = NewWorld(engine);
        Creature(world, Vector3.Zero);

        using var capture = new CaptureSink();
        Tick(world, 5);

        Assert.Contains(capture.Entries, e => e.Message.Contains("no AI task named 'NoSuchTask'"));
    }
}

public class AIMathTests
{
    private const float Deg = MathF.PI / 180f;

    [Fact]
    public void YawTo_MatchesTheViewConvention()
    {
        // Yaw 0 looks toward -Z, the same as PawnIntent.Yaw and the camera.
        Assert.Equal(0f, AIMath.YawTo(Vector3.Zero, new Vector3(0, 0, -5)), 4);
        Assert.Equal(MathF.PI / 2, MathF.Abs(AIMath.YawTo(Vector3.Zero, new Vector3(-5, 0, 0))), 4);
        Assert.Equal(MathF.PI, MathF.Abs(AIMath.YawTo(Vector3.Zero, new Vector3(0, 0, 5))), 4);
    }

    [Fact]
    public void TurnToward_TakesTheShortWayAndNeverOvershoots()
    {
        Assert.Equal(10 * Deg, AIMath.TurnToward(0, 90 * Deg, 10 * Deg), 4);
        Assert.Equal(-10 * Deg, AIMath.TurnToward(0, -90 * Deg, 10 * Deg), 4);
        Assert.Equal(45 * Deg, AIMath.TurnToward(0, 45 * Deg, 90 * Deg), 4);        // no overshoot
        Assert.Equal(-175 * Deg, AIMath.TurnToward(175 * Deg, -175 * Deg, 20 * Deg), 3);   // across the wrap
    }
}
