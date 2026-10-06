#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The RPG kit's screens (docs/design/13 "As built (the RPG screens)", issue #98): the inventory grid with
// its weight, equipment, loot and topics, each a `screen` record in the kit's own content over a headless
// view-model, driven here the way a gamepad drives them — D-pad, A and B through UiRoot.Update.
public class RpgScreenTests
{
    public RpgScreenTests() { _ = TestEnv.UserRoot; }

    internal const string Records = """
    [
      { "type": "item", "id": "sword", "label": "sword", "slot": "MainHand", "weight": 3 },
      { "type": "item", "id": "shield", "label": "shield", "slot": "OffHand", "weight": 4 },
      { "type": "item", "id": "bread", "label": "bread", "weight": 0.5, "maxStack": 10 },
      { "type": "item", "id": "coin", "label": "coin", "weight": 0.1, "maxStack": 100 },
      { "type": "item", "id": "anvil", "label": "anvil", "weight": 50 },
      { "type": "rpg_item", "id": "sword", "grid": [1, 3] },
      { "type": "rpg_item", "id": "anvil", "grid": [2, 2] },

      { "type": "dialogue_topic", "id": "the_bridge", "keyword": "the bridge", "known": true,
        "infos": [ { "text": "Closed. Ask me about the toll.", "then": [ { "add_topic": "toll" } ] } ] },
      { "type": "dialogue_topic", "id": "toll", "keyword": "toll",
        "infos": [ { "requires": { "speaker": { "name": "guard" } }, "text": "Ten gold, friend." },
                   { "text": "Ask the guard." } ] }
    ]
    """;

    private static RecordId Id(string name) => new("sage", name);

    internal static HeadlessApp Boot(string extra = "")
    {
        var builder = HeadlessApp.Gameplay().With(new UiModule(), new RpgKitModule()).File("data/rpg_screens.json", Records);
        if (extra.Length > 0) builder.File("data/extra.json", extra);
        return builder.Boot("rpg");
    }

    private static Entity Carrier(World world, string name, float capacity, params (string Item, int Count)[] items)
    {
        var entity = world.Create(Transform.At(Vector3.Zero), name);
        world.AddInventory(entity, capacity);
        foreach (var (item, count) in items) Assert.True(world.Give(entity, Id(item), count));
        return entity;
    }

    // A screen on the world's UiScreenStack (#97), and a gamepad: each press is one UiScreenStack.Update —
    // the top layer takes it, the view-model acts on it (UiScreen.Handle) — then an idle frame, in which
    // the screen reads its view-model again and lays out: what the client does frame by frame.
    internal sealed class Pad
    {
        public Pad(UiScreenStack stack, UiLayer layer)
        {
            Stack = stack;
            Layer = layer;
        }

        public UiScreenStack Stack { get; }
        public UiLayer Layer { get; }
        public UiRoot Root => Layer.Root;
        public UiScreen Screen => Layer.Screen!;
        public object? Focused => UiScreen.RowOf(Root.Focused);

        public UiResult Press(UiInput input)
        {
            var result = Stack.Update(input);
            Stack.Update(UiInput.Wait(0f));
            return result;
        }

        public void Nav(UiNavigation direction, int times = 1)
        {
            for (int i = 0; i < times; i++) Press(UiInput.Nav(direction));
        }

        public void A() => Press(UiInput.Press);
        public UiResult B() => Press(UiInput.Cancel);

        // Presses a direction until `until` holds (at most 40 times), as a player would.
        public void NavUntil(UiNavigation direction, Func<Pad, bool> until)
        {
            for (int i = 0; i < 40 && !until(this); i++) Press(UiInput.Nav(direction));
            Assert.True(until(this), $"pressing {direction} never got there (focus: {Root.Focused?.Name})");
        }
    }

    private static Pad Open(HeadlessApp app, RecordId screen, Entity subject, Entity other = default)
    {
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.CloseAll();
        return new Pad(stack, stack.Open(screen, new UiBindContext(app.World, subject, other)));
    }

    private static string Text(UiScreen screen, string node) => screen.View.Find<Label>(node)!.Text;

