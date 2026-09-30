#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Making your own spells (docs/design/16 §3.3, TODO F21).
//
// The thing under test is not really the composing — it is that a record made at run time behaves
// like one read from a file, and that what the *save* holds is the player's choices rather than the
// record. So several of these span two "runs of the game" with the content changed in between, which
// is what proves the record is derived and not frozen.
public class SpellmakerTests
{
    public SpellmakerTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "damageType": "physical", "costAttribute": "mana" },
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "attribute", "id": "mana",   "max": 50,  "start": 50, "spendEffect": "spend_mana" },
      { "type": "damage_type", "id": "physical" },
      { "type": "attack", "id": "fists", "damage": 5, "reach": 1.5 },

      { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "mend", "cost": 6, "modifiers": [ { "attribute": "health", "op": "Add", "value": 10 } ] },
      { "type": "effect", "id": "harm", "cost": 9, "modifiers": [ { "attribute": "health", "op": "Add", "value": -10 } ] },

      { "type": "prefab", "id": "hero", "name": "hero",
        "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {}, "melee": { "attack": "fists" } } }
    ]
    """;

    private sealed class Fixture : IDisposable
    {
        public readonly Engine Engine;
        public readonly World World;
        private readonly string _userRoot;
        private readonly bool _keepRoot;

        // Sharing `userRoot` with an earlier fixture is how a test spans two runs of the game: the save
        // outlives the engine that wrote it, which is the only way to test a rebuilt record.
        public Fixture(string? extraRecords = null, string? userRoot = null)
        {
            _userRoot = userRoot ?? TestEnv.NewTempDir();
            _keepRoot = userRoot != null;

            var builder = HeadlessApp.Gameplay().With(new Sage.UI.UiModule(), new RpgKitModule()).File("data/spell_test.json", Records);
            if (extraRecords != null) builder.File("data/extra.json", extraRecords);
            Engine = builder.Build().Engine;

            Engine.Saves.Root = Path.Combine(_userRoot, "saves");

            World = Engine.CreateWorld("main");
            var ground = World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
            World.Add(ground, Collider.Box(new Vector3(200, 1, 200)));
        }

        public Entity Hero()
        {
            var hero = World.Spawn(Id("hero"), Vector3.Zero);
            World.Add(hero, new Persistent { Id = PersistentId.FromName("hero") });
            World.RunFixed(1f / 60f);
            return hero;
        }

        public void Dispose()
        {
            Engine.Dispose();
            if (_keepRoot) return;
            try { Directory.Delete(_userRoot, recursive: true); } catch { /* a leftover temp dir is not a failure */ }
        }
    }

    private static RecordId Id(string name) => new("sage", name);

    private static SpellDraft Heal(string name = "mending", float magnitude = 1f) => new()
    {
        Name = name,
        Targeting = AbilityTargeting.Self,
        Effects = { Id("mend") },
        Magnitude = magnitude,
    };

    // The whole point in one test: a spell nobody wrote a record for is cast by the same system that
    // casts the ones in the content files, and it does what the effects it was built from do.
    [Xunit.Fact]
    public void AComposedSpellCanBeCast()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        Effects.Apply(fx.World, hero, Id("harm"), hero, 3f);   // 30 off, so there is something to mend
        fx.World.RunFixed(1f / 60f);
        Assert.Equal(70f, fx.World.Attribute(hero, Id("health")));

        var made = Spellmaker.Compose(fx.World, Heal(magnitude: 2f));
        Assert.True(made.Ok, made.Problem);
        Assert.Equal(new RecordId("custom", "mending"), made.Id);

        fx.World.Teach(hero, made.Id);
        Assert.True(fx.World.Cast(hero, made.Id));
        for (int i = 0; i < 4; i++) fx.World.RunFixed(1f / 60f);

        // Magnitude 2 on a "+10 health" effect: 20 back, and the spell's cost out of the mana pool.
        Assert.Equal(90f, fx.World.Attribute(hero, Id("health")));
        Assert.True(fx.World.Attribute(hero, Id("mana")) < 50f);
    }

    // A composed spell that flies (issue #138) is thrown on the one carrier attacks fly on too, from the
    // kit's Cast button, and its payload lands on what it struck: the readied spell, the carrier and the
    // payload are the same with the 4e hit pipeline as before it.
    [Xunit.Fact]
    public void AComposedProjectileSpellFromTheCastButtonLandsOnItsTarget()
    {
        using var fx = new Fixture();
        var world = fx.World;
        var mage = HitPipelineTests.Body(world, Vector3.Zero, "mage", new Vector3(0, 0, -8));
        var target = HitPipelineTests.Body(world, new Vector3(0, 0, -8), "target", Vector3.Zero);
        world.RunFixed(1f / 60f);

        var made = Spellmaker.Compose(world, new SpellDraft
        {
            Name = "frost dart", Targeting = AbilityTargeting.Projectile, Effects = { Id("harm") }, Range = 20f, ProjectileSpeed = 30f,
        });
        Assert.True(made.Ok, made.Problem);
        world.Teach(mage, made.Id);
        Assert.Equal(made.Id, world.Readied(mage));

        var cast = fx.Engine.Actions.Get(RpgConventions.Of(world).CastAction);
        world.Get<PawnIntent>(mage).Pressed = default(ActionMask).With(cast);
        world.RunFixed(1f / 60f);
        world.Get<PawnIntent>(mage).Pressed = default;
        world.RunFixed(1f / 60f);
        Assert.Single(world.Query<Transform, Projectile>().Entities.ToEntityList());   // in flight, not arrived

        for (int i = 0; i < 40; i++) world.RunFixed(1f / 60f);
        Assert.Equal(90f, world.Attribute(target, Id("health")));
        Assert.Equal(100f, world.Attribute(mage, Id("health")));
        Assert.Empty(world.Query<Transform, Projectile>().Entities.ToEntityList());
    }

    // A price the player can see before committing, and one that responds to what they asked for.
    [Xunit.Fact]
    public void AskingForMoreCostsMore()
    {
        using var fx = new Fixture();
        var records = fx.Engine.Records;

        float plain = Spellmaker.Price(records, Heal());
        float stronger = Spellmaker.Price(records, Heal(magnitude: 3f));
        float thrown = Spellmaker.Price(records, new SpellDraft
        {
            Name = "flung mending", Targeting = AbilityTargeting.Area, Effects = { Id("mend") }, Radius = 5f,
        });

        Assert.True(plain > 0f);
        Assert.True(stronger > plain, $"{stronger} should beat {plain}");
        Assert.True(thrown > plain, $"a burst at range ({thrown}) should beat touching yourself ({plain})");
    }

    // Refusals, each with something a spellmaker screen can say. The `spend_mana` case is the one that
    // matters: it is a real effect the engine applies constantly, and it is priced at zero because it
    // is not a thing to build with — without the check, "a spell that drains your mana" is composable.
    [Xunit.Theory]
    [Xunit.InlineData("", "mend", "name")]
    [Xunit.InlineData("empty", null, "effect")]
    [Xunit.InlineData("thief", "spend_mana", "not an effect you can put in a spell")]
    [Xunit.InlineData("nonsense", "no_such_effect", "no effect")]
    public void WhatCannotBeComposed(string name, string? effect, string expected)
    {
        using var fx = new Fixture();
        var draft = new SpellDraft { Name = name, Targeting = AbilityTargeting.Self };
        if (effect != null) draft.Effects.Add(Id(effect));

        var made = Spellmaker.Compose(fx.World, draft);
        Assert.False(made.Ok);
        Assert.Contains(expected, made.Problem, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Spellmaker.Book(fx.World).Drafts);
    }

    [Xunit.Fact]
    public void TwoSpellsCannotShareAName()
    {
        using var fx = new Fixture();
        Assert.True(Spellmaker.Compose(fx.World, Heal("Warm Hands")).Ok);

        var again = Spellmaker.Compose(fx.World, Heal("warm hands"));   // the same id, differently typed
        Assert.False(again.Ok);
        Assert.Contains("already", again.Problem);
        Assert.Single(Spellmaker.Book(fx.World).Drafts);
    }

    // A name is a name and an id is an id (05 §3.5). What a screen shows is the name; what the save and
    // the record store see is the slug.
    [Xunit.Fact]
    public void ANameBecomesAUsableId()
    {
        using var fx = new Fixture();
        var made = Spellmaker.Compose(fx.World, Heal("Bob's Big Mend!"));
        Assert.True(made.Ok, made.Problem);
        Assert.Equal(new RecordId("custom", "bob_s_big_mend"), made.Id);
        Assert.True(fx.Engine.Records.TryGet(made.Id, out AbilityRecord record));
        Assert.Equal("Bob's Big Mend!", record.Name);
    }

    // A composed spell did not come from disk, so a content reload has nothing to rebuild it from: it
    // has to survive one, or a designer saving a JSON file would delete the player's magic.
    [Xunit.Fact]
    public void AComposedSpellSurvivesAContentReload()
    {
        using var fx = new Fixture();
        var made = Spellmaker.Compose(fx.World, Heal());
        Assert.True(made.Ok, made.Problem);

        fx.Engine.Records.Reload();

        Assert.True(fx.Engine.Records.TryGet(made.Id, out AbilityRecord _));
    }

    [Xunit.Fact]
    public void ForgettingASpellTakesItsRecordWithIt()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var made = Spellmaker.Compose(fx.World, Heal());
        fx.World.Teach(hero, made.Id);

        Assert.True(Spellmaker.Forget(fx.World, "MENDING"));
        Assert.False(fx.Engine.Records.TryGet(made.Id, out AbilityRecord _));
        Assert.Empty(Spellmaker.Book(fx.World).Drafts);
        Assert.False(Spellmaker.Forget(fx.World, "mending"));   // and it stays gone

        // The player still has the id written down, and casting it is refused the way any ability whose
        // record went missing is — a mod being removed does the same thing.
        Assert.True(fx.World.Knows(hero, made.Id));
        var refusals = new EventProbe<CastRefused>(fx.World);
        fx.World.Cast(hero, made.Id);
        fx.World.RunFixed(1f / 60f);
        Assert.Contains(refusals.All, r => r.Ability == made.Id);
    }

    // Two runs of the game: the save holds the draft, and loading composes the record again. Nothing
    // wrote an `AbilityRecord` into the file.
    [Xunit.Fact]
    public void AComposedSpellComesBackAfterALoad()
    {
        string shared = TestEnv.NewTempDir();
        RecordId id;

        using (var fx = new Fixture(userRoot: shared))
        {
            var hero = fx.Hero();
            var made = Spellmaker.Compose(fx.World, Heal(magnitude: 2f));
            Assert.True(made.Ok, made.Problem);
            id = made.Id;
            fx.World.Teach(hero, id);
            Assert.True(fx.Engine.Saves.Save("slot"));

            // No frozen copy of the record in the file: only the choices behind it (09 §3.1).
            string json = File.ReadAllText(Path.Combine(fx.Engine.Saves.Root, "slot", "world_main.json"));
            Assert.Contains("spellbook", json);
            Assert.Contains("mending", json);
            Assert.DoesNotContain("costAttribute", json, StringComparison.OrdinalIgnoreCase);
        }

        using (var fx = new Fixture(userRoot: shared))
        {
            fx.Hero();
            // Nothing knows the spell yet: this engine never composed it.
            Assert.False(fx.Engine.Records.TryGet(id, out AbilityRecord _));

            Assert.True(fx.Engine.Saves.Load("slot"));
            Assert.True(fx.Engine.Records.TryGet(id, out AbilityRecord record));
            Assert.Equal(2f, record.Magnitude);

            var loaded = fx.World.Resolve(PersistentId.FromName("hero"));
            Assert.True(fx.World.Knows(loaded, id));

            // And it works, which a record that merely exists would not prove.
            Effects.Apply(fx.World, loaded, Id("harm"), loaded, 4f);
            fx.World.RunFixed(1f / 60f);
            fx.World.Cast(loaded, id);
            for (int i = 0; i < 4; i++) fx.World.RunFixed(1f / 60f);
            Assert.Equal(80f, fx.World.Attribute(loaded, Id("health")));
        }

        try { Directory.Delete(shared, recursive: true); } catch { /* best effort */ }
    }

    // Why the *draft* is what is saved: the effects a spell is made of are content, and content gets
    // rebalanced. The old spell follows the new prices instead of carrying last year's.
    [Xunit.Fact]
    public void ARebalancedEffectChangesAnOldSpell()
    {
        string shared = TestEnv.NewTempDir();
        float before;

        using (var fx = new Fixture(userRoot: shared))
        {
            var made = Spellmaker.Compose(fx.World, Heal());
            before = made.Cost;
            Assert.True(before > 0f);
            Assert.True(fx.Engine.Saves.Save("slot"));
        }

        // A second run where mending costs three times as much, and heals three times as much with it.
        using (var fx = new Fixture("""
            [ { "type": "effect", "id": "mend", "patch": true, "cost": 18,
                "modifiers": [ { "attribute": "health", "op": "Add", "value": 30 } ] } ]
            """, shared))
        {
            Assert.True(fx.Engine.Saves.Load("slot"));
            Assert.True(fx.Engine.Records.TryGet(new RecordId("custom", "mending"), out AbilityRecord record));
            Assert.True(record.Cost > before, $"{record.Cost} should beat the old {before}");
        }

        try { Directory.Delete(shared, recursive: true); } catch { /* best effort */ }
    }

    // Loading is a different game. A spell composed since the save must not survive it, and one the
    // save never heard of must not linger in the record store where the next `cast` could find it.
    [Xunit.Fact]
    public void LoadingReplacesTheBookRatherThanAddingToIt()
    {
        using var fx = new Fixture();
        var kept = Spellmaker.Compose(fx.World, Heal("kept"));
        Assert.True(kept.Ok, kept.Problem);
        Assert.True(fx.Engine.Saves.Save("slot"));

        var later = Spellmaker.Compose(fx.World, Heal("later"));
        Assert.True(later.Ok, later.Problem);
        Assert.Equal(2, Spellmaker.Book(fx.World).Drafts.Count);

        Assert.True(fx.Engine.Saves.Load("slot"));

        Assert.Single(Spellmaker.Book(fx.World).Drafts);
        Assert.True(fx.Engine.Records.TryGet(kept.Id, out AbilityRecord _));
        Assert.False(fx.Engine.Records.TryGet(later.Id, out AbilityRecord _));
        Assert.DoesNotContain(fx.Engine.Records.RuntimeRecords, r => r.Id == later.Id);
    }
}
