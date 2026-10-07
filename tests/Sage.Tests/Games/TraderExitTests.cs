#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 4f's trader (issue #380): real shops and barter, in data. tests/games/trader has no C#: rubles are an
// item, Sidorovich is a `merchant` record (1500 rubles to buy with, markup 1.5, pays half, buys artifacts,
// weapons and medicine, a loot table for goods, a restock every 24 game hours) on a prefab with the
// `merchant` part, and the price moves with the player's `barter` attribute and their standing with the
// loners. The player trades through the RPG kit's shop screen (ShopView), driven by a gamepad, as
// RpgScreenTests does; the rest through the base's Merchants.
public class TraderExitTests
{
    public TraderExitTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("bunker", name);
    private static readonly RecordId Rubles = Id("rubles"), Medkit = Id("medkit"), Sausage = Id("sausage"), Ak = Id("ak74"),
        Jellyfish = Id("jellyfish"), Pistol = Id("pistol"), FleshEye = Id("flesh_eye"), Loners = Id("loners");

    private static string GameDirectory => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "trader");

    private static HeadlessApp Boot(string? saves = null)
    {
        var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        app.Engine.Saves.Root = saves ?? Path.Combine(TestEnv.NewTempDir(), "saves");
        WorldClock.Of(app.World).Scale = 0;                       // game time passes when the test says so
        Step(app.World, 3);
        return app;
    }

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity Trader(World world) =>
        Assert.Single(world.QueryAll().Entities.ToEntityList(), e => e.Name == "Sidorovich");

    private static int IndexOf(World world, Entity owner, RecordId item) =>
        world.Get<Inventory>(owner).Items.FindIndex(s => s.Item == item);

    private static RpgScreenTests.Pad Shop(HeadlessApp app, Entity player, Entity trader)
    {
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.CloseAll();
        return new RpgScreenTests.Pad(stack, stack.Open(new RecordId("rpg", "shop"), new UiBindContext(app.World, player, trader)));
    }

    private static string Text(RpgScreenTests.Pad pad, string node) => pad.Screen.View.Find<Label>(node)!.Text;

    // Picks up the stack of `item` on `from` and puts it down on an empty square of the other grid: a buy
    // from the stock, a sale from the bag.
    private static void Move(RpgScreenTests.Pad shop, ShopView view, ItemGrid from, RecordId item)
    {
        var to = from == view.Stock ? view.Bag : view.Stock;
        bool On(RpgScreenTests.Pad p) => p.Focused is GridCell c && c.Grid == from && c.Item?.Item == item;
        for (int i = 0; i < 20 && !On(shop); i++) shop.Nav(UiNavigation.Left);    // the stock is left of the bag
        shop.NavUntil(UiNavigation.Right, On);
        shop.A();                                                                   // pick it up
        Assert.Equal(item, view.Held!.Item);
        shop.NavUntil(to == view.Bag ? UiNavigation.Right : UiNavigation.Left, p => p.Focused is GridCell c && c.Grid == to && c.Item == null);
        shop.A();                                                                   // put it down on the other side
    }

    // Acceptance: buy and sell move the stack and its price together, three refusals move nothing and say
    // why, a day later the trader has restocked, and a save in between keeps money, goods and purse.
    [Xunit.Fact]
    public void TraderExit_BuysSellsRefusesWithAReason_RestocksAfterADay_AndKeepsItAllAcrossASave()
    {
        string saves = TestEnv.NewTempDir();
        PersistentId traderId;
        double restockAt;
        using (var app = Boot(saves))
        {
            var world = app.World;
            var player = Player(world);
            var trader = Trader(world);
            traderId = world.Get<Persistent>(trader).Id;
            var traded = new EventProbe<Traded>(world);

            // His goods are the loot table, his purse the record's; the player's money is their rubles.
            Assert.Equal((3, 5, 1), (world.CountOf(trader, Medkit), world.CountOf(trader, Sausage), world.CountOf(trader, Ak)));
            Assert.Equal(1500, Merchants.GoldOf(world, trader));
            Assert.Equal(500, Merchants.MoneyOf(world, player, trader));

            var shop = Shop(app, player, trader);
            var view = Assert.IsType<ShopView>(shop.Screen.ViewModel);
            Assert.True(view.IsMerchant);
            Assert.Equal("Your money 500    Sidorovich has 1500", Text(shop, "money"));
            Assert.False(shop.Screen.View.Find<Label>("balance")!.Visible);             // the shell's tally is not shown

            // Buy: three medkits at 150 (value 100, markup 1.5): the stack and 450 rubles change hands.
            Move(shop, view, view.Stock, Medkit);
            Assert.Equal("", view.Message);
            Assert.Equal((3, 0), (world.CountOf(player, Medkit), world.CountOf(trader, Medkit)));
            Assert.Equal((50, 1950), (Merchants.MoneyOf(world, player, trader), Merchants.GoldOf(world, trader)));
            Assert.Equal("Your money 50    Sidorovich has 1950", Text(shop, "money"));

            // Too poor: the AK-74 costs 1500; nothing moves.
            Move(shop, view, view.Stock, Ak);
            Assert.Equal("You cannot afford AK-74: it costs 1500 and you have 50.", view.Message);
            Assert.Equal((0, 1), (world.CountOf(player, Ak), world.CountOf(trader, Ak)));
            Assert.Equal(50, Merchants.MoneyOf(world, player, trader));

            // Sell: a Jellyfish fetches 1000 (half its value).
            Move(shop, view, view.Bag, Jellyfish);
            Assert.Equal("", view.Message);
            Assert.Equal((1, 1), (world.CountOf(player, Jellyfish), world.CountOf(trader, Jellyfish)));
            Assert.Equal((1050, 950), (Merchants.MoneyOf(world, player, trader), Merchants.GoldOf(world, trader)));

            // The merchant out of gold: the second Jellyfish would fetch 1000 and he has 950.
            Move(shop, view, view.Bag, Jellyfish);
            Assert.Equal("Sidorovich cannot afford Jellyfish: it fetches 1000 and they have 950.", view.Message);
            Assert.Equal(1, world.CountOf(player, Jellyfish));
            Assert.Equal((1050, 950), (Merchants.MoneyOf(world, player, trader), Merchants.GoldOf(world, trader)));

            // Not something he buys: a flesh eye is a mutant part.
            Move(shop, view, view.Bag, FleshEye);
            Assert.Equal("Sidorovich does not buy flesh eye ×2.", view.Message);
            Assert.Equal((2, 0), (world.CountOf(player, FleshEye), world.CountOf(trader, FleshEye)));
            Assert.Equal(450 - 1000, view.Balance);                                     // paid 450, was paid 1000
            app.World.Resources.Get<UiScreenStack>().CloseAll();

            // An instance changes hands intact (issue #383): Strelok's worn pistol sells for half of half its
            // value, lies in his goods with its name and condition, and buys back for its price there.
            int pistol = IndexOf(world, player, Pistol);
            Assert.Equal(75, Merchants.Price(world, player, trader, world.Get<Inventory>(player).Items[pistol], 1, buying: false));
            Assert.True(Merchants.Sell(world, player, trader, pistol, 1).Ok);
            var sold = world.Get<Inventory>(trader).Items[IndexOf(world, trader, Pistol)];
            Assert.Equal(("Strelok's PM", 0.5f), (sold.Instance!.Name, sold.Instance.Condition));
            Assert.Equal((1125, 875), (Merchants.MoneyOf(world, player, trader), Merchants.GoldOf(world, trader)));
            var back = Merchants.Buy(world, player, trader, IndexOf(world, trader, Pistol), 1);
            Assert.Equal((true, 225), (back.Ok, back.Price));
            Assert.Equal("Strelok's PM", world.Get<Inventory>(player).Items[IndexOf(world, player, Pistol)].Instance!.Name);
            Assert.Equal((900, 1100), (Merchants.MoneyOf(world, player, trader), Merchants.GoldOf(world, trader)));

            Step(world, 2);
            Assert.Equal(new[] { (Medkit, 450, true), (Jellyfish, 1000, false), (Pistol, 75, false), (Pistol, 225, true) },
                         traded.All.Select(t => (t.Item, t.Price, t.Buying)));
            restockAt = world.Get<Merchant>(trader).RestockAt;
            Assert.True(app.Engine.Saves.Save("bunker"));
        }

        // Loaded in a new process's worth of engine: the money, his purse, his goods and his restock time.
        using var again = Boot(saves);
        Assert.True(again.Engine.Saves.Load("bunker"));
        var w = again.World;
        Step(w, 2);
        var stalker = Player(w);
        var sidorovich = w.Resolve(traderId);
        Assert.Equal("Sidorovich", sidorovich.Name);
        Assert.Equal((900, 1100), (Merchants.MoneyOf(w, stalker, sidorovich), Merchants.GoldOf(w, sidorovich)));
        Assert.Equal((3, 1, 1), (w.CountOf(stalker, Medkit), w.CountOf(stalker, Jellyfish), w.CountOf(stalker, Pistol)));
        Assert.Equal((0, 5, 1, 1), (w.CountOf(sidorovich, Medkit), w.CountOf(sidorovich, Sausage), w.CountOf(sidorovich, Ak),
                                    w.CountOf(sidorovich, Jellyfish)));
        Assert.Equal(restockAt, w.Get<Merchant>(sidorovich).RestockAt);

        // Not yet a day: opening the shop changes nothing. A day on: his purse and goods are as they were at
        // the start, and what the player sold him is gone; the player keeps what they bought.
        var clock = WorldClock.Of(w);
        clock.Scale = 0;
        clock.Hour = 23;
        var shop2 = Shop(again, stalker, sidorovich);
        Assert.Equal(1100, Assert.IsType<ShopView>(shop2.Screen.ViewModel).MerchantGold);
        clock.Day += 1;
        shop2 = Shop(again, stalker, sidorovich);
        var restocked = Assert.IsType<ShopView>(shop2.Screen.ViewModel);
        Assert.Equal(1500, restocked.MerchantGold);
        Assert.Equal((3, 5, 1, 0), (w.CountOf(sidorovich, Medkit), w.CountOf(sidorovich, Sausage), w.CountOf(sidorovich, Ak),
                                    w.CountOf(sidorovich, Jellyfish)));
        Assert.Equal(3, w.CountOf(stalker, Medkit));
        Assert.Equal(clock.Elapsed + 24, w.Get<Merchant>(sidorovich).RestockAt, 3);
    }

    // The price rule: value × markup to buy, × what he pays to sell, moved in the player's favour by their
    // `barter` attribute (a percent a point) and their standing with the loners (a fifth of one a point),
    // never more than half either way, and selling never fetches more than buying costs.
    [Xunit.Fact]
    public void TraderExit_PricesMoveWithTheBarterAttributeAndStanding()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);
        var trader = Trader(world);
        int Buy() => Merchants.Price(world, player, trader, Medkit, 1, buying: true);
        int Sell() => Merchants.Price(world, player, trader, Jellyfish, 1, buying: false);

        Assert.Equal((150, 1000), (Buy(), Sell()));

        SetBarter(world, player, 20);                       // 20 % in the player's favour
        Assert.Equal((120, 1200), (Buy(), Sell()));
        Factions.Change(world, Loners, 50);                 // and 10 % more
        Assert.Equal(0.3f, Merchants.Advantage(world, player, trader), 3);
        Assert.Equal((105, 1300), (Buy(), Sell()));

        SetBarter(world, player, 100);                      // 120 %: held at half
        Assert.Equal(0.5f, Merchants.Advantage(world, player, trader), 3);
        Assert.Equal((75, 1500), (Buy(), Sell()));

        SetBarter(world, player, 0);
        Factions.Change(world, Loners, -150);               // hated: 20 % against
        Assert.Equal((180, 800), (Buy(), Sell()));

        // A trade asks the same rule.
        var bought = Merchants.Buy(world, player, trader, IndexOf(world, trader, Sausage), 1);
        Assert.Equal((true, 36), (bought.Ok, bought.Price));   // 20 × 1.5 × 1.2
        Assert.Equal(464, Merchants.MoneyOf(world, player, trader));
    }

    // Refusals from the base, with their reasons, and nothing moved: not a merchant, not there, too heavy.
    [Xunit.Fact]
    public void TraderExit_ATradeThatCannotHappenMovesNothing()
    {
        using var app = Boot();
        var world = app.World;
        var player = Player(world);
        var trader = Trader(world);

        Assert.Equal(TradeRefusal.NotAMerchant, Merchants.Buy(world, trader, player, 0, 1).Refusal);
        Assert.Equal(TradeRefusal.Gone, Merchants.Buy(world, player, trader, 99, 1).Refusal);
        Assert.Equal(TradeRefusal.Gone, Merchants.Sell(world, player, trader, IndexOf(world, player, FleshEye), 3).Refusal);
        Assert.Equal(TradeRefusal.NotBought, Merchants.Sell(world, player, trader, IndexOf(world, player, Rubles), 10).Refusal);

        // Too heavy: the player can carry 50 kg; fill the bag to the brim and the AK will not go in.
        world.Get<Inventory>(player).Capacity = world.WeightOf(player) + 1f;
        Assert.True(world.Give(player, Rubles, 5000));
        var heavy = Merchants.Buy(world, player, trader, IndexOf(world, trader, Ak), 1);
        Assert.Equal(TradeRefusal.TooHeavy, heavy.Refusal);
        Assert.Contains("cannot carry", heavy.Reason);
        Assert.Equal((5500, 1500, 1), (Merchants.MoneyOf(world, player, trader), Merchants.GoldOf(world, trader), world.CountOf(trader, Ak)));
    }

    // A merchant record is checked as it loads: it needs a currency, and may not pay more than it charges.
    [Xunit.Fact]
    public void AMerchantWithoutACurrencyOrThatPaysMoreThanItChargesIsALoadError()
    {
        using var app = HeadlessApp.Gameplay().File("data/shops.json", """
            [{ "type": "item", "id": "gold", "maxStack": 1000, "weight": 0 },
             { "type": "merchant", "id": "fine", "currency": "gold", "gold": 10 },
             { "type": "merchant", "id": "penniless" },
             { "type": "merchant", "id": "pump", "currency": "gold", "markup": 1, "pays": 2 }]
            """).Boot("shops");
        Assert.Equal(2, app.Records.ErrorCount);
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("penniless") && e.Contains("currency"));
        Assert.Contains(app.Records.LoadErrors, e => e.Contains("pump") && e.Contains("money pump"));
    }

    private static void SetBarter(World world, Entity player, float value)
    {
        var registries = world.Resources.Get<GameplayRegistries>();
        world.Get<Attributes>(player).Values.SetBase(registries.Attribute(Id("barter")), value);
        Assert.Equal(value, world.Attribute(player, Id("barter")));
    }
}
