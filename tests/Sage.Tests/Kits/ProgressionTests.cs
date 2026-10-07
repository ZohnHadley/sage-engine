#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Skills and levelling as data (issue #377): the base's attribute_gain hook (an event the engine raises adds to
// an attribute of whoever did it, with filters) and the RPG kit's skill and levelling records over it (use-XP
// becomes rank at a threshold, rises become levels, a level raises the governing attributes).
public class ProgressionTests
{
    public ProgressionTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("sage", name);

    private const string Hooks = """
    [
      { "type": "attribute", "id": "armour_xp", "start": 0, "min": 0 },
      { "type": "attribute", "id": "fire_xp", "start": 0, "min": 0 },
      { "type": "attribute", "id": "theft", "start": 0, "min": 0, "max": 3 },
      { "type": "attribute", "id": "xp", "start": 0, "min": 0 },
      { "type": "attribute", "id": "brawl", "start": 0, "min": 0 },
      { "type": "ability", "id": "fireball" },
      { "type": "ability", "id": "heal" },
      { "type": "item", "id": "gem" },
      { "type": "item", "id": "rock" },
      { "type": "attribute_gain", "id": "armour", "on": "Hurt", "attribute": "armour_xp", "amount": 0.5, "perDamage": 0.1 },
      { "type": "attribute_gain", "id": "fire_school", "on": "Cast", "abilities": ["fireball"], "attribute": "fire_xp", "amount": 2 },
      { "type": "attribute_gain", "id": "pickpocket", "on": "Use", "items": ["gem"], "attribute": "theft", "amount": 1 },
      { "type": "attribute_gain", "id": "kills", "on": "Kill", "attribute": "xp", "amount": 25 },
      { "type": "attribute_gain", "id": "fire_fighter", "on": "Hit", "damageTypes": ["fire"], "attribute": "brawl", "amount": 1 }
    ]
    """;

