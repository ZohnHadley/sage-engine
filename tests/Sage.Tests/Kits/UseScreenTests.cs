#nullable enable
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Screens reachable from play (issue #344): a screen opened about somebody besides the player, from a
// chest's `use_screen` part, a body, a dialogue option's `open_screen` action, a layout button's
// `actions` and `ui_open <screen> <other>`.
public class UseScreenTests
{
    public UseScreenTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "prefab", "id": "chest", "name": "chest",
        "parts": { "inventory": { "items": [ { "item": "bread", "count": 3 } ] }, "use_screen": "rpg:loot" } },
      { "type": "prefab", "id": "bare_chest", "name": "bare chest",
        "parts": { "inventory": {}, "use_screen": {} } },
      { "type": "dialogue", "id": "trader", "start": "hello",
        "nodes": [ { "id": "hello", "text": "Buying?",
                     "options": [ { "text": "Let me see.", "end": true, "actions": [ { "open_screen": "rpg:shop" } ] } ] } ] },
      { "type": "ui_layout", "id": "counter",
        "nodes": { "trade": { "widget": "button", "text": "Trade",
                              "actions": [ { "close_screen": {} }, { "open_screen": "rpg:shop" } ] } } },
      { "type": "screen", "id": "counter", "layout": "counter" }
    ]
    """;

    private static RecordId Id(string name) => new("sage", name);

    // The kit's screens (RpgScreenTests' records), with engine content for its conventions' dead tag.
    private static HeadlessApp Boot() =>
        HeadlessApp.Gameplay().WithEngineContent().With(new UiModule(), new RpgKitModule())
            .File("data/rpg_screens.json", RpgScreenTests.Records).File("data/extra.json", Content).Boot("rpg");

    private static Entity Hero(World world)
    {
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddInventory(hero, 50f);
        hero.AddTag<PlayerControlled>();
        return hero;
    }

    private static UiScreenStack Stack(World world)
    {
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        return stack;
    }

    private static UiLayer Top(UiScreenStack stack) => Assert.Single(stack.Layers, l => l.Modal && !l.IsClosing);

    // A chest made from content: `use_screen` makes it usable, and the player's Use on it opens the loot
    // screen about it. Somebody else using it opens nothing; nor does a second press with a window up.
    [Xunit.Fact]
    public void UsingAChestWithAUseScreenOpensItsLootAboutIt()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var chest = world.Spawn(Id("chest"), new Vector3(0, 0, -1));
        Assert.True(chest.Tags.Has<Interactable>());
        Assert.Equal(RpgKitModule.LootScreen, world.Get<UseScreen>(chest).Screen);

        var npc = world.Create(Transform.At(Vector3.Zero), "npc");
        world.Events.Send(new Used(npc, chest));
        CameraRigTests.Step(world);
        Assert.Empty(stack.Layers);

        world.Events.Send(new Used(hero, chest));
        CameraRigTests.Step(world);
        var loot = Top(stack);
        Assert.Equal(RpgKitModule.LootScreen, loot.Screen!.Id);
        Assert.Equal(new UiBindContext(world, hero, chest), loot.Screen.Context);
        Assert.Equal(3, Assert.IsType<LootView>(loot.Screen.ViewModel).Container.Items.Sum(i => i.Count));

        world.Events.Send(new Used(hero, chest));
        CameraRigTests.Step(world);
        Assert.Single(stack.Layers);

        // A part that names no screen opens rpg_conventions' loot screen, the kit's rpg:loot.
        stack.CloseAll();
        var bare = world.Spawn(Id("bare_chest"), new Vector3(0, 0, -2));
        world.Events.Send(new Used(hero, bare));
        CameraRigTests.Step(world);
        Assert.Equal(RpgKitModule.LootScreen, Top(stack).Screen!.Id);
    }

    // A body: anything with an inventory that dies becomes usable and loots; a dead speaker says nothing,
    // so the hermit's body is looted rather than talked to.
    [Xunit.Fact]
    public void ADeadBodyWithAnInventoryLoots_AndTheDeadSayNothing()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var trader = world.Create(Transform.At(new Vector3(0, 0, -1)), "trader");
        world.AddInventory(trader);
        world.AddAttributes(trader);
        world.Add(trader, new Dialogue { Record = Id("trader") });
        Assert.True(world.Give(trader, Id("bread"), 2));

        // Alive, using him is talking: the kit's conversation screen about him (issue #350), not his loot.
        world.Events.Send(new Used(hero, trader));
        CameraRigTests.Step(world);
        Assert.Equal(RpgKitModule.DialogueScreen, Top(stack).Screen!.Id);
        Assert.True(world.Resources.Get<Conversation>().Running);
        stack.CloseAll();
        CameraRigTests.Step(world);
        Assert.False(world.Resources.Get<Conversation>().Running);   // its screen gone, the conversation ends

        var dead = world.Conventions().Dead;
        Assert.False(dead.IsEmpty);
        world.AddTag(trader, dead.Id);
        world.Events.Send(new Died(trader, hero));
        CameraRigTests.Step(world);
        Assert.True(trader.Tags.Has<Interactable>());
        Assert.False(DialogueRules.Start(world, trader, hero));

        world.Events.Send(new Used(hero, trader));
        CameraRigTests.Step(world);
        var loot = Top(stack);
        Assert.Equal(RpgKitModule.LootScreen, loot.Screen!.Id);
        Assert.Equal(trader, loot.Screen.Context.Other);
    }

    // A dialogue option's `open_screen` opens the shop about the speaker: his goods, the listener's bag.
    [Xunit.Fact]
    public void ADialogueOptionOpensTheShopOverTheSpeakersGoods()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var trader = world.Create(Transform.At(new Vector3(0, 0, -1)), "trader");
        world.AddInventory(trader);
        world.Add(trader, new Dialogue { Record = Id("trader") });
        Assert.True(world.Give(trader, Id("sword")));

        Assert.True(DialogueRules.Start(world, trader, hero));
        Assert.True(DialogueRules.Pick(world, DialogueRules.Current(world)!.Options.Single()));
        Assert.False(world.Resources.Get<Conversation>().Running);
        var shop = Top(stack);
        Assert.Equal(RpgKitModule.ShopScreen, shop.Screen!.Id);
        Assert.Equal(new UiBindContext(world, hero, trader), shop.Screen.Context);
        var view = Assert.IsType<ShopView>(shop.Screen.ViewModel);
        Assert.Equal("trader", view.MerchantName);
        Assert.Contains(view.Stock.Items, i => i.Item == Id("sword"));
    }

    // A layout button names what it does: pressing it closes its own window and opens the shop about the
    // same two, with no view-model behind the screen at all.
    [Xunit.Fact]
    public void ALayoutButtonsActionsRunWhenItIsPressed()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var trader = world.Create(Transform.At(Vector3.Zero), "trader");
        world.AddInventory(trader);

        var counter = stack.Open(Id("counter"), new UiBindContext(world, hero, trader));
        Assert.Null(counter.Screen!.ViewModel);
        stack.Update(UiInput.Wait(0f));
        Assert.Equal("trade", counter.Root.Focused?.Name);
        stack.Update(UiInput.Press);
        stack.Update(UiInput.Wait(0f));

        Assert.DoesNotContain(counter, stack.Layers);
        var shop = Top(stack);
        Assert.Equal(RpgKitModule.ShopScreen, shop.Screen!.Id);
        Assert.Equal(new UiBindContext(world, hero, trader), shop.Screen.Context);
    }

    // `ui_open <screen> <other>`: the console opens a screen about a named entity besides the player.
    [Xunit.Fact]
    public void UiOpenTakesTheEntityTheScreenIsAbout()
    {
        using var app = Boot();
        var world = app.World;
        var stack = Stack(world);
        var hero = Hero(world);
        var crate = world.Create(Transform.At(Vector3.Zero), "old_crate");
        world.AddInventory(crate);

        app.Engine.CVars.Execute("ui_open rpg:loot old_crate");
        var loot = Top(stack);
        Assert.Equal(new UiBindContext(world, hero, crate), loot.Screen!.Context);

        app.Engine.CVars.Execute("ui_open rpg:loot nobody_here");
        Assert.Single(stack.Layers);
    }
}
