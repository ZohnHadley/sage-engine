#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Screens, without a screen (docs/design/13 §3, TODO F38).
//
// The point of `Screen`/`ScreenStack` living in the engine rather than the client: a test can open a
// spellbook, move down twice, press Enter and assert what happened to the world. None of this needs a
// window, a font or a graphics device, because none of it is drawing — the drawing is `PanelView`, and
// what it draws is a screenshot's business.
public class ScreenTests
{
    public ScreenTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "attribute", "id": "mana",   "start": 40,  "min": 0, "max": 40, "spendEffect": "spend_mana" },
      { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
      { "type": "damage_type", "id": "physical" },
      { "type": "attack", "id": "swing", "damage": 6, "reach": 1.8 },

      { "type": "item", "id": "sword",  "label": "a sword", "weight": 3, "slot": "MainHand", "attack": "swing" },
      { "type": "item", "id": "shield", "label": "a shield", "weight": 4, "slot": "OffHand" },
      { "type": "item", "id": "bread",  "label": "bread", "weight": 0.5, "maxStack": 10 },

      { "type": "ability", "id": "spark",  "name": "spark",  "costAttribute": "mana", "cost": 4,  "targeting": "Touch", "range": 8, "damage": 5, "damageType": "physical" },
      { "type": "ability", "id": "pillar", "name": "pillar", "costAttribute": "mana", "cost": 90, "targeting": "Touch", "range": 8, "damage": 40, "damageType": "physical" },

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
        public readonly ScreenStack Screens = new();

        public Fixture()
        {
            // The test's own action, registered with everything else's: once content has loaded, the
            // bindings are built and a late action would have none.
            Engine = HeadlessApp.Gameplay().With(new RpgKitModule())
                .OnRegistered(app => app.Engine.Actions.Register("TestSpellbook", ActionKind.Button))
                .File("data/screen_test.json", Records)
                .Build().Engine;

            World = Engine.CreateWorld("screens");
            World.Resources.Add(Screens);
            var ground = World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
            World.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
        }

        public Entity Hero() => World.Spawn(Id("hero"), Vector3.Zero);

        public void Dispose() => Engine.Dispose();
    }

    private static RecordId Id(string name) => new("sage", name);

    // The two screens the Sandbox has, written here as the engine sees them: a panel, and what Enter
    // does. (The game's own copies live in games/Sandbox.Client/Screens.cs.)
    private sealed class SpellbookScreen : Screen
    {
        public override void Build(World world, Entity who) => GameplayPanels.Spellbook(world, who, Panel);
        public override bool Activate(World world, Entity who, in PanelRow row) => world.Ready(who, row.Id);
    }

    private sealed class BagScreen : Screen
    {
        public override void Build(World world, Entity who) => GameplayPanels.Inventory(world, who, Panel);

        public override bool Activate(World world, Entity who, in PanelRow row)
        {
            if (!row.Selected) return world.Equip(who, row.Id);
            return world.Records().TryGet(row.Id, out ItemRecord record) && Unequip(world, who, record);
        }

        private static bool Unequip(World world, Entity who, ItemRecord record)
        {
            world.Unequip(who, record.Slot);
            return true;
        }

        public override bool Alternate(World world, Entity who, in PanelRow row) => !world.Drop(who, row.Id).IsNull;
    }

    // Opening fills the panel from the world, and the same key closes it again.
    [Xunit.Fact]
    public void AScreenOpensFilledAndClosesOnTheSameAction()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var open = fx.Engine.Actions.Get("TestSpellbook");
        var screen = new SpellbookScreen();
        fx.Screens.Bind(open, screen);

        Assert.False(fx.Screens.IsOpen);

        Assert.True(fx.Screens.Toggle(open, fx.World, hero));
        Assert.True(fx.Screens.IsOpen);
        Assert.Same(screen, fx.Screens.Top);
        Assert.Equal(2, screen.Panel.Count);          // spark and pillar
        Assert.Equal(0, screen.Index);

        Assert.True(fx.Screens.Toggle(open, fx.World, hero));
        Assert.False(fx.Screens.IsOpen);
    }

    // Moving wraps at both ends: a short list you can hold a key on and go round.
    [Xunit.Fact]
    public void TheSelectionMovesAndWraps()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var screen = new SpellbookScreen();
        fx.Screens.Show(screen, fx.World, hero);

        fx.Screens.Move(1);
        Assert.Equal(1, screen.Index);
        fx.Screens.Move(1);
        Assert.Equal(0, screen.Index);                // wrapped forward
        fx.Screens.Move(-1);
        Assert.Equal(1, screen.Index);                // and backward
    }

    // The whole point, end to end: open the spellbook, move to the second spell, press Enter, and the
    // *world* changed — the readied spell is the one the Cast button will fire.
    [Xunit.Fact]
    public void PressingEnterOnARowChangesTheWorld()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var screen = new SpellbookScreen();
        fx.Screens.Show(screen, fx.World, hero);

        Assert.Equal(Id("spark"), fx.World.Readied(hero));   // the first one learned, until one is readied

        fx.Screens.Move(1);
        Assert.True(fx.Screens.Activate(fx.World, hero));

        Assert.Equal(Id("pillar"), fx.World.Readied(hero));
        // And the panel was re-asked, so the tick has moved with it.
        Assert.True(screen.Panel.TryFind(Id("pillar"), out var row));
        Assert.True(row.Selected);
    }

    // Equipping through a screen, then taking it off again with the same key: the row with the tick
    // is the one that comes off.
    [Xunit.Fact]
    public void TheBagEquipsAndUnequipsFromOneKey()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        fx.World.Give(hero, Id("sword"));
        fx.World.Give(hero, Id("shield"));

        var screen = new BagScreen();
        fx.Screens.Show(screen, fx.World, hero);

        int sword = IndexOf(screen, Id("sword"));
        fx.Screens.Move(sword);
        Assert.True(fx.Screens.Activate(fx.World, hero));
        Assert.Equal(Id("sword"), fx.World.Get<Equipment>(hero).MainHand);
        Assert.True(screen.Panel.TryFind(Id("sword"), out var equipped));
        Assert.True(equipped.Selected);

        Assert.True(fx.Screens.Activate(fx.World, hero));                 // again: take it off
        Assert.True(fx.World.Get<Equipment>(hero).MainHand.IsEmpty);
    }

    // Dropping the last row must not leave the highlight past the end of the list.
    [Xunit.Fact]
    public void DroppingTheLastRowKeepsTheSelectionInsideTheList()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        fx.World.Give(hero, Id("sword"));
        fx.World.Give(hero, Id("bread"), 2);

        var screen = new BagScreen();
        fx.Screens.Show(screen, fx.World, hero);
        int last = screen.Panel.Count - 1;
        fx.Screens.Move(last);

        Assert.True(fx.Screens.Alternate(fx.World, hero));                // drop it

        Assert.True(screen.Index <= Math.Max(screen.Panel.Count - 1, 0));
        Assert.True(screen.Index >= 0);
    }

    // A refusal a screen would grey out is still a refusal when Enter is pressed on it: the rules run,
    // not the screen's idea of them (R17). Readying is deliberately allowed for an unaffordable spell
    // — you choose it, then find the mana — so this checks the *bag*, where equipping bread cannot work.
    [Xunit.Fact]
    public void ActivatingARowThatCannotBeUsedChangesNothing()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        fx.World.Give(hero, Id("bread"), 3);

        var screen = new BagScreen();
        fx.Screens.Show(screen, fx.World, hero);
        Assert.True(screen.Panel.TryFind(Id("bread"), out var bread));
        Assert.False(bread.Enabled);

        Assert.False(fx.Screens.Activate(fx.World, hero));
        Assert.True(fx.World.Get<Equipment>(hero).MainHand.IsEmpty);
        Assert.Equal(3, fx.World.CountOf(hero, Id("bread")));
    }

    // An empty screen, a screen over a subject that cannot have one, and acting with nothing open:
    // three ways a screen meets nothing, none of which may throw.
    [Xunit.Fact]
    public void AnEmptyOrImpossibleScreenIsSafe()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var rock = fx.World.Create(Transform.At(Vector3.One), "rock");

        Assert.False(fx.Screens.Activate(fx.World, hero));   // nothing open
        fx.Screens.Move(1);                                  // and nothing to move

        var bag = new BagScreen();
        fx.Screens.Show(bag, fx.World, hero);                // carrying nothing yet
        Assert.Equal(0, bag.Panel.Count);
        Assert.False(fx.Screens.Activate(fx.World, hero));
        fx.Screens.Move(1);
        Assert.Equal(0, bag.Index);

        fx.Screens.Show(new SpellbookScreen(), fx.World, rock);
        Assert.NotEqual("", fx.Screens.Top!.Panel.Problem);
        Assert.False(fx.Screens.Activate(fx.World, rock));
    }

    // Screens stack: opening a second one over the first leaves the first where it was, and closing
    // comes back to it.
    [Xunit.Fact]
    public void ScreensStackAndComeBack()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var spells = new SpellbookScreen();
        var bag = new BagScreen();

        fx.Screens.Show(spells, fx.World, hero);
        fx.Screens.Move(1);
        fx.Screens.Show(bag, fx.World, hero);

        Assert.Same(bag, fx.Screens.Top);
        Assert.Equal(2, fx.Screens.Open.Count);

        fx.Screens.Close();
        Assert.Same(spells, fx.Screens.Top);
        Assert.Equal(1, spells.Index);        // where it was left

        fx.Screens.CloseAll();
        Assert.False(fx.Screens.IsOpen);
    }

    // Something else changed the world while a screen was open — a save loading, poison ticking, a
    // pickpocket. The screen re-asks rather than showing what used to be true.
    [Xunit.Fact]
    public void AScreenCanBeRebuiltWhenTheWorldMovesUnderIt()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        fx.World.Give(hero, Id("sword"));

        var bag = new BagScreen();
        fx.Screens.Show(bag, fx.World, hero);
        Assert.Equal(1, bag.Panel.Count);

        fx.World.Take(hero, Id("sword"));            // nothing the screen did
        fx.Screens.Rebuild(fx.World, hero);

        Assert.Equal(0, bag.Panel.Count);
        Assert.Equal(0, bag.Index);
    }

    private static int IndexOf(Screen screen, RecordId id)
    {
        for (int i = 0; i < screen.Panel.Count; i++)
            if (screen.Panel[i].Id == id) return i;
        return 0;
    }
}
