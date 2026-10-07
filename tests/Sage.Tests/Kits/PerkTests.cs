#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Perks and traits as effects with prerequisites (issue #381): the RPG kit's perk record (effects, requires in the
// condition vocabulary, a cost in rpg_conventions perkPoints, a trait flag), the rpg:perks component that keeps
// them, and picking one (Perks.Pick, the `perk` command, the rpg:perks screen's rows).
public class PerkTests
{
    public PerkTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("sage", name);

    private const string Kit = """
    [
      { "type": "attribute", "id": "strength", "start": 40, "min": 0, "max": 100 },
      { "type": "attribute", "id": "luck", "start": 40, "min": 0, "max": 100 },
      { "type": "attribute", "id": "blade", "start": 0, "min": 0, "max": 100 },
      { "type": "attribute", "id": "blade_xp", "start": 0, "min": 0 },
      { "type": "attribute", "id": "level", "start": 1, "min": 1, "max": 50 },
      { "type": "attribute", "id": "level_progress", "start": 0, "min": 0 },
      { "type": "attribute", "id": "perk_points", "start": 0, "min": 0 },
      { "type": "rpg_conventions", "id": "rules", "perkPoints": "perk_points" },
      { "type": "skill", "id": "blade", "rank": "blade", "experience": "blade_xp", "rate": 1, "growth": 0,
        "levelling": "character", "levelPoints": 1 },
      { "type": "levelling", "id": "character", "level": "level", "points": "level_progress", "rate": 2, "effects": ["perk_point"] },
      { "type": "effect", "id": "perk_point", "modifiers": [ { "attribute": "perk_points", "op": "Add", "value": 1 } ] },
      { "type": "effect", "id": "mighty", "duration": "Infinite", "modifiers": [ { "attribute": "strength", "op": "Add", "value": 5 } ] },
      { "type": "effect", "id": "lucky", "modifiers": [ { "attribute": "luck", "op": "Add", "value": 3 } ] },
      { "type": "perk", "id": "mighty", "name": "Mighty", "cost": 1, "effects": ["mighty"],
        "requires": [ { "skill": "blade", "atLeast": 2 }, { "level": "character", "atLeast": 2 } ] },
      { "type": "perk", "id": "lucky", "cost": 2, "effects": ["lucky"], "requires": [ { "has_perk": "mighty" } ] },
      { "type": "perk", "id": "born_lucky", "trait": true, "effects": ["lucky"] }
    ]
    """;

    private static HeadlessApp BootKit(string records = Kit) =>
        HeadlessApp.Gameplay().With(new Sage.UI.UiModule(), new RpgKitModule()).WithEngineContent()
            .File("data/kit.json", records).Boot("perks");

