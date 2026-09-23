#nullable enable
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Abilities and magic (docs/design/16 §3.3, TODO F21).
//
// What an ability *does* is effects, which F18 already tests. What is new is the gating — you must
// know it, it must be ready, you must afford it, you must be allowed — and the targeting. So that is
// what these pin, plus the property the whole design rests on: a spell changes an entity through the
// same path a sword does, so everything built on that path keeps working without knowing spells exist.
public class AbilityTests
{
    public AbilityTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "attribute", "id": "mana",   "max": 50,  "start": 50, "spendEffect": "spend_mana" },
      { "type": "attribute", "id": "armour", "max": 95 },
      { "type": "damage_type", "id": "fire", "resist": "armour", "effect": "burn" },

      { "type": "tag", "id": "state.invulnerable" },
      { "type": "tag", "id": "cooldown.fireball" },
      { "type": "tag", "id": "silenced" },

      { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "burn", "blockTags": ["state.invulnerable"],
        "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "mend",       "modifiers": [ { "attribute": "health", "op": "Add", "value": 1 } ] },
      { "type": "effect", "id": "fireball_cooldown", "duration": "Timed", "time": 3,
        "modifiers": [], "grantTags": ["cooldown.fireball"] },

      { "type": "ability", "id": "heal", "name": "heal",
        "costAttribute": "mana", "cost": 10,
        "targeting": "Self", "effects": ["mend"], "magnitude": 25 },

      { "type": "ability", "id": "fireball", "name": "fireball",
        "costAttribute": "mana", "cost": 20, "cooldown": "fireball_cooldown",
        "targeting": "TouchArea", "range": 12, "radius": 3,
        "effects": ["burn"], "magnitude": 30, "cues": ["boom"] },

      { "type": "ability", "id": "flame_bolt", "name": "flame bolt",
        "targeting": "Touch", "range": 12, "damage": 40, "damageType": "fire" },

      { "type": "ability", "id": "chant", "name": "chant",
        "targeting": "Self", "effects": ["mend"], "magnitude": 5,
        "blockTags": ["silenced"] }
    ]
    """;

    private static (Engine Engine, World World) NewWorld()
    {
        var cvars = new CVarRegistry();
        var engine = new Engine(cvars, CoreCVars.Register(cvars));
        engine.Modules.Add(new PhysicsModule());
        engine.Modules.AddGameplay();
        engine.Modules.InitAll();

        var fixture = new MountFixture();
        fixture.Write("engine", "data/spells.json", Records);
        fixture.Mount("engine", "sage");
        engine.Records.Load(fixture.Vfs);
        engine.Modules.StartAll();

        var world = engine.CreateWorld("spells");
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(200, 1, 200)));
        return (engine, world);
    }

    private static RecordId Id(string name) => new("sage", name);

    private static Entity Caster(World world, Vector3 at, string name, params string[] known)
    {
        var e = world.Create(Transform.At(at), name);
        world.AddCharacter(e, world.Resources.Get<PhysicsSpace>().Layers.Player);
        world.AddAttributes(e);
        foreach (var ability in known) world.Teach(e, Id(ability));
        return e;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    [Xunit.Fact]
    public void CastingSpendsTheCostAndAppliesTheEffect()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "heal");
            Effects.Apply(world, caster, Id("burn"), caster, 60f);   // hurt through the one path
            Tick(world);
            Assert.Equal(40f, world.Attribute(caster, Id("health")));

            world.Cast(caster, Id("heal"));
            Tick(world, 2);

            Assert.Equal(65f, world.Attribute(caster, Id("health")));   // +25 from one mend at magnitude 25
            Assert.Equal(40f, world.Attribute(caster, Id("mana")));     // 50 - 10, through an effect
        }
    }

    // The property the design rests on: a spell is an effect, so the tag that stops a sword stops it.
    // Note *where* that is decided — the `burn` effect declares `blockTags`, exactly as the engine's
    // own damage effect does (engine_content/data/combat.json). Nothing in the ability system knows
    // the `god` cheat exists, and nothing had to be added for spells to respect it.
    [Xunit.Fact]
    public void AnInvulnerableTargetIsUnharmedByASpell()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "fireball");
            var victim = Caster(world, new Vector3(0, 0.1f, -4), "victim");
            world.AddTag(victim, TagRecord.Invulnerable);

            world.Cast(caster, Id("fireball"));
            Tick(world, 2);

            Assert.Equal(100f, world.Attribute(victim, Id("health")));
        }
    }

    [Xunit.Fact]
    public void AnAbilityYouDoNotKnowIsRefused()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "heal");
            var refusals = new EventProbe<CastRefused>(world);

            world.Cast(caster, Id("fireball"));
            Tick(world, 2);

            Assert.Equal(CastRefusal.NotKnown, refusals.All.Single().Why);
            Assert.Equal(50f, world.Attribute(caster, Id("mana")));   // and it cost nothing
        }
    }

    [Xunit.Fact]
    public void AnAbilityYouCannotAffordIsRefused()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "fireball");
            Effects.Apply(world, caster, Id("spend_mana"), caster, 45f);   // down to 5
            Tick(world);
            var refusals = new EventProbe<CastRefused>(world);

            world.Cast(caster, Id("fireball"));
            Tick(world, 2);

            Assert.Equal(CastRefusal.TooExpensive, refusals.All.Single().Why);
            Assert.Equal(5f, world.Attribute(caster, Id("mana")));
        }
    }

    // The cooldown is an effect granting a tag, not a timer (16 §3.3): one clock, and dispelling it
    // makes the spell ready again.
    [Xunit.Fact]
    public void ACooldownIsATagAndItExpires()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "fireball");
            var refusals = new EventProbe<CastRefused>(world);

            world.Cast(caster, Id("fireball"));
            Tick(world, 2);
            Assert.True(world.HasTag(caster, Id("cooldown.fireball")));

            world.Cast(caster, Id("fireball"));
            Tick(world, 2);
            Assert.Equal(CastRefusal.OnCooldown, refusals.All.Single().Why);

            Tick(world, 190);   // the cooldown effect runs out after 3 s
            Assert.False(world.HasTag(caster, Id("cooldown.fireball")));

            world.Cast(caster, Id("fireball"));
            Tick(world, 2);
            Assert.Single(refusals.All);   // no second refusal: it was ready
        }
    }

    [Xunit.Fact]
    public void ABlockingTagRefusesTheCast()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "chant");
            world.AddTag(caster, Id("silenced"));
            var refusals = new EventProbe<CastRefused>(world);

            world.Cast(caster, Id("chant"));
            Tick(world, 2);

            Assert.Equal(CastRefusal.Blocked, refusals.All.Single().Why);
        }
    }

    // A fireball catches everything near where it lands, not just what it hit.
    [Xunit.Fact]
    public void AnAreaSpellCatchesEverythingNearWhereItLands()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "fireball");
            var near = Caster(world, new Vector3(0, 0.1f, -5), "near");
            var beside = Caster(world, new Vector3(1.5f, 0.1f, -5), "beside");
            var far = Caster(world, new Vector3(0, 0.1f, -30), "far");
            var casts = new EventProbe<AbilityCast>(world);

            world.Get<PawnIntent>(caster).Yaw = 0f;   // facing -Z
            world.Cast(caster, Id("fireball"));
            Tick(world, 2);

            Assert.True(world.Attribute(near, Id("health")) < 100f, "the thing it hit should burn");
            Assert.True(world.Attribute(beside, Id("health")) < 100f, "and so should its neighbour");
            Assert.Equal(100f, world.Attribute(far, Id("health")));
            Assert.True(casts.All.Single().Targets >= 2);
        }
    }

    // Damage from a spell goes through the combat pipeline, not straight into an effect (16 §3.2), so
    // the damage type's resistance applies. Without that, fire_resist would mean nothing against fire
    // while armour meant everything against swords.
    [Xunit.Fact]
    public void SpellDamageIsResistedLikeAnyOtherDamage()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "flame_bolt");
            var bare = Caster(world, new Vector3(0, 0.1f, -4), "bare");
            var damage = new EventProbe<Damaged>(world);

            world.Cast(caster, Id("flame_bolt"));
            Tick(world, 2);

            var hit = damage.All.Single();
            Assert.Equal(bare, hit.Hit.Target);
            Assert.Equal(caster, hit.Hit.Attacker);          // a kill by spell names its killer
            Assert.Equal(Id("fire"), hit.Hit.Type);
            Assert.Equal(40f, hit.Applied);                  // no fire_resist on this one
        }
    }

    // Cues are presentation only: the simulation says a thing happened and never what it looks like.
    [Xunit.Fact]
    public void ACastRaisesItsCues()
    {
        var (engine, world) = NewWorld();
        using (engine)
        {
            var caster = Caster(world, new Vector3(0, 0.1f, 0), "caster", "fireball");
            var cues = new EventProbe<CueTriggered>(world);

            world.Cast(caster, Id("fireball"));
            Tick(world, 2);

            Assert.Equal(Id("boom"), cues.All.Single().Cue);
        }
    }
}
