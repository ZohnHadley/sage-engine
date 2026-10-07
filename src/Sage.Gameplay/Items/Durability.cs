#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Item condition, wear and repair (issue #382, 16 §3.2 "item conditions and repair"). An instance's
// `Condition` (#383) runs from 1, as made, to 0, broken; the item record's `durability` says what that
// means, all of it data:
//
//   { "type": "item", "id": "sword", "attack": "sword_swing", "slot": "MainHand",
//     "durability": { "wearPerHit": 0.05, "wornDamage": 0.4, "fullDamageAt": 0.75, "breaks": "Unequip" } }
//
// A weapon loses `wearPerHit` each time a blow it dealt lands (Combat.ApplyHit), and a worn item loses
// `wearWhenStruck` each time its wearer is hurt (armour, a shield). Damage scales from full at
// `fullDamageAt` down to `wornDamage` × at 0. At 0 it breaks: `Unequip` (it comes off and stays in the bag
// until it is repaired), `Remove` (it is gone) or `Keep` (it stays in hand, at `wornDamage`; 0 = useless).
// A repair is an item use (`repair`): a kit whose use puts condition back on what is worn.
//
// A plain item that takes wear becomes an instance as it does (issue #383, "only what differs is an
// instance"): the worn unit is split off its stack if it shared one, and one repaired back to as made,
// differing in nothing else, is plain again.

// What breaking does to an item (ItemDurability.Breaks).
public enum ItemBreak
{
    Unequip,   // it comes off and cannot be equipped until it is repaired
    Remove,    // it is destroyed
    Keep,      // it stays equipped, at the worn end of the damage curve
}

// An item record's wear rules (`durability`); the default never wears.
public sealed class ItemDurability
{
    [Property(Min = 0, Max = 1, Tooltip = "Condition a weapon loses each time a blow it dealt lands; 0 = it never wears from use")]
    public float WearPerHit;
    [Property(Min = 0, Max = 1, Tooltip = "Condition a worn item (armour, a shield) loses each time its wearer is hurt; 0 = none")]
    public float WearWhenStruck;
    [Property(Min = 0, Tooltip = "Damage multiplier at condition 0 (0 = a broken weapon does nothing); between it and full damage the scale is linear")]
    public float WornDamage = 0.5f;
    [Property(Min = 0, Max = 1, Tooltip = "Condition at or above which it does full damage")]
    public float FullDamageAt = 1f;
    [Property(Tooltip = "What reaching condition 0 does: Unequip (it comes off until repaired), Remove (it is destroyed) or Keep (it stays, at the worn damage)")]
    public ItemBreak Breaks = ItemBreak.Unequip;

    // The damage multiplier at a condition: 1 at or above FullDamageAt, WornDamage at 0, linear between.
    public float DamageScale(float condition)
    {
        if (condition >= FullDamageAt || FullDamageAt <= 0f) return 1f;
        float t = MathF.Max(0f, condition) / FullDamageAt;
        return WornDamage + (1f - WornDamage) * t;
    }

    // Whether a unit in this condition is broken and so cannot be equipped.
    public bool IsBroken(float condition) => Breaks != ItemBreak.Keep && condition <= 0f;
}

// Something worn broke (condition 0): what a HUD, a sound or a quest reacts to. `Removed` when it was
// destroyed, otherwise it is still carried.
[GameEvent]
public readonly record struct ItemBroke(Entity Owner, RecordId Item, string Slot, bool Removed);

public static class Durability
{
    // A stack's condition: its instance's, or 1 for a plain one (as made).
    public static float ConditionOf(in ItemStack stack) => stack.Instance?.Condition ?? 1f;

    // The condition of what is worn in a slot; 1 for a plain item or an empty slot.
    internal static float ConditionIn(this World world, Entity entity, string slot) => world.WornInstance(entity, slot)?.Condition ?? 1f;

