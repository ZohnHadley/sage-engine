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
        engine.Modules.AddGameplay();
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
        engine.Modules.AddGameplay();
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
    // The AI records, plus the combat ones its swings go through (16 §3.2): a creature's claws are
    // an attack record like the player's, so both fight through MeleeCombatSystem.
    private const string AiRecords = """
        [{ "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "armor", "start": 0, "min": 0, "max": 95 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "effect", "id": "damage", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "resist": "armor", "effect": "damage" },
         { "type": "attack", "id": "claws", "damage": 12, "damageType": "physical", "reach": 2.2,
           "radius": 0.45, "windupTime": 0.4, "recoverTime": 0.2, "cooldown": 1.0 },

         { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": ["Wait:1.5"], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": ["MoveToTarget:1.6"], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", "MeleeAttack:0.2", "Wait:0.4"], "interrupts": ["LostEnemy", "NoEnemy"] },

         { "type": "attribute", "id": "mana", "start": 100, "min": 0, "max": 100, "spendEffect": "spend_mana" },
         { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
         { "type": "tag", "id": "cooldown.bolt" },
         { "type": "effect", "id": "bolt_cooldown", "duration": "Timed", "time": 1.0, "grantTags": ["cooldown.bolt"], "modifiers": [] },
         { "type": "ability", "id": "bolt", "name": "bolt", "costAttribute": "mana", "cost": 10,
           "cooldown": "bolt_cooldown", "targeting": "Touch", "range": 12, "width": 0.3,
           "castTime": 0.2, "damage": 7, "damageType": "physical" },
         { "type": "ai_schedule", "id": "cast_spell", "tasks": ["FaceTarget", "CastSpell:2", "Wait:0.4"], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "hold_ground", "tasks": ["FaceTarget", "Wait:0.3"], "interrupts": ["LostEnemy", "NoEnemy"] }]
        """;

    private static Engine NewEngine(string? idleTask = null)
    {
        var cvars = new CVarRegistry();
        var engine = new Engine(cvars, CoreCVars.Register(cvars));
        engine.Modules.Add(new PhysicsModule());
        engine.Modules.AddGameplay();
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
        world.Add(entity, Melee.With(new RecordId("sage", "claws")));
        world.AddAttributes(entity);
        return entity;
    }

    // A creature that casts instead of swinging: no `Melee` at all, which is the case that used to
    // walk into reach and stand there (16 3.4).
    private static Entity Caster(World world, Vector3 position, params string[] spells)
    {
        var entity = world.Create(Transform.At(position), "caster");
        world.AddCharacter(entity, EnemyLayer);
        world.Add(entity, new AIState { Schedule = AIThinkSystem.Schedules.Idle });
        world.AddAttributes(entity);
        foreach (string spell in spells.Length > 0 ? spells : new[] { "bolt" })
            world.Teach(entity, new RecordId("sage", spell));
        return entity;
    }

    private const byte EnemyLayer = 2, PlayerLayer = 1;

    private static Entity Player(World world, Vector3 position)
    {
        var entity = world.Create(Transform.At(position), "player");
        world.Add(entity, Collider.Standing(0.35f, 1.8f, PlayerLayer));
        world.Add(entity, RigidBody.Kinematic());
        world.AddAttributes(entity);        // something to lose: the creature's claws are real now (F20)
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

    // The regression guard for review #48, now that the promise is checked rather than commented
    // (03 §3.5, R16). `AIThinkSystem` used to run in Phase.AI while CharacterMovementSystem consumed
    // the intent in PrePhysics, so every creature acted a tick late. Move it back and this fails:
    // CharacterModule declares PawnIntent final after Commands, and a real creature chasing a real
    // player exercises both controllers.
    [Fact]
    public void NothingWritesPawnIntentAfterTheCommandsPhase()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, -3f));

        Assert.True(world.Contracts.Count > 0, "GameplayModule should declare the PawnIntent contract");

        for (int i = 0; i < 180; i++)   // three seconds: idle, notice, chase and attack all happen
            world.RunFixed(1f / 60f);

        Assert.Equal(0, world.Contracts.Violations);
    }

    [Fact]
    public void InMeleeRangeItSwitchesToAttacking_AndAttacksOnItsCooldown()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, -1.2f));
        var damage = new EventProbe<Damaged>(world);

        for (int i = 0; i < 180; i++)   // three seconds
            world.RunFixed(1f / 60f);

        int attacks = 0;
        foreach (var ev in damage.All)
            if (ev.Hit.Attacker == creature && ev.Hit.Target == player) attacks++;

        Assert.Equal(AIThinkSystem.Schedules.Attack, world.Get<AIState>(creature).Schedule);
        Assert.InRange(attacks, 2, 4);   // once per ~1 s cooldown, not every tick
        Assert.True(world.Attribute(player, AttributeRecord.Health) <= 100f - attacks * 12f + 0.01f,
            "every swing that landed took health off the player");
    }

    // ---- casting (16 3.3, 3.4) ---------------------------------------------------------------------

    // A creature's spell is the player's spell: queued with `world.Cast`, gated by `AbilitySystem`,
    // delivered by the same payload. Nothing about casting knows an AI is doing it.
    [Fact]
    public void ACasterThrowsSpellsAtThePlayerFromWhereItStands()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var caster = Caster(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, -8));
        var casts = new EventProbe<AbilityCast>(world);

        float away = Distance(world, caster, player);
        Tick(world, 180);

        // Casting, or holding its ground between casts -- never chasing (see the next test).
        var schedule = world.Get<AIState>(caster).Schedule;
        Assert.True(schedule == AIThinkSystem.Schedules.Cast || schedule == AIThinkSystem.Schedules.Hold,
            $"it should be fighting at range, not {schedule}");
        Assert.Contains(casts.All, c => c.Caster == caster);
        Assert.True(world.Attribute(player, AttributeRecord.Health) < 100f, "its bolts should have hurt the player");

        // It stayed where it was: casting is chosen over closing, so it does not walk into reach it
        // has no use for. (Backing away to keep the distance is a ranged behaviour for later.)
        Assert.True(Distance(world, caster, player) > away - 1.5f,
            $"a caster should not have charged: it was {away:F1} m away and is now {Distance(world, caster, player):F1}");
    }

    // The point of extracting `AbilityRules`: what the AI decides and what the cast system allows are
    // one set of rules. With the mana gone it must not ask at all -- an agent that queued a cast every
    // think would flood the bus with refusals and stand in a cast schedule that can only fail.
    [Fact]
    public void ItDoesNotAskForASpellItCannotPayFor()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var caster = Caster(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, -8));
        var refusals = new EventProbe<CastRefused>(world);

        Effects.Apply(world, caster, new RecordId("sage", "spend_mana"), caster, 95f);   // 100 -> 5, a bolt costs 10
        Tick(world, 120);

        var state = world.Get<AIState>(caster);
        Assert.False(((AICondition)state.Conditions).HasFlag(AICondition.CanCastAtEnemy));
        Assert.True(state.Spell.IsEmpty);
        Assert.NotEqual(AIThinkSystem.Schedules.Cast, state.Schedule);
        Assert.DoesNotContain(refusals.All, r => r.Caster == caster);
        Assert.Equal(100f, world.Attribute(player, AttributeRecord.Health));
    }

    // Out of range is the same question: the spell's own `Range` is what the cast will be resolved
    // against, so it is what the decision uses.
    [Fact]
    public void ItClosesTheDistanceUntilItsSpellReaches()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var caster = Caster(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, -20));   // a 12 m bolt cannot reach

        Tick(world, 20);
        Assert.Equal(AIThinkSystem.Schedules.Chase, world.Get<AIState>(caster).Schedule);

        Tick(world, 400);
        var reached = world.Get<AIState>(caster).Schedule;
        Assert.True(reached == AIThinkSystem.Schedules.Cast || reached == AIThinkSystem.Schedules.Hold,
            $"once in range it should be casting, not {reached}");
        Assert.True(world.Attribute(player, AttributeRecord.Health) < 100f);
    }

    // A creature that can swing swings when it is close: casting must not steal melee's job, or every
    // creature with a spell becomes a mage who forgot it has claws.
    [Fact]
    public void AClawedCreatureStillSwingsWhenItIsInReach()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        world.Teach(creature, new RecordId("sage", "bolt"));
        Player(world, new Vector3(0, 0.1f, -1.2f));

        Tick(world, 120);

        Assert.Equal(AIThinkSystem.Schedules.Attack, world.Get<AIState>(creature).Schedule);
    }

    // A projectile leaves the caster's eye and a body is lower down, so the aim has a pitch. Without
    // it a creature on a ledge fires over the player's head.
    [Fact]
    public void ItAimsDownAtSomethingBelowIt()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var caster = Caster(world, new Vector3(0, 6f, 0));
        var ledge = world.Create(Transform.At(new Vector3(0, 5.5f, 0)), "ledge");
        world.Add(ledge, Collider.Box(new Vector3(3, 1, 3)));      // something to stand on
        Player(world, new Vector3(0, 0.1f, -6));

        Tick(world, 90);

        Assert.True(world.Get<PawnIntent>(caster).Pitch < -0.3f,
            $"it should be aiming downward, not at {world.Get<PawnIntent>(caster).Pitch:F2} rad");
    }

    // The gate the spellmaker screen and the HUD will both ask. What `AbilityRules` says and what
    // casting does must agree, so the test asks both about the same caster in the same state.
    [Fact]
    public void AskingWhetherItCanCastGivesTheAnswerCastingGives()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var caster = Caster(world, new Vector3(0, 0.1f, 0));
        var bolt = new RecordId("sage", "bolt");
        var refusals = new EventProbe<CastRefused>(world);

        Assert.True(AbilityRules.CanCast(world, caster, bolt, out _));

        Effects.Apply(world, caster, new RecordId("sage", "spend_mana"), caster, 95f);
        Tick(world, 2);

        Assert.False(AbilityRules.CanCast(world, caster, bolt, out var why));
        Assert.Equal(CastRefusal.TooExpensive, why);

        world.Cast(caster, bolt);
        Tick(world, 2);
        Assert.Contains(refusals.All, r => r.Caster == caster && r.Why == CastRefusal.TooExpensive);
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
