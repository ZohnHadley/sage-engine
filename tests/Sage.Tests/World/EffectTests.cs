#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Attributes, tags and effects (docs/design/16 §3.3, TODO F18). Everything here is simulation code:
// no graphics, no input, the same paths a server would run.
public class EffectTests
{
    public EffectTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Health = new("sage", "health");
    private static readonly RecordId Armor = new("sage", "armor");
    private static readonly RecordId Dead = new("sage", "state.dead");
    private static readonly RecordId Invulnerable = new("sage", "state.invulnerable");
    private static readonly RecordId Burning = new("sage", "state.burning");

    private const string Records = """
        [{ "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "attribute", "id": "armor",  "start": 10,  "min": 0, "max": 95 },

         { "type": "tag", "id": "state.dead" },
         { "type": "tag", "id": "state.invulnerable" },
         { "type": "tag", "id": "state.burning" },

         { "type": "effect", "id": "claw", "duration": "Instant", "blockTags": ["state.invulnerable"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -12 } ] },

         { "type": "effect", "id": "heal", "duration": "Instant",
           "modifiers": [ { "attribute": "health", "op": "Add", "value": 30 } ] },

         { "type": "effect", "id": "iron_skin", "duration": "Timed", "time": 2,
           "modifiers": [ { "attribute": "armor", "op": "Add", "value": 40 } ] },

         { "type": "effect", "id": "burning", "duration": "Timed", "time": 3, "period": 1,
           "grantTags": ["state.burning"],
           "modifiers": [ { "attribute": "health", "op": "Add", "value": -5 } ] },

         { "type": "effect", "id": "stacking_might", "duration": "Timed", "time": 5, "stacking": "Stack", "maxStacks": 3,
           "modifiers": [ { "attribute": "armor", "op": "Add", "value": 5 } ] },

         { "type": "effect", "id": "blessed", "duration": "Timed", "time": 5, "requireTags": ["state.burning"],
           "modifiers": [ { "attribute": "armor", "op": "Add", "value": 1 } ] }]
        """;

    private sealed class RecordingRules : GameRules
    {
        public int Deaths;
        public Entity LastVictim;
        public override void OnEntityDied(World world, Entity victim, Entity killer)
        {
            Deaths++;
            LastVictim = victim;
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
        var cvars = new CVarRegistry();
        var engine = new Engine(cvars, CoreCVars.Register(cvars));
        var rules = new RulesModule();
        engine.Modules.Add(new PhysicsModule());
        engine.Modules.AddGameplay();
        engine.Modules.Add(rules);
        engine.Modules.InitAll();

        var fixture = new MountFixture();
        fixture.Write("engine", "data/gameplay.json", Records);
        fixture.Mount("engine", "sage");
        engine.Records.Load(fixture.Vfs);

        engine.Modules.StartAll();
        return (engine, engine.CreateWorld("effects"), rules.Rules);
    }

    private static Entity Fighter(World world, string name = "fighter")
    {
        var entity = world.Create(Transform.At(Vector3.Zero), name);
        world.AddAttributes(entity);
        return entity;
    }

    private static void Tick(World world, float seconds)
    {
        for (int i = 0; i < (int)MathF.Round(seconds * 60); i++) world.RunFixed(1f / 60f);
    }