    // The shop shell (issue #99): the merchant's goods beside the bag, the hand between them, and a move
    // across priced by the stub rule — an item's value to buy, half to sell — into the balance.
    [Fact]
    public void AShopPricesAMoveAcrossByTheStubRuleAndKeepsTheBalance()
    {
        using var app = Boot("""[ { "type": "item", "id": "sword", "patch": true, "value": 40 } ]""");
        var world = app.World;
        var hero = Carrier(world, "hero", 20f, ("bread", 2));
        var merchant = Carrier(world, "merchant", 0f, ("sword", 1));
        Assert.Equal(40, StubPriceRule.Instance.Price(world, Id("sword"), 1, buying: true));
        Assert.Equal(20, StubPriceRule.Instance.Price(world, Id("sword"), 1, buying: false));
        Assert.Equal(2, StubPriceRule.Instance.Price(world, Id("bread"), 2, buying: false));   // worthless: 1 a piece

        var shop = Open(app, new RecordId("rpg", "shop"), hero, merchant);
        var view = Assert.IsType<ShopView>(shop.Screen.ViewModel);
        Assert.Equal("merchant's goods", Text(shop.Screen, "stock_title"));
        Assert.Same(view.Stock.CellAt(0, 0), shop.Focused);
        shop.A();                                                                       // pick the sword up
        Assert.True(view.HeldIsStock);
        Assert.Equal(40, view.HeldPrice);
        Assert.Equal("Buying sword for 40", Text(shop.Screen, "buying"));
        shop.NavUntil(UiNavigation.Right, p => p.Focused is GridCell c && c.Grid == view.Bag && c.Item == null);
        shop.A();                                                                       // put it in the bag: bought
        Assert.Equal("", view.Message);
        Assert.Equal(1, world.CountOf(hero, Id("sword")));
        Assert.Equal(0, world.CountOf(merchant, Id("sword")));
        Assert.Equal(40, view.Balance);
        Assert.Equal("You owe 40", Text(shop.Screen, "balance"));
        Assert.Equal(0, view.HeldPrice);
    }

