#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

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
        public void OnWorldCreated(World world) => world.Resources.Replace<GameRules>(Rules);
    }

    [Fact]
    public void TheEngineStartsTheGamesRulesOnceEveryModuleHasSeenTheWorld()
    {
        var module = new RulesModule();
        using var app = HeadlessApp.Gameplay().With(module).Boot("rules");
        var world = app.World;

        Assert.Same(world, module.Rules.Started);
        Assert.Equal(1, module.Rules.PlayersSpawned);
        Assert.Equal(1, world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Count);
    }

    [Fact]
    public void AWorldWithoutAGamesRulesGetsTheDefaultOnes()
    {
        using var app = HeadlessApp.Gameplay().Boot("rules");
        var world = app.World;
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
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable", "damageType": "physical",
           "aiProfile": "default_ai", "costAttribute": "mana", "schedules": { "idle": "idle", "chase": "chase", "meleeAttack": "melee_attack", "castSpell": "cast_spell", "holdGround": "hold_ground" } },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "armor", "start": 0, "min": 0, "max": 95 },
         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "effect", "id": "damage", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "resist": "armor", "effect": "damage" },
         { "type": "attack", "id": "claws", "damage": 12, "damageType": "physical", "reach": 2.2,
           "radius": 0.45, "windupTime": 0.4, "recoverTime": 0.2, "cooldown": 1.0 },

         { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 20 },
         { "type": "ai_schedule", "id": "idle", "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
         { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 0.2 }, { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] },

         { "type": "attribute", "id": "mana", "start": 100, "min": 0, "max": 100, "spendEffect": "spend_mana" },
         { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
         { "type": "tag", "id": "cooldown.bolt" },
         { "type": "effect", "id": "bolt_cooldown", "duration": "Timed", "time": 1.0, "grantTags": ["cooldown.bolt"], "modifiers": [] },
         { "type": "ability", "id": "bolt", "name": "bolt", "costAttribute": "mana", "cost": 10,
           "cooldown": "bolt_cooldown", "targeting": "Touch", "range": 12, "width": 0.3,
           "castTime": 0.2, "damage": 7, "damageType": "physical" },
         { "type": "ai_schedule", "id": "cast_spell", "tasks": ["FaceTarget", { "task": "CastSpell", "giveUpAfter": 2 }, { "task": "Wait", "seconds": 0.4 }], "interrupts": ["LostEnemy", "NoEnemy"] },
         { "type": "ai_schedule", "id": "hold_ground", "tasks": ["FaceTarget", { "task": "Wait", "seconds": 0.3 }], "interrupts": ["LostEnemy", "NoEnemy"] }]
        """;

    // `idleTask` is one task as JSON: "\"FaceTarget\"" or "{ \"task\": \"Wait\", \"seconds\": 1 }".
    private static Engine NewEngine(string? idleTask = null)
    {
        var builder = HeadlessApp.Gameplay().File("data/ai.json", AiRecords);
        if (idleTask != null)   // patch the idle schedule, the way a mod would
            builder.File("data/patch.json",
                "[{ \"type\": \"ai_schedule\", \"id\": \"idle\", \"patch\": true, \"tasks\": [" + idleTask + "] }]");
        return builder.Build().Engine;
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
        world.Add(entity, new AIState { Schedule = Conventional.Idle });
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
        world.Add(entity, new AIState { Schedule = Conventional.Idle });
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

    // The AI and physics debug views keep what they show in the visual log (issue #300): with vlog_record
    // on, each agent's sight cone, its target and a line of what it is doing ("ai"), and each character's
    // capsule and ground ("physics"), per tick — scrubbable back to the tick before it saw the player.
    [Fact]
    public void TheVisualLogKeepsWhatAnAgentSawAndWhereItStood()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));   // placed facing -Z
        var player = Player(world, new Vector3(0, 0.1f, -12));     // in front of it, in sight
        Assert.True(engine.CVars.Execute("vlog_record 1"));

        Tick(world, 60);

        var log = world.VisualLog();
        Assert.Contains("ai", log.Categories);
        Assert.Contains("physics", log.Categories);

        var now = new System.Collections.Generic.List<VisualLogEntry>();
        log.CollectAt(log.NewestTick, now);
        Assert.Contains(now, e => e.Category == "ai" && e.Shape == VisualShape.Cone && e.Entity == creature);
        Assert.Contains(now, e => e.Category == "ai" && e.Shape == VisualShape.Line && e.Entity == creature);   // at the player
        Assert.Contains(now, e => e.Category == "ai" && e.Entity == player);                                     // the cross on its target
        Assert.Contains(now, e => e.Category == "ai" && e.Text != null && e.Text.Contains("target player"));
        Assert.Contains(now, e => e.Category == "physics" && e.Shape == VisualShape.Capsule && e.Entity == creature && e.Text != null);

        // The first tick is still there to scrub back to, with where it stood then.
        Assert.Equal(1, log.OldestTick);
        Assert.True(engine.CVars.Execute("vlog_at 1"));
        var first = new System.Collections.Generic.List<VisualLogEntry>();
        log.CollectAt(log.ShownTick, first);
        Assert.All(first, e => Assert.Equal(1, e.Tick));
        var then = Assert.Single(first, e => e.Category == "ai" && e.Shape == VisualShape.Cone);
        var at = Assert.Single(now, e => e.Category == "ai" && e.Shape == VisualShape.Cone);
        Assert.True(then.A.Z > at.A.Z + 1f, "it has chased the player since");
    }

    [Fact]
    public void WithNothingToChaseItIdles()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, Vector3.Zero);

        Tick(world, 60);

        var state = world.Get<AIState>(creature);
        Assert.Equal(Conventional.Idle, state.Schedule);
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
        Assert.Equal(Conventional.Chase, state.Schedule);
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
        Assert.Equal(Conventional.Idle, world.Get<AIState>(creature).Schedule);

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

        // The wall first, and a tick for it to get its body: colliders join the physics space in
        // PrePhysics, and the AI thinks in Commands *before* that (16 §3.4). A creature created in the
        // same breath as the wall gets one tick of seeing straight through it — which, now that a
        // creature remembers what it has seen (F23), is six seconds of chasing a wall.
        var wall = world.Create(Transform.At(new Vector3(0, 2, -6)), "wall");
        world.Add(wall, Collider.Box(new Vector3(30, 4, 1)));
        Tick(world, 1);

        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        Player(world, new Vector3(0, 0.1f, -12));

        Tick(world, 30);

        var state = world.Get<AIState>(creature);
        Assert.False(((AICondition)state.Conditions).HasFlag(AICondition.SeeEnemy));
        Assert.Equal(Conventional.Idle, state.Schedule);
    }

    // What a creature does with what it saw a moment ago (16 §3.4, F23): it keeps chasing for
    // `memorySeconds` and walks to where the target *was*, then gives up. Without this, pathfinding is
    // decoration — walking round something means facing away from it, and sight is a cone.
    [Fact]
    public void ACreatureRemembersWhatItCanNoLongerSeeAndThenForgets()
    {
        using var engine = NewEngine();
        var world = NewWorld(engine);
        var creature = Creature(world, new Vector3(0, 0.1f, 0));
        var player = Player(world, new Vector3(0, 0.1f, -8));
        Tick(world, 30);

        var seen = world.Get<AIState>(creature);
        Assert.True(((AICondition)seen.Conditions).HasFlag(AICondition.SeeEnemy));
        Assert.Equal(Conventional.Chase, seen.Schedule);

        // Out of sight: far beyond the profile's sight range. (Straight up does *not* work — sight is
        // range and cone plus a ray, and a player 400 m overhead is still in the cone with nothing in
        // the way, which is its own oddity and not this test's business.)
        world.Get<Transform>(player).LocalPosition = new Vector3(0, 0.1f, -300f);
        Tick(world, 30);

        var remembering = world.Get<AIState>(creature);
        Assert.True(((AICondition)remembering.Conditions).HasFlag(AICondition.RememberEnemy),
            "the creature forgot its target the instant it lost sight of it");
        Assert.Equal(Conventional.Chase, remembering.Schedule);
        Assert.False(remembering.Target.IsNull);

        // Long enough for the default six seconds of memory to run out.
        Tick(world, 60 * 7);

        var forgotten = world.Get<AIState>(creature);
        Assert.False(((AICondition)forgotten.Conditions).HasFlag(AICondition.RememberEnemy));
        Assert.True(forgotten.Target.IsNull, "a forgotten target is not still being chased");
        Assert.Equal(Conventional.Idle, forgotten.Schedule);
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

        Assert.Equal(Conventional.MeleeAttack, world.Get<AIState>(creature).Schedule);
        Assert.InRange(attacks, 2, 4);   // once per ~1 s cooldown, not every tick
        Assert.True(world.Attribute(player, Conventional.Health) <= 100f - attacks * 12f + 0.01f,
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
        Assert.True(schedule == Conventional.CastSpell || schedule == Conventional.HoldGround,
            $"it should be fighting at range, not {schedule}");
        Assert.Contains(casts.All, c => c.Caster == caster);
        Assert.True(world.Attribute(player, Conventional.Health) < 100f, "its bolts should have hurt the player");

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
        Assert.NotEqual(Conventional.CastSpell, state.Schedule);
        Assert.DoesNotContain(refusals.All, r => r.Caster == caster);
        Assert.Equal(100f, world.Attribute(player, Conventional.Health));
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
        Assert.Equal(Conventional.Chase, world.Get<AIState>(caster).Schedule);

        Tick(world, 400);
        var reached = world.Get<AIState>(caster).Schedule;
        Assert.True(reached == Conventional.CastSpell || reached == Conventional.HoldGround,
            $"once in range it should be casting, not {reached}");
        Assert.True(world.Attribute(player, Conventional.Health) < 100f);
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

        Assert.Equal(Conventional.MeleeAttack, world.Get<AIState>(creature).Schedule);
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
        using var engine = NewEngine(idleTask: "\"NoSuchTask\"");
        var world = NewWorld(engine);
        Creature(world, Vector3.Zero);

        using var capture = new CaptureSink();
        Tick(world, 5);

        Assert.Contains(capture.Entries, e => e.Message.Contains("no AI task named 'NoSuchTask'"));
    }

    // A task's number is named (issue #22), and a name the task does not take is how seconds end up
    // read as metres: said once, and the schedule stops rather than running on a guess.
    [Fact]
    public void ATaskGivenAnArgumentItDoesNotTakeIsReportedAndStops()
    {
        using var engine = NewEngine(idleTask: "{ \"task\": \"Wait\", \"distance\": 2 }");
        var world = NewWorld(engine);
        var creature = Creature(world, Vector3.Zero);

        using var capture = new CaptureSink();
        Tick(world, 5);

        // Where the step is written, like a load error (issue #22).
        Assert.Contains(capture.Entries, e => e.Message.EndsWith(": ai_schedule sage:idle: task 'Wait' takes 'seconds', not 'distance'; the schedule stops here") &&
                                              e.Message.StartsWith("engine:data/"));
        Assert.Equal(Vector2.Zero, world.Get<PawnIntent>(creature).Move);
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
