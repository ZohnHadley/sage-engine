#nullable enable
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The screens that were panels (issue #350): the spellbook, the bag, a conversation and the spellmaker, now
// `screen` records in the RPG kit's content over view-models (ListViews.cs) on the one screen system there
// is — driven here with a pad's D-pad, A, B and X through the world's UiScreenStack, as the client drives them.
// Their rows are still the panels' (GameplayPanels, DialogueRules, Spellmaker), so a greyed row and a refused
// action give the same reason.
public class PanelScreenTests
{
    public PanelScreenTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "attribute", "id": "mana",   "start": 10,  "min": 0, "max": 60, "spendEffect": "spend_mana" },
      { "type": "effect", "id": "spend_mana", "modifiers": [ { "attribute": "mana", "op": "Add", "value": -1 } ] },
      { "type": "effect", "id": "mend", "cost": 6, "modifiers": [ { "attribute": "health", "op": "Add", "value": 10 } ] },
      { "type": "effect", "id": "harm", "cost": 9, "modifiers": [ { "attribute": "health", "op": "Add", "value": -10 } ] },
      { "type": "effect", "id": "house_only", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "ability", "id": "shout", "targeting": "Self" },
      { "type": "ability", "id": "fireball", "targeting": "Self", "effects": ["harm"], "cost": 40, "costAttribute": "mana" },

      { "type": "item", "id": "sword", "label": "sword", "slot": "MainHand", "weight": 3 },
      { "type": "item", "id": "bread", "label": "bread", "weight": 0.5, "maxStack": 10 },
      { "type": "item", "id": "relic", "label": "old relic", "weight": 1 },

      { "type": "dialogue", "id": "innkeeper", "start": "greet",
        "nodes": [
          { "id": "greet", "text": "What'll it be?",
            "options": [
              { "text": "Tell me about the relic.", "goto": "relic" },
              { "text": "Here, take this relic.", "goto": "greet", "requires": { "item": "sage:relic" }, "refusal": "you have no relic" },
              { "text": "Nothing.", "end": true } ] },
          { "id": "relic", "text": "Old thing. Cursed, they say.", "options": [ { "text": "I see.", "goto": "greet" } ] } ] },

      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {}, "abilities": ["shout", "fireball"], "inventory": { "capacity": 50 } } }
    ]
    """;

    private static RecordId Id(string name) => new("sage", name);

    internal static HeadlessApp Boot() =>
        HeadlessApp.Gameplay().WithEngineContent().With(new UiModule(), new RpgKitModule()).File("data/panel_screens.json", Records).Boot("panels");

    internal static Entity Hero(HeadlessApp app)
    {
        var world = app.World;
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(50, 1, 50)));
        var hero = world.Spawn(Id("hero"), Vector3.Zero);
        if (!world.Has<Equipment>(hero)) world.Add(hero, new Equipment());
        world.RunFixed(1f / 60f);
        return hero;
    }

    private static UiScreenStack Stack(World world)
    {
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        return stack;
    }

    private static RpgScreenTests.Pad Open(World world, RecordId screen, Entity subject)
    {
        var stack = Stack(world);
        stack.CloseAll();
        return new RpgScreenTests.Pad(stack, stack.Open(screen, new UiBindContext(world, subject)));
    }

    private static string Text(RpgScreenTests.Pad pad, string node) => pad.Screen.View.Find<Label>(node)!.Text;

    private static void FocusRow(RpgScreenTests.Pad pad, System.Func<ListRow, bool> which) =>
        pad.NavUntil(UiNavigation.Down, p => p.Focused is ListRow row && which(row));

    // The spellbook (B): what the hero knows, the spell he cannot afford greyed with the cast rule's own
    // reason under the list while it has focus, and A readies it all the same — readying is choosing what to
    // throw, then finding the mana.
    [Xunit.Fact]
    public void TheSpellbookGreysWhatCannotBeCastSaysWhyAndReadiesOnConfirm()
    {
        using var app = Boot();
        var world = app.World;
        var hero = Hero(app);
        var book = Open(world, RpgKitModule.SpellbookScreen, hero);
        var view = Assert.IsType<SpellbookView>(book.Screen.ViewModel);

        Assert.Equal("Spells", Text(book, "title"));
        Assert.Equal(new[] { "shout", "fireball" }, view.Rows.Select(r => r.Id.Name));
        var fireball = view.Rows[1];
        Assert.False(fireball.Enabled);
        Assert.Equal(AbilityRules.Explain(CastRefusal.TooExpensive), fireball.Reason);
        Assert.Equal(ListRow.OffStyle, fireball.Style);
        Assert.Equal("40 mana", fireball.Detail);
        Assert.Equal(Id("shout"), world.Readied(hero));
        Assert.Equal("•", view.Rows[0].Tick);                               // the readied one is ticked

        FocusRow(book, r => r.Id == Id("fireball"));
        Assert.Equal(fireball.Reason, Text(book, "reason"));
        book.A();
        Assert.Equal(Id("fireball"), world.Readied(hero));
        Assert.True(view.Rows[1].Selected);
        Assert.Equal("", view.Rows[0].Tick);

        // The kit's B opens it on the widget stack, as M the spellmaker and J the journal.
        var stack = Stack(world);
        foreach (string action in new[] { "Spellbook", "Spellmaker", "Journal", "Rest" })
            Assert.Contains(app.Engine.Actions.Get(action), stack.OpenActions);
    }

    // The bag: A equips what is not in a hand and takes off what is (one button, the tick between), and X
    // drops one in front of the hero — the console's `inv` rows, through Items' own rules.
    [Xunit.Fact]
    public void TheBagEquipsTakesOffAndDrops()
    {
        using var app = Boot();
        var world = app.World;
        var hero = Hero(app);
        Assert.True(world.Give(hero, Id("sword")));
        Assert.True(world.Give(hero, Id("bread"), 3));
        var bag = Open(world, RpgKitModule.BagScreen, hero);
        var view = Assert.IsType<BagView>(bag.Screen.ViewModel);

        Assert.StartsWith("Carrying", view.Title);
        Assert.Contains(view.Rows, r => r.Text == "bread ×3");
        var bread = view.Rows.Single(r => r.Id == Id("bread"));
        Assert.False(bread.Enabled);                                           // greyed with the rule's own reason
        Assert.Equal("not something you can wear or wield", bread.Reason);
        FocusRow(bag, r => r.Id == Id("sword"));
        bag.A();
        Assert.Equal(Id("sword"), world.Get<Equipment>(hero).In(RpgKitModule.MainHand));
        Assert.True(view.Rows.Single(r => r.Id == Id("sword")).Selected);
        bag.A();
        Assert.True(world.Get<Equipment>(hero).In(RpgKitModule.MainHand).IsEmpty);

        FocusRow(bag, r => r.Id == Id("bread"));
        bag.Press(UiInput.Do(UiCommand.Alternate));
        Assert.Equal(2, world.CountOf(hero, Id("bread")));
        Assert.Contains(view.Rows, r => r.Text == "bread ×2");
    }

    // Using somebody with something to say opens the kit's conversation about them: the node's line, the
    // options the rules allow and the one they refuse, greyed with its refusal; an option that ends the
    // conversation closes the screen, and Back on it ends the conversation.
    [Xunit.Fact]
    public void UsingSomebodyOpensTheConversationAndItsEndClosesIt()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(app);
        var innkeeper = world.Create(Transform.At(new Vector3(0, 0, -1)), "innkeeper");
        world.Add(innkeeper, new Dialogue { Record = Id("innkeeper") });
        var conversation = world.Resources.Get<Conversation>();

        world.Events.Send(new Used(hero, innkeeper));
        CameraRigTests.Step(world);
        var layer = Assert.Single(stack.Layers, l => l.Modal);
        Assert.Equal(RpgKitModule.DialogueScreen, layer.Screen!.Id);
        Assert.True(conversation.Running);
        var talk = new RpgScreenTests.Pad(stack, layer);
        stack.Update(UiInput.Wait(0f));
        var view = Assert.IsType<DialogueView>(talk.Screen.ViewModel);
        Assert.Equal("What'll it be?", Text(talk, "line"));
        Assert.Equal("innkeeper", view.Speaker);
        Assert.Equal(3, view.Rows.Count);
        Assert.False(view.Rows[1].Enabled);
        Assert.Equal("you have no relic", view.Rows[1].Reason);

        talk.A();                                                              // "Tell me about the relic."
        Assert.Equal("Old thing. Cursed, they say.", Text(talk, "line"));
        talk.A();                                                              // "I see."
        FocusRow(talk, r => r.Text == "Nothing.");
        talk.A();
        Assert.False(conversation.Running);
        Assert.True(layer.IsClosing || !stack.Layers.Contains(layer));

        // Again, and Back: the screen closes, and the conversation with it.
        CameraRigTests.Step(world);
        stack.Update(UiInput.Wait(0f));
        world.Events.Send(new Used(hero, innkeeper));
        CameraRigTests.Step(world);
        var again = new RpgScreenTests.Pad(stack, Assert.Single(stack.Layers, l => l.Modal && !l.IsClosing));
        Assert.True(conversation.Running);
        again.B();
        Assert.False(conversation.Running);
        Assert.False(stack.IsOpen);
    }

    // The spellmaker: the name field has focus when it opens, typing goes into it (and a letter that opens a
    // screen does not), "make it" says what is missing in the words Spellmaker.CanCompose gives, and the spell
    // made is a record the hero then knows — the one the console's spellmaker would make.
    [Xunit.Fact]
    public void TheSpellmakerTakesATypedNameAndMakesTheSpell()
    {
        using var app = Boot();
        var world = app.World;
        var hero = Hero(app);
        var maker = Open(world, RpgKitModule.SpellmakerScreen, hero);
        var view = Assert.IsType<SpellmakerView>(maker.Screen.ViewModel);
        Assert.IsType<TextBox>(maker.Root.Focused);
        Assert.DoesNotContain(view.Rows, r => r.Id == Id("house_only") || r.Id == Id("spend_mana"));   // not for sale

        var make = view.Rows.Single(r => r.Id == SpellmakerView.MakeRow);
        Assert.False(make.Enabled);
        Assert.Contains("name", make.Reason);

        maker.Press(UiInput.Type("Warm Hands"));
        Assert.Equal("Warm Hands", view.Name);
        Assert.Contains("\"Warm Hands\"", Text(maker, "title"));
        Assert.Contains("effect", view.Rows.Single(r => r.Id == SpellmakerView.MakeRow).Reason);

        // While the field has the keyboard, the spellmaker's own key (a letter) does not close it.
        var stack = maker.Stack;
        Assert.True(stack.Typing);
        Assert.False(stack.Toggle(app.Engine.Actions.Get("Spellmaker"), new UiBindContext(world, hero)));
        Assert.Same(maker.Layer, stack.Top);

        FocusRow(maker, r => r.Id == Id("mend"));
        Assert.False(stack.Typing);
        maker.A();
        Assert.Contains(Id("mend"), view.Draft.Effects);
        Assert.True(view.Rows.Single(r => r.Id == Id("mend")).Selected);
        FocusRow(maker, r => r.Id == SpellmakerView.MakeRow);
        Assert.True(((ListRow)maker.Focused!).Enabled);
        maker.A();

        var made = new RecordId("custom", "warm_hands");
        Assert.True(app.Engine.Records.TryGet(made, out AbilityRecord record));
        Assert.Equal("Warm Hands", record.Name);
        Assert.True(world.Knows(hero, made));
        Assert.Equal("", view.Name);                                           // ready for the next one
        Assert.Empty(view.Draft.Effects);
        Assert.Equal("", maker.Screen.View.Find<TextBox>("name")!.Text);
    }

}

// An open list reads the world every frame and allocates nothing while nothing changes (02 §4.6).
[Xunit.Collection(MeasurementsCollection.Name)]
public class PanelScreenAllocationTests
{
    public PanelScreenAllocationTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void AnOpenListAllocatesNothingWhileNothingChanges()
    {
        using var app = PanelScreenTests.Boot();
        var world = app.World;
        var hero = PanelScreenTests.Hero(app);
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        Assert.True(world.Give(hero, new RecordId("sage", "bread"), 3));
        stack.Open(RpgKitModule.SpellbookScreen, new UiBindContext(world, hero));
        stack.Open(RpgKitModule.BagScreen, new UiBindContext(world, hero));
        for (int i = 0; i < 5; i++) stack.Update(UiInput.Wait(1f / 60f));
        AllocationProbe.AssertNone(100, () => stack.Update(UiInput.Wait(1f / 60f)));
    }
}