    // Wears what is in a slot by `amount` of condition, breaking it at 0 as its record says. A plain item
    // becomes an instance (split off its stack when it shared one). False when nothing there can wear.
    public static bool Wear(this World world, Entity entity, string slot, float amount)
    {
        if (amount <= 0f || !world.TryGet<Equipment>(entity, out var equipment) || equipment.Worn == null) return false;
        for (int i = 0; i < equipment.Worn.Count; i++)
            if (string.Equals(equipment.Worn[i].Slot, slot, StringComparison.OrdinalIgnoreCase))
                return WearAt(world, entity, i, amount, out _);
        return false;
    }

    // Puts `amount` of condition back on the stack at `index`, up to `upTo` (a field kit that only gets it
    // to 0.8): what a repair screen calls with the selected item. One unit of a stack is repaired (split
    // off if it shared one); one back to as made and differing in nothing else is plain again. False when
    // there is nothing there below `upTo`.
    public static bool Repair(this World world, Entity entity, int index, float amount, float upTo = 1f)
    {
        if (!world.CanRepair(entity, index, upTo) || amount <= 0f) return false;
        var items = world.Get<Inventory>(entity).Items;
        if (items[index].Count > 1) world.Split(entity, index, items[index].Count - 1);   // the unit at `index` keeps its Id

        var stack = items[index];
        var instance = stack.Instance!;
        instance.Condition = MathF.Min(MathF.Min(upTo, 1f), instance.Condition + amount);
        if (instance.IsPlain)
        {
            Repoint(world, entity, stack.Item, instance.Id, 0);
            stack.Instance = null;
            items[index] = stack;
        }
        return true;
    }

    // Whether the stack at `index` is below `upTo` and so could be repaired.
    public static bool CanRepair(this World world, Entity entity, int index, float upTo = 1f) =>
        world.TryGet<Inventory>(entity, out var inventory) && inventory.Items != null && index >= 0 && index < inventory.Items.Count
        && ConditionOf(inventory.Items[index]) < MathF.Min(upTo, 1f);

