#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// What a screen would show (docs/design/13 §3, TODO F38, R17).
//
// This is the whole argument for the panel model: every one of these would otherwise be a screenshot
// to squint at. "The spellbook greys out the spell you cannot afford **and says why**" is a sentence a
// test can hold the engine to, and it is the sentence a player notices.
public class PanelTests
{
    public PanelTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "attribute", "id": "mana",   "start": 20,  "min": 0, "max": 20, "spendEffect": "spend_mana" },
      { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "mend", "cost": 6, "modifiers": [ { "attribute": "health", "op": "Add", "value": 10 } ] },
      { "type": "effect", "id": "house_only", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      // What a composed spell costs is the game's word, not the kit's (issue #27): mana, here too.
      { "type": "gameplay_conventions", "id": "default_conventions", "costAttribute": "mana" },

      { "type": "damage_type", "id": "physical" },
      { "type": "attack", "id": "swing", "damage": 6, "reach": 1.8 },
      { "type": "item", "id": "sword", "label": "a practice sword", "weight": 3, "slot": "MainHand", "attack": "swing" },
      { "type": "item", "id": "bread", "label": "bread", "weight": 0.5, "maxStack": 10 },

      { "type": "ability", "id": "spark",  "name": "spark",  "costAttribute": "mana", "cost": 4,  "targeting": "Touch", "range": 8, "damage": 5, "damageType": "physical" },
      { "type": "ability", "id": "pillar", "name": "pillar", "costAttribute": "mana", "cost": 50, "targeting": "Touch", "range": 8, "damage": 40, "damageType": "physical" },

      { "type": "prefab", "id": "hero", "name": "hero",
        "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {}, "inventory": { "capacity": 40 },
                   "melee": { "attack": "swing" }, "abilities": ["spark", "pillar"] } }
    ]
    """;

    private sealed class Fixture : IDisposable
    {
        public readonly Engine Engine;
        public readonly World World;

        public Fixture()
        {
            Engine = HeadlessApp.Gameplay().With(new Sage.UI.UiModule(), new RpgKitModule()).File("data/panel_test.json", Records).Build().Engine;

            World = Engine.CreateWorld("panels");
            var ground = World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
            World.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
        }

        public Entity Hero() => World.Spawn(Id("hero"), Vector3.Zero);

        public void Dispose() => Engine.Dispose();
    }

    private static RecordId Id(string name) => new("sage", name);

    // The headline case. Twenty mana, a spell that costs four and a spell that costs fifty: one row is
    // usable and one is not, and the unusable one carries the reason a screen puts in its tooltip.
    [Xunit.Fact]
    public void TheSpellbookSaysWhatYouCanCastAndWhyNot()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();

        var panel = GameplayPanels.Spellbook(fx.World, hero);

        Assert.Equal(2, panel.Count);
        Assert.True(panel.TryFind(Id("spark"), out var spark));
        Assert.True(panel.TryFind(Id("pillar"), out var pillar));

        Assert.Equal("spark", spark.Name);
        Assert.Equal("4 mana", spark.Detail);
        Assert.True(spark.Enabled);
        Assert.Equal("", spark.Reason);

        Assert.False(pillar.Enabled);
        Assert.Equal(AbilityRules.Explain(CastRefusal.TooExpensive), pillar.Reason);
        Assert.Contains("not enough", pillar.Reason);
    }

    // The tick a screen draws beside the readied spell, and the engine call that moves it.
    [Xunit.Fact]
    public void TheReadiedSpellIsMarkedAndCanBeChanged()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var panel = new Panel();

        GameplayPanels.Spellbook(fx.World, hero, panel);
        Assert.True(panel.TryFind(Id("spark"), out var first));
        Assert.True(first.Selected, "the first spell learned is readied (16 §3.3)");

        Assert.True(fx.World.Ready(hero, Id("pillar")));
        GameplayPanels.Spellbook(fx.World, hero, panel);

        Assert.True(panel.TryFind(Id("pillar"), out var readied));
        Assert.True(readied.Selected);
        Assert.True(panel.TryFind(Id("spark"), out var idle));
        Assert.False(idle.Selected);

        // And a spell it does not know cannot be readied, so a stale screen cannot ready nothing.
        Assert.False(fx.World.Ready(hero, Id("mend")));
        Assert.Equal(Id("pillar"), fx.World.Readied(hero));
    }

    // A cooldown is a refusal like any other, and it comes back on its own — which is the difference
    // the AI also cares about (16 §3.4).
    [Xunit.Fact]
    public void ARowBecomesUsableAgainWhenTheSimulationSaysSo()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var panel = new Panel();

        Effects.Apply(fx.World, hero, Id("spend_mana"), hero, 18f);   // 20 → 2: even spark is too dear
        fx.World.RunFixed(1f / 60f);

        GameplayPanels.Spellbook(fx.World, hero, panel);
        Assert.True(panel.TryFind(Id("spark"), out var poor));
        Assert.False(poor.Enabled);

        fx.World.Get<Attributes>(hero).Values.SetBase(
            fx.World.Resources.Get<GameplayRegistries>().Attribute(Id("mana")), 20f);
        fx.World.RunFixed(1f / 60f);

        int before = panel.Version;
        GameplayPanels.Spellbook(fx.World, hero, panel);
        Assert.True(panel.Version > before, "a rebuild bumps the version, so a screen knows to re-lay out");
        Assert.True(panel.TryFind(Id("spark"), out var rich));
        Assert.True(rich.Enabled);
    }

    // The bag: stacks with their weight, the carried total in the title, a tick on what is in hand,
    // and a reason on what cannot go there.
    [Xunit.Fact]
    public void TheInventoryPanelShowsStacksWeightAndWhatIsHeld()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        fx.World.Give(hero, Id("sword"));
        fx.World.Give(hero, Id("bread"), 4);
        fx.World.Equip(hero, Id("sword"));

        var panel = GameplayPanels.Inventory(fx.World, hero);

        Assert.Contains("kg", panel.Title);
        Assert.Contains("40", panel.Title);            // the capacity a screen draws a bar against

        Assert.True(panel.TryFind(Id("sword"), out var sword));
        Assert.Equal("a practice sword", sword.Name);  // the record's label, not the id
        Assert.True(sword.Selected, "it is in the hero's hand");
        Assert.Equal(1, sword.Count);

        Assert.True(panel.TryFind(Id("bread"), out var bread));
        Assert.Equal(4, bread.Count);
        Assert.Equal("2 kg", bread.Detail);            // 0.5 kg each, four of them
        Assert.False(bread.Enabled);
        Assert.Contains("wear or wield", bread.Reason);
    }

    // `CanEquip` is what the row's `Enabled` came from, and `Equip` uses the same gates (R17). Asking
    // and doing must agree, so the test asks both about the same three cases.
    [Xunit.Theory]
    [Xunit.InlineData("sword", true, "")]
    [Xunit.InlineData("bread", false, "wear or wield")]
    [Xunit.InlineData("nonesuch", false, "no such thing")]
    public void AskingWhetherSomethingCanBeEquippedMatchesEquipping(string item, bool expected, string reason)
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        fx.World.Give(hero, Id("sword"));
        fx.World.Give(hero, Id("bread"));

        var id = Id(item);
        bool can = fx.World.CanEquip(hero, id, out string why);

        Assert.Equal(expected, can);
        Assert.Contains(reason, why);
        Assert.Equal(expected, fx.World.Equip(hero, id));
    }

    // Something you are not carrying cannot be equipped, which is the case a screen showing a shop's
    // stock has to get right.
    [Xunit.Fact]
    public void WhatYouDoNotCarryCannotBeEquipped()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();

        Assert.False(fx.World.CanEquip(hero, Id("sword"), out string why));
        Assert.Contains("not carrying", why);
    }

    // The spellmaker's own panels: what can be built with, and what has been (F21).
    [Xunit.Fact]
    public void TheSpellmakerPanelsListWhatIsForSaleAndWhatWasMade()
    {
        using var fx = new Fixture();

        var effects = GameplayPanels.SpellEffects(fx.World);
        Assert.Contains(effects.Rows, r => r.Id == Id("mend"));
        Assert.DoesNotContain(effects.Rows, r => r.Id == Id("spend_mana"));   // cost 0: not for sale
        Assert.DoesNotContain(effects.Rows, r => r.Id == Id("house_only"));
        Assert.True(effects.TryFind(Id("mend"), out var mend));
        Assert.Contains("health +10", mend.Detail);

        var made = Spellmaker.Compose(fx.World, new SpellDraft
        {
            Name = "Warm Hands", Targeting = AbilityTargeting.Self, Effects = { Id("mend") },
        });
        Assert.True(made.Ok, made.Problem);

        var composed = GameplayPanels.ComposedSpells(fx.World);
        Assert.True(composed.TryFind(made.Id, out var row));
        Assert.Equal("Warm Hands", row.Name);
        Assert.Contains("mana", row.Detail);
        Assert.True(row.Enabled);
    }

    // Something that has no spellbook or no bag at all is not the same as an empty one: a screen shows
    // the first as a mistake and the second as "you are carrying nothing".
    [Xunit.Fact]
    public void APanelForSomethingThatCannotHaveOneSaysSo()
    {
        using var fx = new Fixture();
        var rock = fx.World.Create(Transform.At(Vector3.One), "rock");

        var spells = GameplayPanels.Spellbook(fx.World, rock);
        Assert.Equal(0, spells.Count);
        Assert.NotEqual("", spells.Problem);

        var bag = GameplayPanels.Inventory(fx.World, rock);
        Assert.Equal(0, bag.Count);
        Assert.Contains("no inventory", bag.Problem);
    }

    // A panel is retained and rebuilt, so an open screen does not allocate a list a frame. Rebuilding
    // must therefore leave no trace of the last contents.
    [Xunit.Fact]
    public void RebuildingReusesThePanelWithoutKeepingTheOldRows()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        fx.World.Give(hero, Id("bread"), 2);

        var panel = new Panel();
        GameplayPanels.Inventory(fx.World, hero, panel);
        Assert.Equal(1, panel.Count);

        fx.World.Take(hero, Id("bread"), 2);
        GameplayPanels.Inventory(fx.World, hero, panel);

        Assert.Equal(0, panel.Count);
        Assert.False(panel.TryFind(Id("bread"), out _));
        Assert.Equal("", panel.Problem);   // it has an inventory; it is simply empty
    }

    // What a screen highlights when it opens: the readied spell, or the first thing that would work.
    [Xunit.Fact]
    public void ThePanelKnowsWhichRowAScreenWouldStartOn()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();

        var spells = GameplayPanels.Spellbook(fx.World, hero);
        Assert.True(spells.TryCurrent(out var current));
        Assert.Equal(Id("spark"), current.Id);          // readied

        fx.World.Give(hero, Id("bread"), 1);
        fx.World.Give(hero, Id("sword"));
        var bag = GameplayPanels.Inventory(fx.World, hero);
        Assert.True(bag.TryCurrent(out var first));
        Assert.Equal(Id("sword"), first.Id);            // nothing equipped yet: the first usable row
    }
}