    // The acceptance test of issue #98 (phase 4c's exit): with gamepad input only, a stack is moved on the
    // inventory grid, a move from a corpse that would go over the weight limit is refused, the corpse is
    // looted, and a topic is asked — on three screens built from the kit's screen and ui_layout records.
    [Fact]
    public void AGamepadMovesAnItem_IsRefusedOverTheWeightLimit_LootsACorpse_AndAsksATopic()
    {
        using var app = Boot();
        var world = app.World;
        var hero = Carrier(world, "hero", 20f, ("sword", 1), ("bread", 2));
        var bandit = Carrier(world, "bandit", 0f, ("anvil", 1), ("coin", 20), ("bread", 3));
        var guard = world.Create(Transform.At(new Vector3(2, 0, 0)), "guard");

        // ---- the inventory: move the sword three squares right --------------------------------------
        var bag = Open(app, RpgKitModule.InventoryScreen, hero);
        Assert.Equal(new RecordId("rpg", "inventory"), bag.Screen.View.Layout);            // from the kit's records
        var inventory = Assert.IsType<InventoryView>(bag.Screen.ViewModel);
        var cells = bag.Screen.View.Find<Grid>("cells")!;
        Assert.Equal(8, cells.Columns);                                                     // bound to the grid's width
        Assert.Equal(8 * 6, cells.ChildCount);
        Assert.Equal("Weight 4 / 20 kg", Text(bag.Screen, "weight"));
        Assert.Equal(0.2f, bag.Screen.View.Find<Bar>("weight_bar")!.Fraction, 3);
        var sword = inventory.Bag.ItemAt(0, 0)!;
        Assert.Equal((1, 3), (sword.Width, sword.Height));                                  // rpg_item's footprint
        Assert.Same(sword, inventory.Bag.ItemAt(0, 2));
        Assert.Equal("bread ×2", inventory.Bag.CellAt(1, 0)!.Label);                        // 1×1 by default, next to it

        Assert.Same(inventory.Bag.CellAt(0, 0), bag.Focused);                               // a screen opens focused
        bag.A();                                                                            // pick the sword up
        Assert.Same(sword, inventory.Held);
        Assert.Equal(GridCell.HeldStyle, cells.Child(0).Style);
        Assert.Equal("Moving sword", Text(bag.Screen, "holding"));
        bag.Nav(UiNavigation.Right, 3);
        Assert.Same(inventory.Bag.CellAt(3, 0), bag.Focused);
        bag.A();                                                                            // put it down
        Assert.Null(inventory.Held);
        Assert.Equal("sword", inventory.Bag.ItemAt(3, 2)!.Label);
        Assert.Null(inventory.Bag.ItemAt(0, 0));
        Assert.Equal(GridCell.EmptyStyle, cells.Child(0).Style);
        Assert.Equal(GridCell.ItemStyle, cells.Child(3).Style);
        Assert.Contains(world.Get<ItemGridPlacements>(hero).Placed!, p => p.Item == Id("sword") && p.X == 3 && p.Y == 0);
        bag.A();                                                                            // pick the sword up again,
        bag.B();                                                                            // and B puts it back
        Assert.Null(inventory.Held);
        Assert.False(bag.Layer.IsClosing);                                                  // the view-model used that B
        bag.B();                                                                            // nothing held: B closes
        Assert.True(bag.Layer.IsClosing);

        // ---- loot: the anvil is too heavy, take all takes the rest ----------------------------------
        var loot = Open(app, RpgKitModule.LootScreen, hero, bandit);
        var view = Assert.IsType<LootView>(loot.Screen.ViewModel);
        Assert.Equal("bandit", Text(loot.Screen, "their_title"));
        Assert.Same(view.Container.CellAt(0, 0), loot.Focused);
        loot.A();                                                                           // pick the anvil up
        Assert.Equal(Id("anvil"), view.Held!.Item);
        loot.NavUntil(UiNavigation.Right, p => p.Focused is GridCell c && c.Grid == view.Bag);
        loot.A();                                                                           // put it in the bag: refused
        Assert.Null(view.Held);
        Assert.Equal("anvil is too heavy: that would be 54 of 20 kg.", view.Message);
        Assert.Equal(view.Message, Text(loot.Screen, "message"));
        Assert.Equal(1, world.CountOf(bandit, Id("anvil")));
        Assert.Equal(0, world.CountOf(hero, Id("anvil")));
        Assert.Equal(4f, world.WeightOf(hero), 3);

        loot.NavUntil(UiNavigation.Down, p => p.Root.Focused?.Name == LootView.TakeAllButton);
        loot.A();                                                                           // take all
        Assert.Equal(20, world.CountOf(hero, Id("coin")));
        Assert.Equal(5, world.CountOf(hero, Id("bread")));
        Assert.Equal(1, world.CountOf(bandit, Id("anvil")));                                // left behind, and said so
        Assert.Equal(0, world.CountOf(bandit, Id("coin")));
        Assert.Equal("Left anvil: too heavy.", view.Message);
        Assert.Single(view.Container.Items);
        Assert.Equal(7.5f, view.Bag.Weight, 3);
        Assert.Equal("Weight 7.5 / 20 kg", Text(loot.Screen, "weight"));

        // ---- topics: ask about the bridge, and the toll it teaches ----------------------------------
        var talk = Open(app, RpgKitModule.TopicsScreen, hero, guard);
        var topics = Assert.IsType<TopicsView>(talk.Screen.ViewModel);
        Assert.Equal("guard", Text(talk.Screen, "title"));
        Assert.Equal(new[] { "the bridge" }, topics.Topics.Select(t => t.Keyword));
        Assert.Same(topics.Topics[0], talk.Focused);
        talk.A();                                                                           // ask it
        Assert.Equal("Closed. Ask me about the toll.", Text(talk.Screen, "answer"));
        Assert.True(DialogueTopics.Knows(world, hero, Id("toll")));                        // its `then` taught one
        Assert.Equal(new[] { "the bridge", "toll" }, topics.Topics.Select(t => t.Keyword));
        Assert.Equal(TopicsView.AskedStyle, talk.Screen.View.Find<ItemList>("list")!.Child(0).Style);
        talk.Nav(UiNavigation.Down);
        talk.A();
        Assert.Equal("Ten gold, friend.", Text(talk.Screen, "answer"));                    // the guard's own answer
    }