    // What a repair with no stack chosen mends: the most worn thing in `slot`, or with no slot the most
    // worn thing equipped, else the most worn thing carried; -1 when nothing is below `upTo`.
    internal static int RepairTarget(this World world, Entity entity, string slot, float upTo = 1f)
    {
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null) return -1;
        world.TryGet<Equipment>(entity, out var equipment);
        int best = -1;
        float lowest = MathF.Min(upTo, 1f);
        for (int pass = 0; pass < 2 && best < 0; pass++)
        {
            if (pass == 1 && slot.Length > 0) break;   // a slot was named: only it
            for (int i = 0; i < inventory.Items.Count; i++)
            {
                var stack = inventory.Items[i];
                if (stack.Instance == null || stack.Instance.Condition >= lowest) continue;
                if (pass == 0 && !WornIn(in equipment, stack, slot)) continue;
                best = i;
                lowest = stack.Instance.Condition;
            }
        }
        return best;
    }

    // ---- what combat calls --------------------------------------------------------------------------

    // The worn weapon whose record hands its wielder `attack`: the index of its entry in Equipment.Worn
    // and its record, or -1. What scales a blow's damage and wears the blade that dealt it.
    internal static int WeaponFor(World world, Entity attacker, RecordId attack, RecordStore records, out ItemRecord? record)
    {
        record = null;
        if (attack.IsEmpty || !world.TryGet<Equipment>(attacker, out var equipment) || equipment.Worn == null) return -1;
        for (int i = 0; i < equipment.Worn.Count; i++)
            if (records.TryGet(equipment.Worn[i].Item, out ItemRecord found) && found.Attack.Id == attack)
            {
                record = found;
                return i;
            }
        return -1;
    }

    // The damage multiplier for a blow of `attack` by `attacker`: its weapon's condition on its curve.
    internal static float DamageScale(World world, Entity attacker, RecordId attack, RecordStore records)
    {
        int worn = WeaponFor(world, attacker, attack, records, out var record);
        if (worn < 0) return 1f;
        var equipped = world.Get<Equipment>(attacker).Worn![worn];
        float condition = equipped.Instance == 0 ? 1f : world.WornInstance(attacker, equipped.Slot)?.Condition ?? 1f;
        return record!.Durability.DamageScale(condition);
    }

    // A blow of `attack` landed: the weapon that dealt it wears.
    internal static void Landed(World world, Entity attacker, RecordId attack, RecordStore records)
    {
        int worn = WeaponFor(world, attacker, attack, records, out var record);
        if (worn >= 0 && record!.Durability.WearPerHit > 0f) WearAt(world, attacker, worn, record.Durability.WearPerHit, out _);
    }

    // `target` was hurt: whatever it wears that wears when struck does. Backwards, as a break takes its
    // entry out of the list.
    internal static void Hurt(World world, Entity target, RecordStore records)
    {
        if (!world.TryGet<Equipment>(target, out var equipment) || equipment.Worn == null) return;
        for (int i = equipment.Worn.Count - 1; i >= 0; i--)
        {
            if (i >= equipment.Worn.Count) continue;
            if (records.TryGet(equipment.Worn[i].Item, out ItemRecord record) && record.Durability.WearWhenStruck > 0f)
                WearAt(world, target, i, record.Durability.WearWhenStruck, out _);
        }
    }

    // The content checks for `durability` (issue #382): numbers in range, so a typo is a load error.
    internal static void AddChecks(RecordStore records) =>
        records.AddCheck<ItemRecord>((item, check) =>
        {
            var d = item.Durability;
            if (d == null) { check.Error(nameof(ItemRecord.Durability), "durability is null; leave it out for an item that never wears"); return; }
            if (d.WearPerHit is < 0f or > 1f) check.Error(nameof(ItemRecord.Durability), $"wearPerHit {d.WearPerHit} is not between 0 and 1");
            if (d.WearWhenStruck is < 0f or > 1f) check.Error(nameof(ItemRecord.Durability), $"wearWhenStruck {d.WearWhenStruck} is not between 0 and 1");
            if (d.WornDamage < 0f) check.Error(nameof(ItemRecord.Durability), $"wornDamage {d.WornDamage} is below 0");
            if (d.FullDamageAt is <= 0f or > 1f) check.Error(nameof(ItemRecord.Durability), $"fullDamageAt {d.FullDamageAt} is not above 0 and at most 1");
        });

    // ---- the work -----------------------------------------------------------------------------------

    // Wears the item in `Worn[worn]`. `broke` when this took it to 0.
    private static bool WearAt(World world, Entity entity, int worn, float amount, out bool broke)
    {
        broke = false;
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null) return false;
        var equipped = world.Get<Equipment>(entity).Worn![worn];
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(equipped.Item, out ItemRecord record)) return false;
        var items = inventory.Items;
        int index = IndexOf(items, equipped.Item, equipped.Instance);
        if (index < 0) return false;

        var stack = items[index];
        if (stack.Instance != null && stack.Instance.Condition <= 0f) return false;   // nothing left to lose (Keep)
        ItemInstance instance;
        if (stack.Count > 1)
        {
            // One unit of a shared stack is in hand: it leaves the stack, which keeps what it was.
            stack.Count--;
            items[index] = stack;
            instance = stack.Instance?.Clone() ?? new ItemInstance();
            instance.Id = NextInstanceId(items);
            index++;
            items.Insert(index, new ItemStack { Item = equipped.Item, Count = 1, Instance = instance });
            Repoint(world, entity, worn, instance.Id);
        }
        else if (stack.Instance == null)
        {
            instance = new ItemInstance { Id = NextInstanceId(items) };
            stack.Instance = instance;
            items[index] = stack;
            Repoint(world, entity, worn, instance.Id);
        }
        else instance = stack.Instance;

        instance.Condition = MathF.Max(0f, instance.Condition - amount);
        if (instance.Condition > 0f) return true;

        broke = true;
        var durability = record.Durability;
        Log.Info(LogCat.Gameplay, $"{World.Describe(entity)}'s {record.Describe(equipped.Item, instance)} breaks");
        if (entity.Tags.Has<PlayerControlled>())
            world.Say($"Your {record.Describe(equipped.Item, instance)} breaks", MessageKind.Bad, 3f);
        switch (durability.Breaks)
        {
            case ItemBreak.Unequip: world.Unequip(entity, equipped.Slot); break;
            case ItemBreak.Remove: world.TakeAt(entity, index, 1, out _); break;   // off it comes with its stack
        }
        world.Events.Send(new ItemBroke(entity, equipped.Item, equipped.Slot, durability.Breaks == ItemBreak.Remove));
        return true;
    }

    // Points `Worn[worn]` at another instance of the same item, in place: nothing comes off or goes on.
    private static void Repoint(World world, Entity entity, int worn, int instance)
    {
        var list = world.Get<Equipment>(entity).Worn!;
        var entry = list[worn];
        entry.Instance = instance;
        list[worn] = entry;
    }

    // Repoints whatever wears instance `from` of `item` at `to`.
    private static void Repoint(World world, Entity entity, RecordId item, int from, int to)
    {
        if (!world.TryGet<Equipment>(entity, out var equipment) || equipment.Worn == null) return;
        for (int i = 0; i < equipment.Worn.Count; i++)
            if (equipment.Worn[i].Item == item && equipment.Worn[i].Instance == from) Repoint(world, entity, i, to);
    }

    private static bool WornIn(in Equipment equipment, in ItemStack stack, string slot)
    {
        if (equipment.Worn == null || stack.Instance == null) return false;
        foreach (var worn in equipment.Worn)
            if (worn.Item == stack.Item && worn.Instance == stack.Instance.Id
                && (slot.Length == 0 || string.Equals(worn.Slot, slot, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static int IndexOf(List<ItemStack> items, RecordId item, int instance)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i].Item == item && (items[i].Instance?.Id ?? 0) == instance) return i;
        return -1;
    }

    private static int NextInstanceId(List<ItemStack> items)
    {
        int max = 0;
        foreach (var stack in items)
            if (stack.Instance != null) max = Math.Max(max, stack.Instance.Id);
        return max + 1;
    }
}