    private static Entity Hero(World world)
    {
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddAttributes(hero);
        world.FlushCommands();
        return hero;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    // A perk is refused until its prerequisites hold (each condition's own reason) and the points are there; a
    // level brings the point, picking spends it and applies the perk's effects (lasting, on the current value),
    // and the perk is kept and said (PerkGranted). A perk asked for by another holds once that one is had, and
    // a trait is never picked with points.
    [Xunit.Fact]
    public void APerkIsPickedAtLevelUpWhenItsPrerequisitesHoldAndAppliesItsEffects()
    {
        using var app = BootKit();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var world = app.World;
        var hero = Hero(world);
        var granted = new EventProbe<PerkGranted>(world);

        Assert.False(Perks.CanPick(world, hero, Id("mighty"), out string why));
        Assert.Equal("your skill is too low", why);
        Skills.Practise(world, hero, Id("blade"), 1f);                      // blade 1: a point toward level 2
        Assert.False(Perks.CanPick(world, hero, Id("mighty"), out why));
        Assert.Equal("your skill is too low", why);
        Skills.Practise(world, hero, Id("blade"), 1f);                      // blade 2: level 2, a perk point
        Assert.Equal(2f, world.Attribute(hero, Id("level")));
        Assert.Equal(1f, Perks.PointsOf(world, hero));

        Assert.False(Perks.CanPick(world, hero, Id("lucky"), out why));
        Assert.Equal("you need another perk first", why);
        Assert.False(Perks.CanPick(world, hero, Id("born_lucky"), out why));
        Assert.Equal("a trait is chosen when the character is made", why);
        Assert.False(Perks.Pick(world, hero, Id("nothing"), out why));
        Assert.Equal("there is no such perk", why);

        Assert.True(Perks.Pick(world, hero, Id("mighty"), out why), why);
        Tick(world);
        Assert.True(Perks.Has(world, hero, Id("mighty")));
        Assert.Equal(0f, Perks.PointsOf(world, hero));
        Assert.Equal(45f, world.Attribute(hero, Id("strength")));
        Assert.Equal(Id("mighty"), Assert.Single(granted.All).Perk);
        Assert.False(Perks.CanPick(world, hero, Id("mighty"), out why));
        Assert.Equal("you have it already", why);

        Assert.False(Perks.CanPick(world, hero, Id("lucky"), out why));    // its prerequisite holds; it costs 2
        Assert.Equal("not enough perk points", why);
    }

    // A trait (or any perk) is given for nothing by the `perks` prefab part at creation and the `grant_perk`
    // action later, effects and all, and only once.
    [Xunit.Fact]
    public void TraitsAreGrantedForNothingByAPrefabOrAnAction()
    {
        using var app = BootKit(Kit.TrimEnd().TrimEnd(']') + """
              ,
              { "type": "prefab", "id": "lucky_one", "parts": { "attributes": {}, "perks": ["born_lucky"] } }
            ]
            """);
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var world = app.World;

        var born = world.Spawn(Id("lucky_one"), Vector3.Zero);
        world.FlushCommands();
        Assert.True(Perks.Has(world, born, Id("born_lucky")));
        Assert.Equal(43f, world.Attribute(born, Id("luck")));

        var hero = Hero(world);
        var actions = JsonSerializer.Deserialize<List<IAction>>("""[ { "grant_perk": "born_lucky" }, { "grant_perk": "born_lucky" } ]""", app.Records.Json)!;
        Conditions.Run(world, hero, actions);
        Assert.True(Perks.Has(world, hero, Id("born_lucky")));
        Assert.Equal(43f, world.Attribute(hero, Id("luck")));               // once
        Assert.Equal(new[] { Id("born_lucky") }, world.Get<GrantedPerks>(hero).Perks!);
        Assert.Equal(0f, Perks.PointsOf(world, hero));                      // for nothing

        var hasIt = JsonSerializer.Deserialize<ICondition>("""{ "has_perk": "born_lucky" }""", app.Records.Json)!;
        Assert.True(Conditions.Evaluate(world, hero, hasIt));
        Assert.False(Conditions.Evaluate(world, Hero(world), hasIt));
    }

    // The rows the `perks` command prints and the rpg:perks screen lists: the points in the title, what it has
    // ticked, what it could pick greyed with the reason, and a trait only once it has it.
    [Xunit.Fact]
    public void ThePerksPanelListsWhatItHasAndWhyTheRestCannotBePicked()
    {
        using var app = BootKit();
        var world = app.World;
        var hero = Hero(world);

        var panel = Perks.Panel(world, hero);
        Assert.Equal("Perks — 0 points", panel.Title);
        Assert.Equal(new[] { "Mighty", "lucky" }, panel.Rows.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(panel.TryFind(Id("mighty"), out var row));
        Assert.False(row.Enabled);
        Assert.Equal("your skill is too low", row.Reason);
        Assert.Equal("1 point", row.Detail);

        Assert.True(Perks.Grant(world, hero, Id("born_lucky")));
        Assert.True(Perks.Grant(world, hero, Id("mighty")));
        Perks.Panel(world, hero, panel);
        Assert.True(panel.TryFind(Id("born_lucky"), out row));
        Assert.True(row.Selected);
        Assert.Equal("trait", row.Detail);
        Assert.True(panel.TryFind(Id("mighty"), out row));
        Assert.True(row.Selected && row.Enabled);
        Assert.True(panel.TryFind(Id("lucky"), out row));
        Assert.Equal("not enough perk points", row.Reason);

        Assert.Contains("can have no perks", Perks.Panel(world, world.Create(Transform.At(Vector3.Zero), "rock")).Problem);
    }

    // A perk that costs less than nothing, or names an empty effect, is a load error; so is a condition naming
    // a perk, skill or levelling there is not.
    [Xunit.Fact]
    public void PerkMistakesAreLoadErrors()
    {
        using var app = BootKit("""
            [
              { "type": "perk", "id": "refund", "cost": -1 },
              { "type": "perk", "id": "hollow", "effects": [""] },
              { "type": "perk", "id": "ghost", "requires": [ { "has_perk": "nobody_has_this" } ] }
            ]
            """);
        Assert.Equal(3, app.Records.ErrorCount);
    }
}