    // A long answer is a paragraph (#338): the topics screen's answer wraps inside its 420 units, a line
    // under another, and the window grows down to hold it rather than off the side of the screen.
    [Fact]
    public void ALongAnswerWrapsInsideTheTopicsScreensAnswerBox()
    {
        const string speech = "The bridge has been closed since the spring floods took the middle span, and the " +
                              "captain will not open it again until the masons from the city have looked at every stone.";
        using var app = Boot("""
            [ { "type": "dialogue_topic", "id": "the_bridge", "patch": true, "infos": [ { "text": "SPEECH" } ] } ]
            """.Replace("SPEECH", speech));
        var world = app.World;
        var hero = Carrier(world, "hero", 0f);
        var guard = world.Create(Transform.At(Vector3.Zero), "guard");
        var talk = Open(app, RpgKitModule.TopicsScreen, hero, guard);
        talk.A();                                                                           // ask about the bridge

        var answer = talk.Screen.View.Find<Label>("answer")!;
        Assert.Equal(speech, answer.Text);
        Assert.True(answer.Wrap);
        talk.Root.Layout();
        float line = talk.Root.Text.LineHeight * answer.TextScale;
        Assert.True(answer.Rect.Width <= 420f + 0.01f, $"the answer is {answer.Rect.Width} wide");
        Assert.True(answer.DesiredSize.Y >= 3 * line, $"the answer is {answer.DesiredSize.Y / line} line(s)");

        var plan = new UiRenderPlan();
        plan.Update(talk.Root, app.World.Resources.Get<UiStyles>());
        var lines = plan.Commands.ToArray().Where(c => c.Kind == UiDrawKind.Text && c.Widget == answer).ToArray();
        Assert.True(lines.Length >= 3);
        Assert.Equal(speech.Replace(" ", ""), string.Concat(lines.Select(l => l.Text!.Substring(l.Start, l.Length))).Replace(" ", ""));   // every word, once
        Assert.All(lines, l => Assert.True(l.Rect.Right <= talk.Root.ToPixels(answer.ContentRect).Right + 0.01f));
    }

    // Equipment over EquipSlots: the kit's two hands, what fills them, and what could.
    [Fact]
    public void TheEquipmentScreenPutsOnAndTakesOffThroughTheItemRules()
    {
        using var app = Boot();
        var world = app.World;
        var hero = Carrier(world, "hero", 0f, ("sword", 1), ("shield", 1), ("bread", 1));
        var pad = Open(app, RpgKitModule.EquipmentScreen, hero);
        var view = Assert.IsType<EquipmentView>(pad.Screen.ViewModel);

        Assert.Equal(new[] { "Main hand: nothing", "Off hand: nothing" }, view.Slots.Select(s => s.Text));
        Assert.Equal(new[] { "sword (Main hand)", "shield (Off hand)" }, view.Candidates.Select(c => c.Text));   // not the bread

        pad.NavUntil(UiNavigation.Down, p => p.Focused is EquipmentView.CandidateRow { Text: "sword (Main hand)" });
        pad.A();
        Assert.Equal(Id("sword"), world.Get<Equipment>(hero).In(RpgKitModule.MainHand));
        Assert.Equal("Main hand: sword", view.Slots[0].Text);
        Assert.Equal(new[] { "shield (Off hand)" }, view.Candidates.Select(c => c.Text));

        pad.NavUntil(UiNavigation.Up, p => p.Focused == view.Slots[0]);
        pad.A();
        Assert.True(world.Get<Equipment>(hero).In(RpgKitModule.MainHand).IsEmpty);
        Assert.Equal("Main hand: nothing", ((Label)pad.Screen.View.Find<ItemList>("slots")!.Child(0)).Text);
    }

