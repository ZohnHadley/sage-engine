#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Typing (docs/design/13 §3, TODO F38): the field itself, and the spellmaker screen that needed it.
//
// Both are engine-side, so a test types a name, chooses effects and presses Enter on "make it" — and
// asserts that a *record* now exists and the player knows it. There is no window anywhere in this.
public class TextFieldTests
{
    [Xunit.Fact]
    public void ItTakesCharactersAndBackspace()
    {
        var field = new TextField();

        Assert.True(field.Type("Bob".AsSpan()));
        Assert.Equal("Bob", field.Text);

        Assert.True(field.Backspace());
        Assert.Equal("Bo", field.Text);

        // Backspace arrives as a character from the window, so it works that way round too.
        Assert.True(field.Type('\b'));
        Assert.Equal("B", field.Text);

        Assert.True(field.Backspace());
        Assert.False(field.Backspace());        // empty: nothing to take back, and no exception
        Assert.True(field.IsEmpty);
    }

    // Return and Escape belong to the screen — "done" and "give up" — so the field must not swallow
    // them, and nor may any other control character.
    [Xunit.Theory]
    [Xunit.InlineData('\r')]
    [Xunit.InlineData('\n')]
    [Xunit.InlineData('\t')]
    [Xunit.InlineData('')]
    public void ItLeavesControlCharactersAlone(char c)
    {
        var field = new TextField();
        field.Type("hi".AsSpan());

        Assert.False(field.Type(c));
        Assert.Equal("hi", field.Text);
    }

    [Xunit.Fact]
    public void ItStopsAtItsLength()
    {
        var field = new TextField(maxLength: 4);

        Assert.True(field.Type("abcd".AsSpan()));
        Assert.False(field.Type('e'));
        Assert.Equal("abcd", field.Text);

        Assert.True(field.Backspace());
        Assert.True(field.Type('e'));
        Assert.Equal("abce", field.Text);
    }

    [Xunit.Fact]
    public void ItSaysWhenItChanged()
    {
        var field = new TextField();
        int changes = 0;
        field.Changed += () => changes++;

        field.Type("ab".AsSpan());
        Assert.Equal(2, changes);

        field.Backspace();
        Assert.Equal(3, changes);

        field.Type('\r');           // refused, so nothing changed
        Assert.Equal(3, changes);

        field.Clear();
        Assert.Equal(4, changes);
        field.Clear();              // already empty
        Assert.Equal(4, changes);
    }

    // Anything a person can type is a character, including the ones a keyboard layout produces that
    // a `Keys` enum has no name for. That is the whole reason the field takes characters.
    [Xunit.Fact]
    public void ItTakesWhateverTheKeyboardProduced()
    {
        var field = new TextField();
        field.Type("Björn's épée".AsSpan());
        Assert.Equal("Björn's épée", field.Text);
    }
}