    [Fact]
    public void AttributesStartAtTheirRecordedValues()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var fighter = Fighter(world);
            Assert.Equal(100f, world.Attribute(fighter, Health));
            Assert.Equal(10f, world.Attribute(fighter, Armor));
            Assert.Equal(0f, world.Attribute(fighter, new RecordId("sage", "nonexistent")));
        }
    }

    [Fact]
    public void AnInstantEffectChangesTheValueAndIsClamped()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var fighter = Fighter(world);

            Assert.True(Effects.Apply(world, fighter, new RecordId("sage", "claw")));
            Assert.Equal(88f, world.Attribute(fighter, Health));

            Effects.Apply(world, fighter, new RecordId("sage", "heal"));
            Assert.Equal(100f, world.Attribute(fighter, Health));   // clamped to the record's max

            for (int i = 0; i < 20; i++) Effects.Apply(world, fighter, new RecordId("sage", "claw"));
            Assert.Equal(0f, world.Attribute(fighter, Health));     // and to its min
        }
    }

    [Fact]
    public void ATimedEffectAppliesWhileItRunsAndThenExpires()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var fighter = Fighter(world);
            Effects.Apply(world, fighter, new RecordId("sage", "iron_skin"));

            Tick(world, 0.5f);
            Assert.Equal(50f, world.Attribute(fighter, Armor));       // 10 + 40 while active
            Assert.True(Effects.IsActive(world, fighter, new RecordId("sage", "iron_skin")));

            Tick(world, 2f);
            Assert.Equal(10f, world.Attribute(fighter, Armor));       // back to the base value
            Assert.False(Effects.IsActive(world, fighter, new RecordId("sage", "iron_skin")));
        }
    }

    [Fact]
    public void APeriodicEffectTicksAndGrantsATagWhileItBurns()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var fighter = Fighter(world);
            Effects.Apply(world, fighter, new RecordId("sage", "burning"));

            Tick(world, 1.1f);
            Assert.True(world.HasTag(fighter, Burning));
            Assert.Equal(95f, world.Attribute(fighter, Health));      // one tick of 5

            Tick(world, 2.2f);
            Assert.Equal(85f, world.Attribute(fighter, Health));      // three ticks over three seconds
            Assert.False(world.HasTag(fighter, Burning), "the tag goes when the effect expires");
        }
    }

    [Fact]
    public void StacksAddUpToTheirLimit()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var fighter = Fighter(world);
            var might = new RecordId("sage", "stacking_might");
            for (int i = 0; i < 5; i++) Effects.Apply(world, fighter, might);

            Tick(world, 0.2f);
            Assert.Equal(25f, world.Attribute(fighter, Armor));       // 10 + 3 stacks of 5 (max 3)
        }
    }

    [Fact]
    public void TagsGateWhatCanBeApplied()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var fighter = Fighter(world);

            // "blessed" needs state.burning, which the fighter doesn't have yet.
            Assert.False(Effects.Apply(world, fighter, new RecordId("sage", "blessed")));

            Effects.Apply(world, fighter, new RecordId("sage", "burning"));
            Tick(world, 0.1f);
            Assert.True(Effects.Apply(world, fighter, new RecordId("sage", "blessed")));

            // The god cheat's tag blocks damage outright.
            world.AddTag(fighter, Invulnerable);
            float health = world.Attribute(fighter, Health);
            Assert.False(Effects.Apply(world, fighter, new RecordId("sage", "claw")));
            Assert.Equal(health, world.Attribute(fighter, Health));
        }
    }

    [Fact]
    public void RunningOutOfHealthTellsTheRulesOnce()
    {
        var (engine, world, rules) = NewWorld();
        using (engine)
        {
            var fighter = Fighter(world);
            for (int i = 0; i < 9; i++) Effects.Apply(world, fighter, new RecordId("sage", "claw"));

            Tick(world, 0.5f);

            Assert.Equal(0f, world.Attribute(fighter, Health));
            Assert.Equal(1, rules.Deaths);
            Assert.Equal(fighter, rules.LastVictim);
            Assert.True(world.HasTag(fighter, Dead));

            Tick(world, 1f);
            Assert.Equal(1, rules.Deaths);   // reported once, not every tick
        }
    }

    [Fact]
    public void AnEntityWithoutAttributesIsReportedRatherThanCrashing()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var prop = world.Create(Transform.At(Vector3.Zero), "prop");   // no AddAttributes
            using var capture = new CaptureSink();

            Assert.False(Effects.Apply(world, prop, new RecordId("sage", "claw")));
            Assert.Contains(capture.Entries, e => e.Message.Contains("has no attributes"));
        }
    }

    [Fact]
    public void AnUnknownEffectIsReported()
    {
        var (engine, world, _) = NewWorld();
        using (engine)
        {
            var fighter = Fighter(world);
            using var capture = new CaptureSink();

            Assert.False(Effects.Apply(world, fighter, new RecordId("sage", "no_such_effect")));
            Assert.Contains(capture.Entries, e => e.Message.Contains("No effect record"));
        }
    }
}