// A repair kit (issue #382): puts `amount` of condition back on what is worn in `slot` — with no slot,
// the most worn thing equipped, else the most worn thing carried — up to `upTo`. It refuses when nothing
// needs it, so a kit listed with `consume` after it is not used up for nothing:
//
//   { "type": "item", "id": "repair_kit", "uses": [{ "use": "repair", "amount": 0.5 }, "consume"] }
[ItemUse("repair", Plugin = "sage.gameplay.items")]
internal sealed class RepairUse : IItemUse
{
    [Property(Min = 0, Max = 1, Tooltip = "Condition it puts back")]
    public float Amount = 0.25f;
    [Property(Min = 0, Max = 1, Tooltip = "The best condition it can bring something to (a field kit is not a smith)")]
    public float UpTo = 1f;
    [Property(Tooltip = "The equipment slot whose item it mends; empty = the most worn thing equipped, else the most worn thing carried")]
    public string Slot = "";

    public bool CanUse(in ItemUse use, out string why)
    {
        why = use.World.RepairTarget(use.User, Slot ?? "", UpTo) >= 0 ? "" : "nothing needs repair";
        return why.Length == 0;
    }

    public bool Use(in ItemUse use, out string why)
    {
        int target = use.World.RepairTarget(use.User, Slot ?? "", UpTo);
        why = target >= 0 && use.World.Repair(use.User, target, Amount, UpTo) ? "" : "nothing needs repair";
        return why.Length == 0;
    }
}
