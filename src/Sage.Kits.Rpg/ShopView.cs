#nullable enable
using System;
using Sage.UI;

namespace Sage.Kits.Rpg;

// One of the RPG kit's screens (issue #99); the pattern they share is at the top of InventoryView.cs.

// What a trade is worth (issue #99): the price of `count` of an item, bought from a shop (`buying`) or
// sold to it. A *shell*: real barter — haggling, disposition, a merchant's gold — is phase 4f, which
// replaces this rule; the shop screen only asks it.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#99): SAGE0125, as Sage.UI
public interface IPriceRule
{
    int Price(World world, RecordId item, int count, bool buying);
}

// The stub: an item record's `value` a piece to buy (at least 1), half of it to sell.
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

// A shop (the shell of #99): the merchant's goods (Other) beside your bag (Subject), and the hand
// between them — ItemGridView's moves, so a stack goes across with ItemGrid.Transfer and its weight
// rules. What moving it would cost is shown while it is held, and each move across is priced by
// Prices (the stub, until 4f) and added to Balance: what you owe the merchant, less what they owe you.
// No money changes hands yet.
//   screen rpg:shop — layout rpg:shop
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#99): SAGE0125, as Sage.UI
[ViewModel("rpg_shop")]
public sealed class ShopView : ItemGridView
{
    private Entity _named;
    private GridItem? _priced;

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

    // What the trades so far come to: positive, you owe the merchant; negative, they owe you.
    public int Balance { get; private set; }

    // What the stack in the hand costs (from the stock) or fetches (from the bag); 0 with nothing held.
    public int HeldPrice { get; private set; }
    public bool HeldIsStock => Held != null && Held.Grid == Stock;

    public override void Refresh(in UiBindContext context)
    {
        base.Refresh(in context);
        if (context.World is not { } world) return;
        if (context.Other != _named)
        {
            _named = context.Other;
            MerchantName = world.IsAlive(context.Other) ? context.Other.Name ?? "" : "";
        }
        if (Held != _priced)
        {
            _priced = Held;
            HeldPrice = Held == null ? 0 : Prices.Price(world, Held.Item, Held.Count, buying: Held.Grid == Stock);
        }
    }

    protected override Entity OwnerOf(int grid, in UiBindContext context) => grid == 0 ? context.Other : context.Subject;

    public override bool Activate(Widget widget, in UiBindContext context) => base.Activate(widget, in context);

    // Putting a held stack down on the other side is a trade — by the hand or by the pointer: priced,
    // and added to the balance.
    protected override void Moved(World world, RecordId item, int count, ItemGrid from, ItemGrid to)
    {
        bool buying = from == Stock;
        Balance += buying ? Prices.Price(world, item, count, buying: true) : -Prices.Price(world, item, count, buying: false);
    }
}
