#nullable enable
using System;
using Sage.UI;

namespace Sage.Kits.Rpg;

// One of the RPG kit's screens (issue #99); the pattern they share is at the top of InventoryView.cs.

// What a trade is worth (issue #99): the price of `count` of an item, bought from a shop (`buying`) or
// sold to it, for a trader who is no `merchant`. A merchant (issue #380, the base's Merchants) has the real
// rule — value, markup, the customer's bargaining attribute and standing — and money changes hands.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#99): SAGE0125, as Sage.UI
public interface IPriceRule
{
    int Price(World world, RecordId item, int count, bool buying);
}

// The stub: an item record's `value` a piece to buy (at least 1), half of it to sell. What a trader with no
// `merchant` record (no purse, no currency) is priced by, into the balance.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#99): SAGE0125, as Sage.UI
public sealed class StubPriceRule : IPriceRule
{
    public static readonly StubPriceRule Instance = new();

    public int Price(World world, RecordId item, int count, bool buying)
    {
        int value = world.Records().TryGet(item, out ItemRecord record) ? Math.Max(record.Value, 1) : 1;
        int each = buying ? value : Math.Max(value / 2, 1);
        return each * Math.Max(count, 0);
    }
}

// A shop (#99, money since #380): the merchant's goods (Other) beside your bag (Subject), and the hand
// between them — ItemGridView's moves. What moving the held stack would cost or fetch is shown while it is
// held.
// - **A merchant** (the base's `merchant` part, issue #380): putting a stack down on the other side is a
//   trade (Merchants.Buy / Sell): the stack and its price change hands together, or neither does and
//   Message says why (too poor, they are too poor, not something they buy). Money and their purse are
//   shown; opening the shop restocks them when it is time (Merchants.Ready).
// - **Anybody else** (an NPC with only an inventory): the shell — each move across is priced by Prices
//   (the stub) and added to Balance, what you owe the merchant less what they owe you; no money moves.
//   screen rpg:shop — layout rpg:shop
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#99): SAGE0125, as Sage.UI
[ViewModel("rpg_shop")]
public sealed class ShopView : ItemGridView
{
    private Entity _named;
    private GridItem? _priced;
    private int _traded;   // what the last trade cost or fetched

    public ShopView() : this(new ItemGrid(), new ItemGrid()) { }

    private ShopView(ItemGrid stock, ItemGrid bag) : base(stock, bag)
    {
        Stock = stock;
        Bag = bag;
    }

    public ItemGrid Stock { get; }
    public ItemGrid Bag { get; }

    public IPriceRule Prices { get; set; } = StubPriceRule.Instance;

    // Who is selling, by name.
    public string MerchantName { get; private set; } = "";

    // The other side is a `merchant` (issue #380): trades move money. False: the shell's balance.
    public bool IsMerchant { get; private set; }
    public bool KeepsBalance => !IsMerchant;

    // The customer's money in the merchant's currency, and the merchant's purse (0 when not a merchant).
    public int Money { get; private set; }
    public int MerchantGold { get; private set; }

    // What the trades so far come to: positive, you owe the merchant (or paid them); negative, they owe (or paid) you.
    public int Balance { get; private set; }

    // What the stack in the hand costs (from the stock) or fetches (from the bag); 0 with nothing held.
    public int HeldPrice { get; private set; }
    public bool HeldIsStock => Held != null && Held.Grid == Stock;
    public bool HeldIsBag => Held != null && Held.Grid == Bag;

    public override void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        if (context.Other != _named)
        {
            _named = context.Other;
            MerchantName = world.IsAlive(context.Other) ? context.Other.Name ?? "" : "";
            IsMerchant = world.IsAlive(context.Other) && world.Has<Merchant>(context.Other);
            if (IsMerchant) Merchants.Ready(world, context.Other);   // the shop opens restocked when it is time
            _priced = null;
            HeldPrice = 0;
        }
        base.Refresh(in context);
        Money = IsMerchant ? Merchants.MoneyOf(world, context.Subject, context.Other) : 0;
        MerchantGold = IsMerchant ? Merchants.GoldOf(world, context.Other) : 0;
        if (Held != _priced)
        {
            _priced = Held;
            HeldPrice = Held == null ? 0 : PriceOf(world, Held, in context);
        }
    }

    private int PriceOf(World world, GridItem held, in UiBindContext context)
    {
        bool buying = held.Grid == Stock;
        if (!IsMerchant) return Prices.Price(world, held.Item, held.Count, buying);
        int at = held.Grid.StackOf(world, held);
        var owner = buying ? context.Other : context.Subject;
        var stack = at >= 0 ? world.Get<Inventory>(owner).Items[at] : new ItemStack { Item = held.Item, Count = held.Count };
        return Merchants.Price(world, context.Subject, context.Other, in stack, held.Count, buying);
    }

    protected override Entity OwnerOf(int grid, in UiBindContext context) => grid == 0 ? context.Other : context.Subject;

    public override bool Activate(Widget widget, in UiBindContext context) => base.Activate(widget, in context);

    // A stack put down on the other side of a merchant's shop is a trade: Merchants.Buy from the stock,
    // Sell from the bag, its refusal said in the kit's words.
    private protected override bool Transfer(World world, GridItem held, ItemGrid to, int x, int y, bool rotated, out string reason)
    {
        if (!IsMerchant || !world.IsAlive(Stock.Owner) || !world.Has<Merchant>(Stock.Owner))
            return base.Transfer(world, held, to, x, y, rotated, out reason);
        var label = held.Label;
        return ItemGrid.Transfer(world, held, to, x, y, rotated, Trade, out reason);

        bool Trade(World w, Entity from, int index, Entity into, int count, out string why)
        {
            bool buying = from == Stock.Owner;
            var result = buying ? Merchants.Buy(w, into, from, index, count) : Merchants.Sell(w, from, into, index, count);
            _traded = result.Price;
            why = result.Ok ? "" : Refusal(w, result, label, buying ? into : from, buying ? from : into);
            return result.Ok;
        }
    }

    private static string Refusal(World world, in TradeResult result, string item, Entity customer, Entity merchant)
    {
        var text = RpgText.Of(world);
        string who = world.IsAlive(merchant) ? merchant.Name ?? "" : "";
        return result.Refusal switch
        {
            TradeRefusal.CannotAfford => text.Format("@rpg.shop.too_poor", ("item", item), ("price", result.Price),
                                                     ("money", Merchants.MoneyOf(world, customer, merchant))),
            TradeRefusal.MerchantCannotAfford => text.Format("@rpg.shop.merchant_too_poor", ("who", who), ("item", item),
                                                             ("price", result.Price), ("gold", Merchants.GoldOf(world, merchant))),
            TradeRefusal.NotBought => text.Format("@rpg.shop.not_bought", ("who", who), ("item", item)),
            TradeRefusal.TooHeavy => text.Format("@rpg.shop.too_heavy", ("item", item)),
            _ => text.Format("@rpg.grid.gone", ("item", item)),
        };
    }

    // Putting a held stack down on the other side is a trade — by the hand or by the pointer: priced (or
    // paid, at a merchant's), and added to the balance.
    protected override void Moved(World world, RecordId item, int count, ItemGrid from, ItemGrid to)
    {
        bool buying = from == Stock;
        int price = IsMerchant ? _traded : Prices.Price(world, item, count, buying);
        Balance += buying ? price : -price;
    }
}
