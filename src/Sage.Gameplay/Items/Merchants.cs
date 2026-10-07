#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Shops and barter (REDESIGN §5 4f, issue #380): somebody who sells for money and buys for it, as data.
//
//   { "type": "item", "id": "rubles", "label": "rubles", "weight": 0, "maxStack": 1000000, "category": "money" },
//   { "type": "merchant", "id": "sidorovich", "currency": "rubles", "gold": 1500, "markup": 1.5, "pays": 0.5,
//     "buys": ["artifact", "weapon"], "stock": "bunker_stock", "restock": 24,
//     "skill": "barter", "skillEffect": 0.01, "dispositionEffect": 0.002 }
//   ... "parts": { "inventory": { ... }, "merchant": "sidorovich" }
//
// - **Money is an item** (`currency`): the customer pays with the units of it they carry, and is paid in
//   them. The merchant's own purse is a number on the merchant (`Merchant.Gold`), Morrowind's way, so it
//   is never stock for sale and refills on its own.
// - **What they sell is their inventory** (an `inventory` part, a `stock` loot table rolled as they spawn,
//   or both); **what they buy** is the item categories in `buys` (an item's `category`), everything but
//   money when it is empty.
// - **The price rule** (Merchants.Price): an item's value (times an instance's condition, #383) times
//   `markup` to buy, times `pays` to sell, each moved in the customer's favour by their bargaining
//   attribute (`skill`, any attribute: #377's skill ranks are attributes) times `skillEffect`, and by the
//   player's standing with the merchant's faction times `dispositionEffect` — never more than
//   `maxAdvantage` either way, and never so far that selling fetches more than buying costs.
// - **A trade is atomic** (Merchants.Buy / Sell): the stack moves (instance and all, Items.MoveTo) and the
//   money with it, or nothing moves and the result says why — too poor, the merchant too poor, not
//   something they buy, too heavy, gone.
// - **Restock** (`restock`, game hours by the world clock): when that time has passed since they last
//   restocked (or spawned), the next trade or shop finds their gold back to `gold` and their goods as they
//   were — the inventory they spawned with, and `stock` rolled again. What the player sold them is gone.
//
// The merchant's purse, goods and restock time are components of the merchant, so a save keeps them.

[Record("merchant", Plugin = "sage.gameplay.items")]
public sealed class MerchantRecord
{
    [Property(Tooltip = "The item that is money here: what the customer pays with and is paid in")]
    public RecordRef<ItemRecord> Currency;
    [Property(Min = 0, Tooltip = "The merchant's purse, as they spawn and after every restock: what they can buy with")]
    public int Gold;
    [Property(Min = 0, Tooltip = "What they charge, times an item's value: 1.5 sells a 10 for 15")]
    public float Markup = 1.5f;
    [Property(Min = 0, Tooltip = "What they pay for an item, times its value: 0.5 buys a 10 for 5")]
    public float Pays = 0.5f;
    [Property(Tooltip = "The item categories they buy (an item's `category`); empty: everything but money")]
    public List<string> Buys = new();
    [Property(Tooltip = "A loot table rolled into their goods as they spawn and at every restock")]
    public RecordRef<LootTableRecord> Stock;
    [Property(Min = 0, Unit = "h", Tooltip = "Game hours between restocks (gold and goods back to what they were); 0 = never")]
    public float Restock;
    [Property(Tooltip = "The customer's attribute that bargains: a barter or mercantile skill; empty: none")]
    public RecordRef<AttributeRecord> Skill;
    [Property(Tooltip = "How much one point of that attribute moves a price in the customer's favour: 0.01 is a percent")]
    public float SkillEffect = 0.005f;
    [Property(Tooltip = "How much one point of the player's standing with the merchant's faction (-100 to 100) moves a price")]
    public float DispositionEffect = 0.002f;
    [Property(Min = 0, Max = 0.95f, Tooltip = "The most skill and standing together move a price, either way: 0.5 is half")]
    public float MaxAdvantage = 0.5f;