    // The kit's content is in its assembly, mounted as `rpg` after the engine's and before anything else,
    // so a game's patch reaches a node of a kit layout and a line of its strings without copying either.
    [Fact]
    public void TheKitsContentIsMountedAndAGamePatchesIt()
    {
        using var app = Boot("""
        [ { "type": "ui_layout", "id": "rpg:inventory", "patch": true, "nodes": { "hint": { "visible": false } } },
          { "type": "rpg_conventions", "id": "rpg", "inventoryGrid": [10, 4] } ]
        """);
        var mount = Assert.Single(app.Vfs.Mounts.OfType<AssemblyContentMount>());
        Assert.Equal(RpgKitModule.Id, mount.Name);
        Assert.Equal("rpg", mount.RecordNamespace);
        Assert.True(mount.Exists(VirtualPath.Parse("strings/en/rpg.json")));
        Assert.Contains(VirtualPath.Parse("data/ui_screens.json"), mount.Enumerate(VirtualPath.Parse("data"), "*.json", recursive: true));
        Assert.Null(mount.PhysicalPath(VirtualPath.Parse("data/ui_screens.json")));
        foreach (var screen in new[] { RpgKitModule.InventoryScreen, RpgKitModule.EquipmentScreen, RpgKitModule.LootScreen, RpgKitModule.TopicsScreen })
            Assert.True(app.Records.Exists("screen", screen), $"{screen}");
        Assert.Equal(0, app.Records.ErrorCount);

        var hero = Carrier(app.World, "hero", 0f);
        var pad = Open(app, RpgKitModule.InventoryScreen, hero);
        Assert.False(pad.Screen.View.Find("hint")!.Visible);                                // the game's patch
        Assert.Equal("Inventory", Text(pad.Screen, "title"));                               // the kit's strings
        Assert.Equal(10, pad.Screen.View.Find<Grid>("cells")!.Columns);                     // the game's grid size
        Assert.Equal(40, pad.Screen.View.Find<Grid>("cells")!.ChildCount);
        Assert.Equal("Weight 0 kg", Text(pad.Screen, "weight_any"));                         // no capacity: no limit
        Assert.False(pad.Screen.View.Find("weight_bar")!.Visible);
    }