    private static Entity Body(World world, string name)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), name);
        world.AddAttributes(entity);
        world.FlushCommands();
        return entity;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static void Hit(World world, Entity attacker, Entity target, RecordId type, float applied) =>
        world.Events.Send(new Damaged(new DamageInfo(attacker, target, type, applied, Vector3.Zero, Vector3.UnitZ), applied));

    // The base alone (no kit): each event the hook listens for adds to the right entity's attribute, only when
    // its filters hold, clamped to the attribute's range, and says so with AttributeGained.
    [Xunit.Fact]
    public void AnAttributeGainAddsOnItsEventToWhoeverDidItWhenItsFiltersHold()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent().File("data/hooks.json", Hooks).Boot("prog");
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var world = app.World;
        var hero = Body(world, "hero");
        var foe = Body(world, "foe");
        var gained = new EventProbe<AttributeGained>(world);

        // Hurt: the target gains, amount plus per point of damage; a blow that did nothing is no practice.
        Hit(world, hero, foe, new RecordId("sage", "physical"), 10f);
        Hit(world, hero, foe, new RecordId("sage", "physical"), 0f);
        Tick(world);
        Assert.Equal(1.5f, world.Attribute(foe, Id("armour_xp")), 4);
        Assert.Equal(0f, world.Attribute(hero, Id("armour_xp")));
        // Hit with a damage type filter: physical is not fire.
        Assert.Equal(0f, world.Attribute(hero, Id("brawl")));
        Hit(world, hero, foe, Id("fire"), 3f);
        Tick(world);
        Assert.Equal(1f, world.Attribute(hero, Id("brawl")));

        // Cast: only the abilities it names.
        world.Events.Send(new AbilityCast(hero, Id("heal"), Vector3.Zero, 1));
        world.Events.Send(new AbilityCast(hero, Id("fireball"), Vector3.Zero, 1));
        Tick(world);
        Assert.Equal(2f, world.Attribute(hero, Id("fire_xp")));

        // Use: only the items it names, and clamped at the attribute's maximum of 3.
        world.Events.Send(new Used(hero, foe) { Item = Id("rock") });
        for (int i = 0; i < 5; i++) world.Events.Send(new Used(hero, foe) { Item = Id("gem") });
        Tick(world);
        Assert.Equal(3f, world.Attribute(hero, Id("theft")));

        // Kill: the killer gains; a death nobody caused gives nobody anything.
        world.Events.Send(new Died(foe, hero));
        world.Events.Send(new Died(hero, default));
        Tick(world);
        Assert.Equal(25f, world.Attribute(hero, Id("xp")));

        Tick(world);
        var theft = gained.All.Where(g => g.Attribute == Id("theft")).ToList();
        Assert.Equal(3, theft.Count);                                      // the two past the cap changed nothing
        Assert.All(theft, g => Assert.Equal(Id("pickpocket"), g.Rule));
        Assert.Contains(gained.All, g => g.Entity == foe && g.Attribute == Id("armour_xp") && g.Amount == 1.5f);
    }

    // A filter that cannot hold on the event it is on is a load error, as is a gain that adds to nothing.
    [Xunit.Fact]
    public void AnAttributeGainThatCannotMatchIsALoadError()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent().File("data/bad.json", """
            [
              { "type": "ability", "id": "fireball" },
              { "type": "item", "id": "gem" },
              { "type": "attribute_gain", "id": "nothing", "on": "Hit" },
              { "type": "attribute_gain", "id": "hit_spell", "on": "Hit", "abilities": ["fireball"], "attribute": "sage:health" },
              { "type": "attribute_gain", "id": "cast_item", "on": "Cast", "items": ["gem"], "attribute": "sage:health" },
              { "type": "attribute_gain", "id": "use_damage", "on": "Use", "perDamage": 1, "attribute": "sage:health" }
            ]
            """).Boot("prog");
        Assert.Equal(4, app.Records.ErrorCount);
    }

    private const string Kit = """
    [
      { "type": "attribute", "id": "strength", "start": 40, "min": 0, "max": 100 },
      { "type": "attribute", "id": "agility", "start": 40, "min": 0, "max": 100 },
      { "type": "attribute", "id": "blade", "start": 0, "min": 0, "max": 3 },
      { "type": "attribute", "id": "blade_xp", "start": 0, "min": 0 },
      { "type": "attribute", "id": "sneak", "start": 0, "min": 0, "max": 100 },
      { "type": "attribute", "id": "sneak_xp", "start": 0, "min": 0 },
      { "type": "attribute", "id": "level", "start": 1, "min": 1, "max": 3 },
      { "type": "attribute", "id": "level_progress", "start": 0, "min": 0 },
      { "type": "attribute", "id": "xp", "start": 0, "min": 0 },
      { "type": "attribute", "id": "xp_level", "start": 1, "min": 1, "max": 100 },
      { "type": "item", "id": "sword", "slot": "MainHand" },
      { "type": "attribute_gain", "id": "blade_use", "on": "Hit", "wielding": ["sword"], "attribute": "blade_xp", "amount": 1 },
      { "type": "attribute_gain", "id": "kill_xp", "on": "Kill", "attribute": "xp", "amount": 60 },
      { "type": "skill", "id": "blade", "rank": "blade", "experience": "blade_xp", "governing": "strength",
        "rate": 1, "growth": 1, "levelling": "character", "levelPoints": 1 },
      { "type": "skill", "id": "sneak", "rank": "sneak", "experience": "sneak_xp", "governing": "agility",
        "rate": 1, "growth": 0, "levelling": "character", "levelPoints": 0.5 },
      { "type": "levelling", "id": "character", "level": "level", "points": "level_progress", "rate": 2,
        "attributeBonus": [ { "rises": 1, "gain": 1 }, { "rises": 3, "gain": 5 } ] },
      { "type": "levelling", "id": "experience", "level": "xp_level", "points": "xp", "rate": 100, "growth": 1 }
    ]
    """;

    private static HeadlessApp BootKit(string records = Kit)
    {
        var app = HeadlessApp.Gameplay().With(new Sage.UI.UiModule(), new RpgKitModule()).WithEngineContent()
            .File("data/kit.json", records).Boot("prog");
        return app;
    }

    private static Entity Hero(World world)
    {
        var hero = Body(world, "hero");
        world.AddInventory(hero);
        world.Add(hero, new Melee());
        world.FlushCommands();
        return hero;
    }

    // Morrowind's "use a skill, it rises": a cut with the sword in hand is use-XP; at rate × (1 + growth × rank)
    // the rank rises and the use-XP keeps what was over; without the sword nothing; at the rank's maximum the
    // use-XP waits. Each rise is a SkillRaised.
    [Xunit.Fact]
    public void ASkillRisesWithUseAtItsThresholdAndStopsAtItsMaximum()
    {
        using var app = BootKit();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var world = app.World;
        var hero = Hero(world);
        var dummy = Body(world, "dummy");
        var raised = new EventProbe<SkillRaised>(world);
        var blade = Id("blade");

        Hit(world, hero, dummy, new RecordId("sage", "physical"), 1f);    // bare-handed: no practice
        Tick(world);
        Assert.Equal(0f, Skills.ExperienceOf(world, hero, blade));

        Assert.True(world.Give(hero, Id("sword")));
        Assert.True(world.Equip(hero, Id("sword")));
        // From 0 it takes 1, from 1 it takes 2, from 2 it takes 3; its maximum is 3.
        float[] rankAfter = { 1, 1, 2, 2, 2, 3, 3, 3, 3, 3 };
        for (int i = 0; i < rankAfter.Length; i++)
        {
            Hit(world, hero, dummy, new RecordId("sage", "physical"), 1f);
            Tick(world);
            Assert.Equal(rankAfter[i], Skills.RankOf(world, hero, blade));
        }
        Assert.Equal(4f, Skills.ExperienceOf(world, hero, blade));          // mastered: the use-XP waits
        Tick(world);
        Assert.Equal(new[] { 1f, 2f, 3f }, raised.All.Where(r => r.Skill == blade).Select(r => r.Rank));
    }

    // Daggerfall's level by skill use, with Morrowind's attribute bonus: rises are points toward a level (a
    // minor skill's half a point); at the level each governing attribute gains by its skills' rises since the
    // last (one +1, three or more +5), and the count starts again. Several rises at once level as far as they go.
    [Xunit.Fact]
    public void SkillRisesLevelTheCharacterAndRaiseTheGoverningAttributes()
    {
        using var app = BootKit();
        var world = app.World;
        var hero = Hero(world);
        var levelled = new EventProbe<LevelledUp>(world);

        Skills.Practise(world, hero, Id("sneak"), 1f);                       // a rise: half a point
        Assert.Equal(1f, Skills.RankOf(world, hero, Id("sneak")));
        Assert.Equal(1f, world.Attribute(hero, Id("level")));
        Skills.Practise(world, hero, Id("sneak"), 2f);                       // two more: 1.5 points
        Assert.Equal(3f, Skills.RankOf(world, hero, Id("sneak")));
        Skills.Practise(world, hero, Id("blade"), 1f);                       // blade 1: 2.5 points, a level
        Assert.Equal(2f, world.Attribute(hero, Id("level")));
        Assert.Equal(0.5f, world.Attribute(hero, Id("level_progress")));
        Assert.Equal(45f, world.Attribute(hero, Id("agility")));             // three sneak rises: +5
        Assert.Equal(41f, world.Attribute(hero, Id("strength")));            // one blade rise: +1
        Assert.Empty(world.Get<Progression>(hero).Rises!);

        // Blade 1 → 3 at once (2 + 3 use-XP): two points; the level is at its maximum after one more.
        Skills.Practise(world, hero, Id("blade"), 5f);
        Assert.Equal(3f, Skills.RankOf(world, hero, Id("blade")));
        Assert.Equal(3f, world.Attribute(hero, Id("level")));
        Assert.Equal(42f, world.Attribute(hero, Id("strength")));            // two rises since the last level: +1
        Tick(world);
        Assert.Equal(new[] { 2f, 3f }, levelled.All.Select(l => l.Level));
    }

    // Experience by kills, with no skills at all: an attribute_gain on Kill into a levelling's points, which asks
    // rate × (1 + growth × (level − 1)) — 100, then 200.
    [Xunit.Fact]
    public void ExperienceFromKillsLevelsAtGrowingThresholds()
    {
        using var app = BootKit();
        var world = app.World;
        var hero = Hero(world);
        float[] levelAfter = { 1, 2, 2, 2, 3 };
        for (int i = 0; i < levelAfter.Length; i++)
        {
            world.Events.Send(new Died(Body(world, "wolf"), hero));
            Tick(world);
            Assert.Equal(levelAfter[i], world.Attribute(hero, Id("xp_level")));
        }
        Assert.Equal(0f, world.Attribute(hero, Id("xp")));                  // 300 = 100 + 200
    }

    // A skill with no rank or use-XP, or one that rises for nothing, and a levelling with no level, are load errors.
    [Xunit.Fact]
    public void SkillAndLevellingMistakesAreLoadErrors()
    {
        using var app = BootKit("""
            [
              { "type": "attribute", "id": "a", "start": 0 },
              { "type": "skill", "id": "no_rank", "experience": "a" },
              { "type": "skill", "id": "same", "rank": "a", "experience": "a" },
              { "type": "skill", "id": "free", "rank": "a", "experience": "sage:health", "rate": 0 },
              { "type": "levelling", "id": "no_level", "points": "a" },
              { "type": "levelling", "id": "bad_step", "level": "a", "points": "sage:health", "attributeBonus": [ { "rises": 0, "gain": 1 } ] }
            ]
            """);
        Assert.Equal(5, app.Records.ErrorCount);
    }
}