    // Whether they buy an item of this category: one of `buys`, or anything at all when it is empty.
    public bool BuysCategory(string? category)
    {
        if (Buys.Count == 0) return true;
        foreach (var buys in Buys)
            if (string.Equals(buys, category ?? "", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

// Somebody who trades: the `merchant` part adds it.
[Component("sage:merchant")]
public struct Merchant : IComponent
{
    [RecordRef("merchant"), Property(Tooltip = "Their prices, purse, what they buy and when they restock")]
    public RecordId Record;
    [Property(Min = 0, Tooltip = "What they have to buy with now")]
    public int Gold;
    [Property(Unit = "h", Tooltip = "World-clock hours at which they next restock; 0 = never")]
    public double RestockAt;

    public List<ItemStack>? Stock;   // the goods they spawned with, before `stock` was rolled: what a restock puts back
}

// Why a trade did not happen.
public enum TradeRefusal
{
    None,
    NotAMerchant,      // the other side has no `merchant`
    Gone,              // the stack is not there (any more), or not that many
    CannotAfford,      // the customer has too little money
    MerchantCannotAfford,   // the merchant's purse is too small
    NotBought,         // not a category they buy, or money itself
    TooHeavy,          // whoever receives it cannot carry it (or the money)
}

// What a trade did: the price, or why it was refused.
public readonly record struct TradeResult(TradeRefusal Refusal, int Price, string Reason)
{
    public bool Ok => Refusal == TradeRefusal.None;
}

// A trade happened: `Buying` the customer bought `Count` of `Item` from the merchant for `Price`, otherwise
// sold them to the merchant. For a barter skill to rise by (#377), a quest or a HUD.
[GameEvent]
public readonly record struct Traded(Entity Customer, Entity Merchant, RecordId Item, int Count, int Price, bool Buying);

// "merchant": "sidorovich" — sells and buys by that record. After `inventory`, whose items are the goods it
// restocks to, and `loot`, so a table rolled as it spawns is goods too.
[PrefabPart("merchant", Plugin = "sage.gameplay.items", After = new[] { "inventory", "loot" }, Shorthand = nameof(Record))]
public sealed class MerchantPart : IPrefabPart
{
    [Property(Tooltip = "The merchant record: currency, purse, prices, what they buy, restock")]
    public RecordRef<MerchantRecord> Record;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Record.IsEmpty) { ctx.Error("needs a merchant record"); return; }
        var world = ctx.World;
        world.AddInventory(ctx.Entity);
        if (!world.Records().TryGet(Record.Id, out MerchantRecord record)) { ctx.Error($"no merchant record {Record.Id}"); return; }
        var stock = new List<ItemStack>();
        if (world.Get<Inventory>(ctx.Entity).Items is { } items)
            foreach (var stack in items) stock.Add(Copy(stack));
        if (!record.Stock.IsEmpty) LootTables.Roll(world, ctx.Entity, record.Stock);
        world.Add(ctx.Entity, new Merchant
        {
            Record = Record.Id,
            Gold = record.Gold,
            RestockAt = record.Restock > 0f ? Merchants.Now(world) + record.Restock : 0.0,
            Stock = stock,
        });
    }

    internal static ItemStack Copy(in ItemStack stack) =>
        new() { Item = stack.Item, Count = stack.Count, Instance = stack.Instance?.Clone() };
}

public static class Merchants
{
    // The merchant's record, or null for something that is not one.
    public static MerchantRecord? RecordOf(World world, Entity merchant) =>
        world.IsAlive(merchant) && world.TryGet<Merchant>(merchant, out var m) && world.Records().TryGet(m.Record, out MerchantRecord record)
            ? record : null;

    // What the merchant has to buy with now; 0 for something that is not one.
    public static int GoldOf(World world, Entity merchant) =>
        world.IsAlive(merchant) && world.TryGet<Merchant>(merchant, out var m) ? m.Gold : 0;

    // How much money `customer` carries in the merchant's currency.
    public static int MoneyOf(World world, Entity customer, Entity merchant) =>
        RecordOf(world, merchant) is { } record && !record.Currency.IsEmpty ? world.CountOf(customer, record.Currency) : 0;

    // Restocks a merchant whose time has come: gold back to the record's, goods back to what they spawned
    // with and the `stock` table rolled again, and the next restock `restock` hours from now. Every trade
    // and the shop screen ask it first. True when it restocked.
    public static bool Ready(World world, Entity merchant)
    {
        if (RecordOf(world, merchant) is not { } record) return false;
        ref var m = ref world.Get<Merchant>(merchant);
        if (record.Restock <= 0f || m.RestockAt <= 0.0 || Now(world) < m.RestockAt) return false;

        var items = world.Get<Inventory>(merchant).Items ??= new List<ItemStack>();
        world.TryGet<Equipment>(merchant, out var equipment);
        for (int i = items.Count - 1; i >= 0; i--)
            if (!Worn(equipment, items[i])) items.RemoveAt(i);
        var restock = m.Stock;
        m.Gold = record.Gold;
        m.RestockAt = Now(world) + record.Restock;
        if (restock != null)
            foreach (var stack in restock) world.Give(merchant, stack.Item, stack.Instance, stack.Count);
        if (!record.Stock.IsEmpty) LootTables.Roll(world, merchant, record.Stock);
        Log.Info(LogCat.Gameplay, $"{World.Describe(merchant)} restocks");
        return true;
    }

    // What `count` of the stack would cost `customer` to buy from `merchant` (`buying`), or fetch sold to
    // them. The item's value (times an instance's condition), times the merchant's markup or what they pay,
    // moved in the customer's favour by their bargaining attribute and their standing with the merchant's
    // faction. 0 for something that is not a merchant.
    public static int Price(World world, Entity customer, Entity merchant, in ItemStack stack, int count, bool buying)
    {
        if (RecordOf(world, merchant) is not { } record || count <= 0 || !world.Records().TryGet(stack.Item, out ItemRecord item)) return 0;
        return Each(world, customer, merchant, record, item, stack.Instance, buying) * count;
    }

    // The same for plain units of an item.
    public static int Price(World world, Entity customer, Entity merchant, RecordId item, int count, bool buying) =>
        Price(world, customer, merchant, new ItemStack { Item = item, Count = count }, count, buying);

    // How far the price moves in the customer's favour, a fraction in [-MaxAdvantage, MaxAdvantage].
    public static float Advantage(World world, Entity customer, Entity merchant)
    {
        if (RecordOf(world, merchant) is not { } record) return 0f;
        float skill = 0f;
        if (!record.Skill.IsEmpty && world.IsAlive(customer) && world.Has<Attributes>(customer)
            && world.Resources.TryGet<GameplayRegistries>(out var registries) && registries != null && registries.Attribute(record.Skill) >= 0)
            skill = world.Attribute(customer, record.Skill);
        float standing = 0f;
        if (customer.Tags.Has<PlayerControlled>() && Factions.FactionOf(world, merchant) is { IsEmpty: false } faction)
            standing = Factions.StandingWith(world, faction);
        float max = Math.Clamp(record.MaxAdvantage, 0f, 0.95f);
        return Math.Clamp(skill * record.SkillEffect + standing * record.DispositionEffect, -max, max);
    }

    private static int Each(World world, Entity customer, Entity merchant, MerchantRecord record, ItemRecord item, ItemInstance? instance, bool buying)
    {
        float value = Math.Max(item.Value, 0) * (instance != null ? Math.Clamp(instance.Condition, 0f, 1f) : 1f);
        if (value <= 0f) return buying ? 1 : 0;
        float advantage = Advantage(world, customer, merchant);
        int buy = Math.Max((int)MathF.Round(value * Math.Max(record.Markup, 0f) * (1f - advantage)), 1);
        if (buying) return buy;
        int sell = Math.Max((int)MathF.Round(value * Math.Max(record.Pays, 0f) * (1f + advantage)), 0);
        return Math.Min(sell, buy);   // never more for selling than buying it back costs
    }

    // The customer buys `count` of the merchant's stack at `index`: it moves into the customer's inventory,
    // instance and all, and its price from the customer's money into the merchant's purse — or nothing
    // moves and the result says why.
    public static TradeResult Buy(World world, Entity customer, Entity merchant, int index, int count)
    {
        if (RecordOf(world, merchant) is not { } record) return Refuse(TradeRefusal.NotAMerchant, 0, $"{World.Describe(merchant)} does not trade");
        Ready(world, merchant);
        if (!StackAt(world, merchant, index, count, out var stack)) return Refuse(TradeRefusal.Gone, 0, "that is not there");
        string what = Describe(world, stack, count);
        int price = Price(world, customer, merchant, in stack, count, buying: true);
        int money = world.CountOf(customer, record.Currency);
        if (money < price) return Refuse(TradeRefusal.CannotAfford, price, $"{what} costs {price} and {World.Describe(customer)} has {money}");

        // The goods first (the one step that may refuse, for weight), then the money, which is there.
        if (!world.MoveTo(merchant, index, customer, count)) return Refuse(TradeRefusal.TooHeavy, price, $"{World.Describe(customer)} cannot carry {what}");
        if (price > 0) world.Take(customer, record.Currency, price);
        world.Get<Merchant>(merchant).Gold += price;
        return Done(world, customer, merchant, stack.Item, count, price, buying: true);
    }

    // The customer sells `count` of their stack at `index` to the merchant: it moves into the merchant's
    // goods, instance and all, and its price from the merchant's purse into the customer's money — or
    // nothing moves and the result says why.
    public static TradeResult Sell(World world, Entity customer, Entity merchant, int index, int count)
    {
        if (RecordOf(world, merchant) is not { } record) return Refuse(TradeRefusal.NotAMerchant, 0, $"{World.Describe(merchant)} does not trade");
        Ready(world, merchant);
        if (!StackAt(world, customer, index, count, out var stack)) return Refuse(TradeRefusal.Gone, 0, "that is not there");
        string what = Describe(world, stack, count);
        world.Records().TryGet(stack.Item, out ItemRecord item);
        if (stack.Item == record.Currency.Id || !record.BuysCategory(item?.Category) || item?.QuestItem == true)
            return Refuse(TradeRefusal.NotBought, 0, $"{World.Describe(merchant)} does not buy {what}");
        int price = Price(world, customer, merchant, in stack, count, buying: false);
        ref var m = ref world.Get<Merchant>(merchant);
        if (m.Gold < price) return Refuse(TradeRefusal.MerchantCannotAfford, price, $"{what} fetches {price} and {World.Describe(merchant)} has {m.Gold}");

        // The money first (the customer may not have room for it), then the goods; goods that will not go
        // take the money back, which was just given and so is there.
        if (price > 0 && !world.Give(customer, record.Currency, price))
            return Refuse(TradeRefusal.TooHeavy, price, $"{World.Describe(customer)} cannot carry {price} more money");
        if (!world.MoveTo(customer, index, merchant, count))
        {
            if (price > 0) world.Take(customer, record.Currency, price);
            return Refuse(TradeRefusal.TooHeavy, price, $"{World.Describe(merchant)} cannot carry {what}");
        }
        world.Get<Merchant>(merchant).Gold -= price;
        return Done(world, customer, merchant, stack.Item, count, price, buying: false);
    }

    internal static double Now(World world) => WorldClock.Of(world).Elapsed;

    private static bool StackAt(World world, Entity owner, int index, int count, out ItemStack stack)
    {
        stack = default;
        if (count <= 0 || !world.TryGet<Inventory>(owner, out var inventory) || inventory.Items == null
            || index < 0 || index >= inventory.Items.Count || inventory.Items[index].Count < count) return false;
        stack = inventory.Items[index];
        return true;
    }

    private static bool Worn(in Equipment equipment, in ItemStack stack)
    {
        if (equipment.Worn == null) return false;
        foreach (var worn in equipment.Worn)
            if (worn.Item == stack.Item && worn.Instance == (stack.Instance?.Id ?? 0)) return true;
        return false;
    }

    private static string Describe(World world, in ItemStack stack, int count)
    {
        world.Records().TryGet(stack.Item, out ItemRecord record);
        string name = record?.Describe(stack.Item, stack.Instance) ?? stack.Item.Name;
        return count > 1 ? $"{count}x {name}" : name;
    }

    private static TradeResult Refuse(TradeRefusal why, int price, string reason)
    {
        Log.Info(LogCat.Gameplay, $"No trade: {reason}");
        return new TradeResult(why, price, reason);
    }

    private static TradeResult Done(World world, Entity customer, Entity merchant, RecordId item, int count, int price, bool buying)
    {
        Log.Info(LogCat.Gameplay, $"{World.Describe(customer)} {(buying ? "buys" : "sells")} {count}x {item.Name} for {price} "
            + $"{(buying ? "from" : "to")} {World.Describe(merchant)}");
        world.Events.Send(new Traded(customer, merchant, item, count, price, buying));
        return new TradeResult(TradeRefusal.None, price, "");
    }

    // ---- content checks ---------------------------------------------------------------------------

    internal static void Check(MerchantRecord record, RecordCheck check)
    {
        if (record.Currency.IsEmpty) check.Error(nameof(MerchantRecord.Currency), "a merchant needs a currency: the item that is money");
        if (!(record.Markup >= 0f) || !float.IsFinite(record.Markup)) check.Error(nameof(MerchantRecord.Markup), $"{record.Markup} is not a markup of 0 or more");
        if (!(record.Pays >= 0f) || !float.IsFinite(record.Pays)) check.Error(nameof(MerchantRecord.Pays), $"{record.Pays} is not a share of 0 or more");
        if (record.Pays > record.Markup)
            check.Error(nameof(MerchantRecord.Pays), $"they would pay more ({record.Pays}) than they charge ({record.Markup}): an endless money pump");
        if (!(record.MaxAdvantage >= 0f) || record.MaxAdvantage > 0.95f)
            check.Error(nameof(MerchantRecord.MaxAdvantage), $"{record.MaxAdvantage} is not between 0 and 0.95");
        if (record.Gold < 0) check.Error(nameof(MerchantRecord.Gold), $"{record.Gold} is less than nothing");
        for (int i = 0; i < record.Buys.Count; i++)
            if (string.IsNullOrWhiteSpace(record.Buys[i])) check.Error($"{nameof(MerchantRecord.Buys)}[{i}]", "an empty category");
    }
}