    // Without the kit there is no kit content, and a kit brings the base plugins it needs even when
    // game.json's `plugins` leaves them out (the RPG kit's screens need sage.ui).
    [Fact]
    public void AKitBringsItsContentAndTheBasePluginsItNeeds()
    {
        using (var bare = HeadlessApp.Gameplay().Boot("bare"))
            Assert.Empty(bare.Vfs.Mounts.OfType<AssemblyContentMount>());

        string folder = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(folder, "game.json"),
            """{ "id": "kitplugins", "plugins": ["sage.gameplay.items", "sage.gameplay.abilities"], "kits": ["sage.kits.rpg"] }""");
        using var app = HeadlessApp.ForGame(folder).Build();
        Assert.Contains(UiModule.Id, app.PluginIds);
        Assert.Contains(RpgKitModule.Id, app.PluginIds);
        Assert.True(app.Records.Exists("screen", RpgKitModule.LootScreen));
    }

    // A footprint is whole squares, for an item that exists; a bad one is a load error at its line.
    [Fact]
    public void AnRpgItemIsCheckedAgainstItsItem()
    {
        using var app = Boot("""
        [ { "type": "rpg_item", "id": "ghost", "grid": [1, 1] },
          { "type": "rpg_item", "id": "bread", "grid": [0.5, 2] } ]
        """);
        Assert.Equal(2, app.Records.ErrorCount);
        Assert.Equal((1, 1), RpgItemRecord.Of(app.Records, Id("coin")));                    // none: 1×1
        Assert.Equal((2, 2), RpgItemRecord.Of(app.Records, Id("anvil")));
    }

    // Where the player put things is saved with them; a stack with no place (a save from before the grid,
    // an item given since) is packed into the first free square, the same way every time.
    [Fact]
    public void PlacesOnTheGridSurviveASave()
    {
        using var app = Boot();
        var world = app.World;
        var hero = Carrier(world, "hero", 0f, ("sword", 1), ("coin", 5));
        world.MakePersistent(hero);                                                         // what a save writes
        var grid = new ItemGrid(4, 3);
        grid.Refresh(world, hero);
        Assert.Equal((0, 0), (grid.Items[0].X, grid.Items[0].Y));
        Assert.Equal((1, 0), (grid.Items[1].X, grid.Items[1].Y));
        Assert.False(grid.Move(world, grid.Items[1], 0, 2));                                 // under the sword
        Assert.True(grid.Move(world, grid.Items[0], 3, 0));
        Assert.Equal(4, grid.Rows);                                                         // one free row below the sword
        Assert.True(app.Engine.Saves.Save("grid"));

        Assert.True(grid.Move(world, grid.Items[0], 2, 0));
        Assert.True(app.Engine.Saves.Load("grid"));
        var loaded = world.FindByName("hero");
        var again = new ItemGrid(4, 3);
        again.Refresh(world, loaded);
        Assert.Equal(Id("sword"), again.ItemAt(3, 2)!.Item);
        Assert.Equal(Id("coin"), again.ItemAt(1, 0)!.Item);
        Assert.False(again.Refresh(world, loaded));                                         // nothing changed: nothing done
    }

    // ---- drag and drop, pictures, turning, splitting and dropping (issue #346) -------------------------

    private const string Pictures = """
        [ { "type": "rpg_item", "id": "sword", "patch": true, "icon": "textures/sword.png" } ]
        """;

    private static Widget CellWidget(Grid cells, GridCell cell) => cells.Child(cell.Y * cell.Grid.Columns + cell.X);

    // A square's middle in pixels (the stack's viewport is the design size: one pixel a unit), from the
    // second square of the top row, which these tests keep a single square.
    private static Vector2 At(Grid cells, int column, int row)
    {
        var second = cells.Child(1).Rect;
        float pitch = second.Width + cells.Spacing.X;
        return new Vector2(second.X + pitch * (column - 1) + second.Width / 2f, second.Y + pitch * row + second.Height / 2f);
    }

    // The acceptance test of issue #346 for the mouse: the sword's picture spans its three squares; the
    // pointer drags it by its bottom square, R turns it in the hand, and let go it lies across three
    // squares where the pointer put it, turned, and saved so; a stack dragged off the window lands on the
    // ground in front of the hero.
    [Fact]
    public void AMouseDragsAnItemByItsPictureTurnsItAndDropsAStackOnTheGround()
    {
        using var app = Boot(Pictures);
        var world = app.World;
        var hero = Carrier(world, "hero", 0f, ("sword", 1), ("bread", 4));
        var bag = Open(app, RpgKitModule.InventoryScreen, hero);
        var view = Assert.IsType<InventoryView>(bag.Screen.ViewModel);
        var cells = bag.Screen.View.Find<Grid>("cells")!;
        bag.Press(UiInput.Wait(1f));   // opened: faded in, laid out

        // One picture across the sword's squares: its top-left square spans 1 × 3 and the others hide.
        var sword = view.Bag.ItemAt(0, 0)!;
        var top = CellWidget(cells, view.Bag.CellAt(0, 0)!);
        Assert.Equal((1, 3), (top.ColumnSpan, top.RowSpan));
        Assert.Equal("textures/sword.png", ((Button)top).Icon);
        Assert.False(CellWidget(cells, view.Bag.CellAt(0, 1)!).Visible);
        Assert.False(CellWidget(cells, view.Bag.CellAt(0, 2)!).Visible);
        Assert.True(top.Draggable);
        Assert.Equal(cells.Child(1).Rect.Height * 3f + cells.Spacing.Y * 2f, top.Rect.Height, 3);

        // Grabbed by its bottom square and dragged: the hand has it, whole (its picture is the ghost).
        var stack = bag.Stack;
        stack.Update(UiInput.Hold(At(cells, 0, 2)));
        var result = stack.Update(UiInput.Drag(At(cells, 4, 4)));
        Assert.True(result.DragStarted);
        Assert.Same(sword, view.Held);
        Assert.True(view.Dragging);
        Assert.True(top.Visible);
        // Over square (4, 4) by its bottom square: it would go at (4, 2) to (4, 4), and it fits there.
        stack.Update(UiInput.Drag(At(cells, 4, 4) + new Vector2(1f, 0f)));
        Assert.Equal(GridCell.TargetStyle, view.Bag.CellAt(4, 2)!.Style);
        Assert.Equal(GridCell.TargetStyle, view.Bag.CellAt(4, 4)!.Style);
        Assert.Equal(GridCell.EmptyStyle, view.Bag.CellAt(4, 1)!.Style);

        // R turns it in the hand: three across from the square under the pointer, blocked by nothing.
        stack.Update(new UiInput { Command = UiCommand.Rotate, Pointer = At(cells, 4, 4), PointerDown = true });
        Assert.True(view.HeldTurned);
        stack.Update(UiInput.Drag(At(cells, 4, 4)));
        Assert.Equal(GridCell.TargetStyle, view.Bag.CellAt(6, 4)!.Style);
        Assert.Equal(GridCell.EmptyStyle, view.Bag.CellAt(4, 3)!.Style);

        // Let go: it lies across (4, 4) to (6, 4), turned, its picture turned with it, and saved so.
        result = stack.Update(UiInput.Release(At(cells, 4, 4)));
        Assert.True(result.Dropped);
        stack.Update(UiInput.Wait(0f));
        Assert.Null(view.Held);
        sword = view.Bag.ItemAt(4, 4)!;
        Assert.Equal(Id("sword"), sword.Item);
        Assert.True(sword.Rotated);
        Assert.Equal((3, 1), (sword.Width, sword.Height));
        Assert.Same(sword, view.Bag.ItemAt(6, 4));
        Assert.Null(view.Bag.ItemAt(0, 0));
        var corner = (Button)CellWidget(cells, view.Bag.CellAt(4, 4)!);
        Assert.Equal((3, 1), (corner.ColumnSpan, corner.RowSpan));
        Assert.True(corner.IconTurned);
        Assert.Contains(world.Get<ItemGridPlacements>(hero).Placed!, p => p.Item == Id("sword") && p.X == 4 && p.Y == 4 && p.Rotated);

        // A click (pressed and let go in place) is the hand's: it picks the bread up, and Back puts it back.
        var bread = view.Bag.ItemAt(1, 0)!;
        Assert.Equal(Id("bread"), bread.Item);
        stack.Update(UiInput.Hold(At(cells, 1, 0)));
        stack.Update(UiInput.Release(At(cells, 1, 0)));
        Assert.Same(bread, view.Held);
        Assert.False(view.Dragging);
        bag.B();
        Assert.Null(view.Held);
        Assert.False(bag.Layer.IsClosing);

        // Dragged off the window and let go over the world: on the ground in front of the hero.
        stack.Update(UiInput.Hold(At(cells, 1, 0)));
        Assert.True(stack.Update(UiInput.Drag(new Vector2(4f, 4f))).DragStarted);
        stack.Update(UiInput.Release(new Vector2(4f, 4f)));
        stack.Update(UiInput.Wait(0f));
        Assert.Equal("", view.Message);
        Assert.Equal(0, world.CountOf(hero, Id("bread")));
        var pickups = world.Query<Pickup>().Entities.ToArray();
        Assert.Single(pickups);
        Assert.Equal(4, world.Get<Pickup>(pickups[0]).Count);
        Assert.False(bag.Layer.IsClosing);                                                  // a drag is not a click outside
    }

    // And for the gamepad: the left shoulder turns a stack where it lies (refused when there is no room)
    // or in the hand, the left trigger halves a stack, X drops one — and a corpse's stacks are not the
    // hero's to drop.
    [Fact]
    public void AGamepadTurnsSplitsAndDropsAStack()
    {
        using var app = Boot(Pictures);
        var world = app.World;
        var hero = Carrier(world, "hero", 0f, ("sword", 1), ("bread", 5));
        var bag = Open(app, RpgKitModule.InventoryScreen, hero);
        var view = Assert.IsType<InventoryView>(bag.Screen.ViewModel);

        // Turned where it lies, the sword would cover the bread: refused, and said why.
        Assert.Same(view.Bag.CellAt(0, 0), bag.Focused);
        bag.Press(UiInput.Do(UiCommand.Rotate));
        Assert.Equal("There is no room to turn sword there.", view.Message);
        Assert.False(view.Bag.ItemAt(0, 0)!.Rotated);

        // In the hand it turns, and goes down three across on the fourth row. While it is held its squares
        // come apart, so the D-pad walks them; where it would go is shown under focus.
        bag.A();
        Assert.Equal(Id("sword"), view.Held!.Item);
        bag.Press(UiInput.Do(UiCommand.Rotate));
        Assert.True(view.HeldTurned);
        bag.Nav(UiNavigation.Down, 3);
        Assert.Same(view.Bag.CellAt(0, 3), bag.Focused);
        Assert.Equal(GridCell.TargetStyle, view.Bag.CellAt(2, 3)!.Style);
        bag.A();
        var sword = view.Bag.ItemAt(0, 3)!;
        Assert.True(sword.Rotated);
        Assert.Same(sword, view.Bag.ItemAt(2, 3));
        Assert.Null(view.Bag.ItemAt(0, 0));
        // Turned back where it lies, it stands up again: three squares down from (0, 3).
        Assert.Same(sword, ((GridCell)bag.Focused!).Item);
        bag.Press(UiInput.Do(UiCommand.Rotate));
        Assert.Equal("", view.Message);
        Assert.Equal((1, 3), (view.Bag.ItemAt(0, 5)!.Width, view.Bag.ItemAt(0, 5)!.Height));

        // The bread halved: 3 stays where it was, 2 more in the first free square, and still 5 carried.
        var cells = bag.Screen.View.Find<Grid>("cells")!;
        Assert.True(bag.Root.Focus(CellWidget(cells, view.Bag.CellAt(1, 0)!)));
        bag.Press(UiInput.Do(UiCommand.Split));
        Assert.Equal("", view.Message);
        Assert.Equal(5, world.CountOf(hero, Id("bread")));
        var halves = view.Bag.Items.Where(i => i.Item == Id("bread")).ToArray();
        Assert.Equal(new[] { 3, 2 }, halves.Select(h => h.Count));
        Assert.Equal((0, 0), (halves[1].X, halves[1].Y));
        Assert.Equal(2, world.Get<Inventory>(hero).Items.Count(s => s.Item == Id("bread")));
        bag.Press(UiInput.Do(UiCommand.Split));                                             // and 3 again: 2 and 1
        Assert.Equal(new[] { 2, 1, 2 }, view.Bag.Items.Where(i => i.Item == Id("bread")).Select(h => h.Count));   // the new half after its own
        Assert.Equal((2, 0), (view.Bag.Items[2].X, view.Bag.Items[2].Y));

        // X drops the focused stack on the ground.
        var focused = Assert.IsType<GridCell>(bag.Focused);
        int count = focused.Item!.Count;
        bag.Press(UiInput.Do(UiCommand.Alternate));
        Assert.Equal(5 - count, world.CountOf(hero, Id("bread")));
        Assert.Single(world.Query<Pickup>().Entities.ToArray());

        // Looting, the corpse's stacks are not the hero's to drop.
        var bandit = Carrier(world, "bandit", 0f, ("coin", 3));
        var loot = Open(app, RpgKitModule.LootScreen, hero, bandit);
        var lootView = Assert.IsType<LootView>(loot.Screen.ViewModel);
        Assert.Same(lootView.Container.CellAt(0, 0), loot.Focused);
        loot.Press(UiInput.Do(UiCommand.Alternate));
        Assert.Equal("coin ×3 cannot be dropped here.", lootView.Message);
        Assert.Equal(3, world.CountOf(bandit, Id("coin")));
    }
}

