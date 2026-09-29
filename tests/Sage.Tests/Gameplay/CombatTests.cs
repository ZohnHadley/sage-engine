#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Combat (docs/design/16 §3.2, TODO F20): the damage pipeline and melee swings. All simulation, so
// these run headless — the same paths a dedicated server would.
public class CombatTests
{
    public CombatTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Health = new("sage", "health");
    private static readonly RecordId Armor = new("sage", "armor");
    private static readonly RecordId Physical = new("sage", "physical");
    private static readonly RecordId Sword = new("sage", "sword");
    private static readonly RecordId Invulnerable = new("sage", "state.invulnerable");

    private const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "invulnerable": "state.invulnerable", "damageType": "physical" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "armor",  "start": 0,   "min": 0, "max": 95 },

         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },

         { "type": "effect", "id": "damage", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "effect", "id": "plating", "duration": "Infinite",
           "modifiers": [ { "attribute": "armor", "op": "Add", "value": 40 } ] },
         { "type": "effect", "id": "impervious", "duration": "Infinite",
           "modifiers": [ { "attribute": "armor", "op": "Add", "value": 400 } ] },

         { "type": "damage_type", "id": "physical", "resist": "armor", "effect": "damage" },
         { "type": "damage_type", "id": "pure", "effect": "damage" },

         { "type": "attack", "id": "sword", "damage": 20, "damageType": "physical", "reach": 2.0,
           "radius": 0.3, "arcDegrees": 120, "windupTime": 0.2, "recoverTime": 0.1, "cooldown": 0.5 },

         { "type": "tag", "id": "state.poisoned" },
         { "type": "effect", "id": "venom", "duration": "Timed", "time": 5, "grantTags": ["state.poisoned"],
           "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "attack", "id": "poisoned_blade", "base": "sword", "effects": ["venom"] },

         { "type": "cue", "id": "swoosh" },
         { "type": "attack", "id": "loud_sword", "base": "sword", "swingCue": "swoosh" }]
        """;

    private sealed class RecordingRules : GameRules
    {
        public Entity LastVictim, LastKiller;
        public int Deaths;

        public override void OnEntityDied(World world, Entity victim, Entity killer)
        {
            Deaths++;
            LastVictim = victim;
            LastKiller = killer;
        }
    }

    private sealed class RulesModule : IModule
    {
        public readonly RecordingRules Rules = new();
        public void Init(ModuleContext ctx) { }
        public void OnWorldCreated(World world) => world.Resources.Replace<GameRules>(Rules);
    }

    private static (Engine Engine, World World, RecordingRules Rules) NewWorld()
    {
        var rules = new RulesModule();
        var app = HeadlessApp.Gameplay().With(rules).File("data/combat.json", Records).Boot("combat");
        var (engine, world) = (app.Engine, app.World);
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
        return (engine, world, rules.Rules);
    }

    // A fighter facing `lookAt`, with a sword and something to lose.
    private static Entity Fighter(World world, Vector3 feet, string name, Vector3 lookAt, bool armed = true)
    {
        var entity = world.Create(Transform.At(feet), name);
        world.AddCharacter(entity, world.Resources.Get<PhysicsSpace>().Layers.Player);
        if (armed) world.Add(entity, Melee.With(Sword));
        world.AddAttributes(entity);
        world.Get<PawnIntent>(entity).Yaw = SageMath.YawTo(feet, lookAt);
        return entity;
    }

    private static void Press(World world, Entity fighter, ActionId attack) =>
        world.Get<PawnIntent>(fighter).Pressed = world.Get<PawnIntent>(fighter).Pressed.With(attack);

    private static void Release(World world, Entity fighter) =>
        world.Get<PawnIntent>(fighter).Pressed = default;

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    [Fact]
    public void ResistanceTakesItsShareAndIsCappedBelowImmunity()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var victim = Fighter(world, Vector3.Zero, "victim", Vector3.UnitZ, armed: false);

            // No armour: all of it lands.
            Assert.Equal(20f, Combat.ApplyDamage(world, Hit(victim, 20f)), 3);

            // 40% armour: 40% of it stops here.
            Effects.Apply(world, victim, new RecordId("sage", "plating"));
            Tick(world, 1);
            Assert.Equal(40f, world.Attribute(victim, Armor), 3);
            Assert.Equal(12f, Combat.ApplyDamage(world, Hit(victim, 20f)), 3);

            // Armour past the cap still isn't immunity: the attribute's own max (95) decides.
            Effects.Apply(world, victim, new RecordId("sage", "impervious"));
            Tick(world, 1);
            Assert.Equal(1f, Combat.ApplyDamage(world, Hit(victim, 20f)), 3);

            // A damage type with no resistance attribute ignores armour entirely.
            Assert.Equal(20f, Combat.ApplyDamage(world, Hit(victim, 20f) with { Type = new RecordId("sage", "pure") }), 3);
        }
    }

    [Fact]
    public void TheGodTagStopsDamageBeforeItTouchesHealth()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var victim = Fighter(world, Vector3.Zero, "victim", Vector3.UnitZ, armed: false);
            world.AddTag(victim, Invulnerable);
            float before = world.Attribute(victim, Health);
            var damage = new EventProbe<Damaged>(world);

            Assert.Equal(0f, Combat.ApplyDamage(world, Hit(victim, 35f)));
            Assert.Equal(before, world.Attribute(victim, Health));
            Assert.Empty(damage.All);   // nothing happened, so nothing to react to
        }
    }

    // A swing says so where it starts, not where it lands: the weapon's own noise plays on the windup
    // and a miss is still information (11 §3, F4). The *hit* is the damage type's business.
    [Fact]
    public void ASwingAnnouncesItselfWhetherOrNotItLands()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var attacker = Fighter(world, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
            world.Get<Melee>(attacker).Attack = new RecordId("sage", "loud_sword");
            var attack = engine.Actions.Get("Attack");
            var cues = new EventProbe<CueTriggered>(world);
            var damage = new EventProbe<Damaged>(world);

            Press(world, attacker, attack);      // at nothing at all: the room is empty
            Tick(world, 1);
            Release(world, attacker);

            Assert.Single(cues.All);
            Assert.Equal(new RecordId("sage", "swoosh"), cues.All[0].Cue);
            Assert.Equal(attacker, cues.All[0].Source);

            Tick(world, 20);                     // past the windup, where a hit would have been
            Assert.Empty(damage.All);
            Assert.Single(cues.All);             // and one swing is one cue, not one per tick
        }
    }

    // The swing is a physics query (10 §4), so what you are facing is what you hit.
    [Fact]
    public void ASwingHitsWhatIsInFrontOfItAndNothingBehind()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var attacker = Fighter(world, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
            var target = Fighter(world, new Vector3(0, 0, -1.5f), "target", Vector3.Zero, armed: false);
            var behind = Fighter(world, new Vector3(0, 0, 1.5f), "behind", Vector3.Zero, armed: false);
            var attack = engine.Actions.Get("Attack");

            Press(world, attacker, attack);
            Tick(world, 1);
            Release(world, attacker);
            Tick(world, 20);                       // past the 0.2 s windup

            Assert.Equal(80f, world.Attribute(target, Health), 3);
            Assert.Equal(100f, world.Attribute(behind, Health), 3);
        }
    }

    [Fact]
    public void AFighterCannotSwingFasterThanItsCooldown()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var attacker = Fighter(world, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
            var target = Fighter(world, new Vector3(0, 0, -1.5f), "target", Vector3.Zero, armed: false);
            var attack = engine.Actions.Get("Attack");
            var damage = new EventProbe<Damaged>(world);

            for (int i = 0; i < 120; i++)          // two seconds of holding the button down
            {
                Press(world, attacker, attack);
                world.RunFixed(1f / 60f);
            }

            int hits = 0;
            foreach (var ev in damage.All) if (ev.Hit.Target == target) hits++;

            // windup 0.2 + recover 0.1 + cooldown 0.5 = a swing every 0.8 s.
            Assert.InRange(hits, 2, 3);
        }
    }

    [Fact]
    public void TheRulesAreToldWhoLandedTheKillingBlow()
    {
        var (engine, world, rules) = NewWorld();
        using (engine)
        {
            var attacker = Fighter(world, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
            var target = Fighter(world, new Vector3(0, 0, -1.5f), "target", Vector3.Zero, armed: false);
            var attack = engine.Actions.Get("Attack");

            for (int i = 0; i < 400 && rules.Deaths == 0; i++)
            {
                Press(world, attacker, attack);
                world.RunFixed(1f / 60f);
            }

            Assert.Equal(1, rules.Deaths);
            Assert.Equal(target, rules.LastVictim);
            Assert.Equal(attacker, rules.LastKiller);
            Assert.True(world.HasTag(target, new RecordId("sage", "state.dead")));
        }
    }

    [Fact]
    public void SwingingAtSceneryHurtsNothingAndSaysNothing()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var attacker = Fighter(world, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
            var wall = world.Create(Transform.At(new Vector3(0, 1, -1.5f)), "wall");
            world.Add(wall, Collider.Box(new Vector3(4, 2, 0.4f)));
            var attack = engine.Actions.Get("Attack");
            var damage = new EventProbe<Damaged>(world);

            Press(world, attacker, attack);
            Tick(world, 1);
            Release(world, attacker);
            Tick(world, 20);

            Assert.Empty(damage.All);
        }
    }

    // A poisoned blade: the effects an attack carries ride along with a hit that lands, and stop with
    // it when a tag blocks the damage.
    [Fact]
    public void AnAttacksOwnEffectsRideAlongWithTheHit()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var attacker = Fighter(world, Vector3.Zero, "attacker", new Vector3(0, 0, -2));
            world.Get<Melee>(attacker).Attack = new RecordId("sage", "poisoned_blade");
            var target = Fighter(world, new Vector3(0, 0, -1.5f), "target", Vector3.Zero, armed: false);
            var attack = engine.Actions.Get("Attack");
            var poisoned = new RecordId("sage", "state.poisoned");

            Press(world, attacker, attack);
            Tick(world, 1);
            Release(world, attacker);
            Tick(world, 20);

            Assert.True(world.HasTag(target, poisoned), "the blade's venom should be running on the target");
            Assert.True(world.Attribute(target, Health) < 80f, "and ticking on top of the cut");

            // Invulnerable: no cut, and no venom either.
            var warded = Fighter(world, new Vector3(0, 0, -1.5f), "warded", Vector3.Zero, armed: false);
            world.Destroy(target);
            world.AddTag(warded, Invulnerable);

            Press(world, attacker, attack);
            Tick(world, 60);
            Assert.False(world.HasTag(warded, poisoned));
            Assert.Equal(100f, world.Attribute(warded, Health), 3);
        }
    }

    [Fact]
    public void AnUnknownDamageTypeIsReportedRatherThanCrashing()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var victim = Fighter(world, Vector3.Zero, "victim", Vector3.UnitZ, armed: false);
            using var capture = new CaptureSink();

            Assert.Equal(0f, Combat.ApplyDamage(world, Hit(victim, 10f) with { Type = new RecordId("sage", "no_such_type") }));
            Assert.Contains(capture.Entries, e => e.Message.Contains("No damage_type record"));
        }
    }

    private static DamageInfo Hit(Entity victim, float amount) =>
        new(default, victim, Physical, amount, Vector3.Zero, -Vector3.UnitZ);
}