// The spellmaker screen: the first screen that is not just a list (docs/design/16 §3.3).
public class SpellmakerScreenTests
{
    public SpellmakerScreenTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "attribute", "id": "mana",   "start": 60,  "min": 0, "max": 60, "spendEffect": "spend_mana" },
      { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "mend", "cost": 6, "modifiers": [ { "attribute": "health", "op": "Add", "value": 10 } ] },
      { "type": "effect", "id": "harm", "cost": 9, "modifiers": [ { "attribute": "health", "op": "Add", "value": -10 } ] },
      { "type": "effect", "id": "house_only", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },

      { "type": "prefab", "id": "hero", "name": "hero",
        "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {}, "abilities": [] } }
    ]
    """;

    private sealed class Fixture : IDisposable
    {
        public readonly Engine Engine;
        public readonly World World;
        public readonly ScreenStack Screens = new();

        public Fixture()
        {
            Engine = HeadlessApp.Gameplay().With(new RpgKitModule()).File("data/spellmaker_screen.json", Records).Build().Engine;

            World = Engine.CreateWorld("spellmaker");
            World.Resources.Add(Screens);
            var ground = World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
            World.Add(ground, Collider.Box(new Vector3(100, 1, 100)));
        }

        public Entity Hero() => World.Spawn(Id("hero"), Vector3.Zero);

        public void Dispose() => Engine.Dispose();
    }

    private static RecordId Id(string name) => new("sage", name);

    private static void Choose(ScreenStack screens, Screen screen, RecordId row, World world, Entity who)
    {
        for (int i = 0; i < screen.Panel.Count; i++)
            if (screen.Panel[i].Id == row) { screen.Index = i; screens.Activate(world, who); return; }
        throw new Xunit.Sdk.XunitException($"no row {row} on the screen");
    }

    // The whole feature, end to end and headless: type a name, pick an effect, press Enter on the
    // last row, and a spell that did not exist a moment ago is in the player's book.
    [Xunit.Fact]
    public void TypingANameAndChoosingAnEffectMakesASpell()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var screen = new SpellmakerScreen();
        fx.Screens.Show(screen, fx.World, hero);

        Assert.NotNull(screen.Field);
        screen.Field!.Type("Warm Hands".AsSpan());
        screen.Typed(fx.World, hero);

        Choose(fx.Screens, screen, Id("mend"), fx.World, hero);
        Assert.True(screen.Panel.TryFind(Id("mend"), out var chosen));
        Assert.True(chosen.Selected, "the effect is in the spell now");

        Choose(fx.Screens, screen, new RecordId("screen", "make"), fx.World, hero);

        var made = new RecordId("custom", "warm_hands");
        Assert.True(fx.Engine.Records.TryGet(made, out AbilityRecord record));
        Assert.Equal("Warm Hands", record.Name);
        Assert.True(fx.World.Knows(hero, made));

        // And the screen is ready for the next one, with the name box empty again.
        Assert.True(screen.Field!.IsEmpty);
        Assert.Empty(screen.Draft.Effects);
    }

    // "Make it" is a row, and it is greyed with the reason the *rules* give — the same words
    // `Compose` would have refused with (R17). This is what the player reads before they can proceed.
    [Xunit.Fact]
    public void TheMakeRowSaysWhatIsMissing()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var screen = new SpellmakerScreen();
        fx.Screens.Show(screen, fx.World, hero);
        var make = new RecordId("screen", "make");

        Assert.True(screen.Panel.TryFind(make, out var empty));
        Assert.False(empty.Enabled);
        Assert.Contains("name", empty.Reason);

        screen.Field!.Type("Warm Hands".AsSpan());
        screen.Typed(fx.World, hero);
        Assert.True(screen.Panel.TryFind(make, out var named));
        Assert.False(named.Enabled);
        Assert.Contains("effect", named.Reason);        // named, but it does nothing yet

        Choose(fx.Screens, screen, Id("mend"), fx.World, hero);
        Assert.True(screen.Panel.TryFind(make, out var ready));
        Assert.True(ready.Enabled);
        Assert.Equal("", ready.Reason);

        // Pressing Enter on a greyed row does nothing but say why, so a stale screen cannot compose.
        screen.Field!.Clear();
        screen.Typed(fx.World, hero);
        Choose(fx.Screens, screen, make, fx.World, hero);
        Assert.Empty(Spellmaker.Book(fx.World).Drafts);
    }

    // What the screen shows as it is typed: the price follows the choices, and only effects that are
    // for sale are offered at all.
    [Xunit.Fact]
    public void ThePriceFollowsWhatIsChosen()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var screen = new SpellmakerScreen();
        fx.Screens.Show(screen, fx.World, hero);

        Assert.DoesNotContain(screen.Panel.Rows, r => r.Id == Id("house_only"));   // cost 0
        Assert.DoesNotContain(screen.Panel.Rows, r => r.Id == Id("spend_mana"));

        screen.Field!.Type("Ouch".AsSpan());
        screen.Typed(fx.World, hero);
        Choose(fx.Screens, screen, Id("harm"), fx.World, hero);
        float cheap = Spellmaker.Price(fx.Engine.Records, screen.Draft);

        // Power cycles 1 → 1.5 → 2 …, and the spell costs more for it.
        Choose(fx.Screens, screen, new RecordId("screen", "power"), fx.World, hero);
        float dearer = Spellmaker.Price(fx.Engine.Records, screen.Draft);
        Assert.True(dearer > cheap, $"{dearer} should beat {cheap}");
        Assert.Contains(screen.Draft.Magnitude.ToString("0.#"), screen.Panel[1].Detail);

        // Delivery cycles too, and a burst gets a radius to burst in.
        Choose(fx.Screens, screen, new RecordId("screen", "delivery"), fx.World, hero);
        Assert.NotEqual(AbilityTargeting.Touch, screen.Draft.Targeting);
        while (screen.Draft.Targeting != AbilityTargeting.Area)
            Choose(fx.Screens, screen, new RecordId("screen", "delivery"), fx.World, hero);
        Assert.True(screen.Draft.Radius > 0f, "an area spell needs a radius");
    }

    // Choosing an effect twice takes it back out: one key, both directions, like the bag's equip.
    [Xunit.Fact]
    public void ChoosingAnEffectTwiceRemovesIt()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();
        var screen = new SpellmakerScreen();
        fx.Screens.Show(screen, fx.World, hero);

        Choose(fx.Screens, screen, Id("mend"), fx.World, hero);
        Assert.Contains(Id("mend"), screen.Draft.Effects);

        Choose(fx.Screens, screen, Id("mend"), fx.World, hero);
        Assert.DoesNotContain(Id("mend"), screen.Draft.Effects);
        Assert.True(screen.Panel.TryFind(Id("mend"), out var row));
        Assert.False(row.Selected);
    }

    // The spell a player makes on the screen is the spell the console would have made: same rules,
    // same id, same refusal when it is made twice.
    [Xunit.Fact]
    public void TheScreenAndTheConsoleComposeTheSameSpell()
    {
        using var fx = new Fixture();
        var hero = fx.Hero();

        var first = Spellmaker.Compose(fx.World, new SpellDraft
        {
            Name = "Warm Hands", Targeting = AbilityTargeting.Touch, Effects = { Id("mend") },
        });
        Assert.True(first.Ok, first.Problem);

        var screen = new SpellmakerScreen();
        fx.Screens.Show(screen, fx.World, hero);
        screen.Field!.Type("warm hands".AsSpan());       // the same id, differently typed
        screen.Typed(fx.World, hero);
        Choose(fx.Screens, screen, Id("mend"), fx.World, hero);

        Assert.True(screen.Panel.TryFind(new RecordId("screen", "make"), out var make));
        Assert.False(make.Enabled);
        Assert.Contains("already know", make.Reason);
        Assert.Single(Spellmaker.Book(fx.World).Drafts);
    }
}