// Every RPG screen open and read each frame, with nothing changing: no allocation (02 §4.6).
[Collection(MeasurementsCollection.Name)]
public class RpgScreenAllocationTests
{
    public RpgScreenAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void RefreshingTheRpgScreensAllocatesNothing()
    {
        using var app = RpgScreenTests.Boot();
        var world = app.World;
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddInventory(hero, 30f);
        world.Give(hero, new RecordId("sage", "sword"), 1);
        world.Give(hero, new RecordId("sage", "coin"), 12);
        var bandit = world.Create(Transform.At(Vector3.Zero), "bandit");
        world.AddInventory(bandit);
        world.Give(bandit, new RecordId("sage", "bread"), 2);
        var guard = world.Create(Transform.At(Vector3.Zero), "guard");
        Assert.True(world.Systems.Disable("sage.physics.step"));   // Bepu's own bytes a tick are not the screens'
        WorldClock.Of(world).Scale = 0;   // the rest screen says the time again when the minute turns, and only then

        // All six on the world's screen stack (#97), the topics screen on top taking the (idle) input.
        var stack = world.Resources.Get<UiScreenStack>();
        stack.Open(RpgKitModule.InventoryScreen, new UiBindContext(world, hero));
        stack.Open(RpgKitModule.EquipmentScreen, new UiBindContext(world, hero));
        stack.Open(RpgKitModule.LootScreen, new UiBindContext(world, hero, bandit));
        stack.Open(RpgKitModule.RestScreen, new UiBindContext(world, hero));   // the rest screen (4g-7): its rule asked every frame
        stack.Open(RpgKitModule.ModsScreen, new UiBindContext(world));   // the mods screen (4j-6): rows made on opening, not per frame
        stack.Open(RpgKitModule.TopicsScreen, new UiBindContext(world, hero, guard));

        void Frame()
        {
            world.RunFixed(1f / 60f);
            stack.Update(UiInput.Wait(1f / 60f));   // every layer's view-model read, bound and laid out
            Profiler.EndFrame();
        }
        for (int i = 0; i < 30; i++) Frame();   // warm: readers compiled, rows made, the fades done

        AllocationProbe.AssertNone(300, Frame);
    }
}
