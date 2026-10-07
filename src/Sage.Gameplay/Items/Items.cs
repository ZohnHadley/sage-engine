#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Gameplay;

// Items, inventory and interaction (docs/design/16 §3.2, TODO F19).
//
// An item is a *record*, not an entity: an inventory holds ids and counts, which is what makes
// stacking, saving and modding cheap (05 §3.5, 09 §3.5). An item only becomes an entity when it is
// lying in the world, as a `Pickup` with a billboard, and stops being one when someone takes it.
//
// Equipping is where items meet the rest of the game: a weapon hands its `attack` record to the
// wielder's `Melee` (16 §3.2), and anything else it carries — armour, a cursed ring — is an effect
// applied while it is worn (16 §3.3). Combat never asks what you are holding; it reads `Melee`.
//
// Where it is worn is a slot, by name, and which slots there are is the game's (issue #27): the base
// has none, and registers none. The RPG kit's two hands ("MainHand", "OffHand") are a kit default; a
// game adds a head, a ring finger or a sidearm the same way, with EquipSlots.Register in its Init.

// The slots items may name (ItemRecord.Slot), registered in Init by a kit or a game and checked as
// content loads: an item for a slot nobody has is a load error, not a sword that cannot be drawn.
public sealed partial class EquipSlots
{
    private readonly List<string> _names = new();
    private readonly List<string> _all = new();    // those, then the equip_slot records' (Slots.cs, issue #384)

    public RegistrationSeal Seal { get; } = new("equipment slot", "items that name it were checked without it");

    public IReadOnlyList<string> Names => _all;

    public void Register(string name)
    {
        Seal.Check(name);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An equipment slot needs a name.", nameof(name));
        if (Find(name) != null) throw new InvalidOperationException($"The equipment slot '{name}' is already registered.");
        _names.Add(name);
        _all.Insert(_names.Count - 1, name);
    }

    // The registered spelling of a slot, found ignoring case; null for one nobody registered.
    public string? Find(string name) => _all.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}

[Record("item", Plugin = "sage.gameplay.items")]
public sealed class ItemRecord
{
    public string Label = "";               // what the log and, later, the UI call it
    public RecordRef<SpriteSheetRecord> Sheet; // the billboard it uses lying on the ground
    public Vector2 Size;                    // ground size in metres; 0 = the sheet's
    public string Slot = "";                // where it is worn (EquipSlots); empty = not something you wear
    public RecordRef<AttackRecord> Attack;   // a weapon's swing: equipping puts this in Melee
    public List<RecordRef<EffectRecord>> Effects = new();  // applied while it is equipped, removed when it comes off
    public float Weight = 1f;               // kg, against the carrier's capacity
    public int Value;                       // what it is worth in money: a merchant's prices start here (issue #380)
    public string Category = "";            // what kind of thing it is: what a merchant's `buys` names (issue #380)
    public int MaxStack = 1;                // > 1 for arrows, potions and the like
    public RecordRef<SoundRecord> Sound;     // picking it up (11 §3, F4)
    public List<IItemUse> Uses = new();     // what using it does, in order (issue #28, ItemUses)
    public ItemDurability Durability = new(); // how it wears, what wear costs, what breaking does (issue #382); the default never wears
    [Property(Tooltip = "A quest item: it cannot be dropped or sold, only given or taken by the story (issue #391)")]
    public bool QuestItem;

    public string Describe(RecordId id) => string.IsNullOrEmpty(Label) ? id.Name : Label;

    // What one unit is called: its own name when an instance has one (issue #383), the record's otherwise.
    public string Describe(RecordId id, ItemInstance? instance) =>
        instance != null && !string.IsNullOrEmpty(instance.Name) ? instance.Name : Describe(id);
}

// One unit of an item that is not like the rest of its kind (issue #383, 16 §3.2): a sword with a name,
// an enchanted ring, a worn helmet, a wand with charges left. **Only what differs is an instance**: a plain
// stack of identical items stays ids and counts (`ItemStack.Instance` null), and an instance that differs
// in nothing (IsPlain) is made plain when it goes into an inventory, so two loaves never become two kinds.
//
// Identical instances stack like plain items, up to the record's MaxStack (a quiver of the same poisoned
// arrows); different ones never do. `Id` tells instances apart *within one inventory* — it is what an
// equipped slot points at — and is given as the instance goes in, so it means nothing anywhere else.
public sealed class ItemInstance
{
    [Property(Min = 0, Tooltip = "Which instance this is in its carrier's inventory, given as it goes in; what an equipment slot points at")]
    public int Id;
    [Property(Tooltip = "Its own name (a unique sword); empty = the item's label")]
    public string Name = "";
    [Property(Tooltip = "Enchantments: effects applied while it is equipped, on top of the item's own")]
    public List<RecordRef<EffectRecord>> Effects = new();
    [Property(Min = 0, Max = 1, Tooltip = "How worn it is, 1 = as made; below that, the durability rules say what it means")]
    public float Condition = 1f;
    [Property(Min = 0, Tooltip = "Uses left in it (a wand, a lantern's oil); 0 = none")]
    public int Charges;

    // Differs in nothing from the record: not worth being an instance.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPlain => string.IsNullOrEmpty(Name) && (Effects == null || Effects.Count == 0) && Condition >= 1f && Charges == 0;

    // The same in everything but Id: units that may share a stack.
    public bool SameAs(ItemInstance? other)
    {
        if (other == null) return IsPlain;
        if ((Name ?? "") != (other.Name ?? "") || Condition != other.Condition || Charges != other.Charges) return false;
        int a = Effects?.Count ?? 0, b = other.Effects?.Count ?? 0;
        if (a != b) return false;
        for (int i = 0; i < a; i++)
            if (Effects![i].Id != other.Effects![i].Id) return false;
        return true;
    }

    // A copy, with no Id: what a split stack's new half or a dropped unit carries.
    public ItemInstance Clone() => new()
    {
        Name = Name ?? "",
        Effects = Effects != null ? new List<RecordRef<EffectRecord>>(Effects) : new(),
        Condition = Condition,
        Charges = Charges,
    };
}

public struct ItemStack
{
    public RecordId Item;
    public int Count;
    // What makes these units unlike the rest of their kind (issue #383); null for a plain stack.
    public ItemInstance? Instance;
}

// What something is carrying. `Capacity` is kilograms; 0 means "as much as it likes".
[Component("sage:inventory")]
public struct Inventory : IComponent
{
    [Property(Tooltip = "What it carries")]
    public List<ItemStack> Items;
    [Property(Min = 0, Unit = "kg", Tooltip = "How much it can carry; 0 = no limit")]
    public float Capacity;

    public static Inventory Create(float capacity = 0f) => new() { Items = new List<ItemStack>(), Capacity = capacity };
}

// One slot's item.
public struct EquippedItem
{
    public string Slot;
    public RecordId Item;
    // The instance worn (ItemInstance.Id in the same inventory), 0 for a plain one (issue #383).
    public int Instance;
}

// What it is wearing and holding, by slot (issue #27). Version 1 had two fields, `MainHand` and
// `OffHand` — the two-slot assumption the RPG kit now makes as its default — and its upgrader turns
// them into slots of those names, so a save from then loads with the sword still in hand.
[Component("sage:equipment", Version = 2)]
public struct Equipment : IComponent
{
    [Property(Tooltip = "What is worn or wielded, by slot")]
    public List<EquippedItem>? Worn;

    public readonly RecordId In(string slot)
    {
        if (Worn != null)
            foreach (var worn in Worn)
                if (string.Equals(worn.Slot, slot, StringComparison.OrdinalIgnoreCase)) return worn.Item;
        return default;
    }

    // Which instance of it is in a slot (ItemInstance.Id; Items.WornInstance finds it), 0 for a plain one or none.
    public readonly int InstanceIn(string slot)
    {
        if (Worn != null)
            foreach (var worn in Worn)
                if (string.Equals(worn.Slot, slot, StringComparison.OrdinalIgnoreCase)) return worn.Instance;
        return 0;
    }

    // Puts an item in a slot, or empties it (`default`).
    internal void Set(string slot, RecordId item, int instance = 0)
    {
        Worn ??= new List<EquippedItem>();
        Worn.RemoveAll(w => string.Equals(w.Slot, slot, StringComparison.OrdinalIgnoreCase));
        if (!item.IsEmpty) Worn.Add(new EquippedItem { Slot = slot, Item = item, Instance = instance });
    }

    [Upgrade(1)]
    private static void From1(ref JsonObject o)
    {
        var worn = new JsonArray();
        foreach (string slot in new[] { "MainHand", "OffHand" })
        {
            string? item = o[slot]?.GetValue<string>();
            if (!string.IsNullOrEmpty(item)) worn.Add(new JsonObject { ["Slot"] = slot, ["Item"] = item });
            o.RemoveField(slot);
        }
        o["Worn"] = worn;
    }
}

// An item lying in the world, waiting to be picked up.
[Component("sage:pickup")]
public struct Pickup : IComponent
{
    public RecordId Item;
    public int Count;
    // The instance lying there (issue #383); null for plain ones.
    public ItemInstance? Instance;
}

// Tag: the Use action does something with this. A `Pickup` is one implicitly; anything else with the
// tag raises an interaction its game can react to (a door, a lever, a corpse).
[Tag("sage:interactable")]
public struct Interactable : ITag { }

// Somebody used something: the fact, for whatever a game wants to do with it (a door, a lever, a
// quest trigger). The engine itself only knows how to pick things up. Entity I/O (04 §3.4) will give
// designers the wiring; this is the code path.
//
// **It carries what was taken and where, rather than leaving the reader to ask.** A frame-side reader
// sees this up to a frame later (04 §3.1), and a sword that was picked up has been destroyed by then:
// asking the world for its `Pickup` finds nothing, which is why the pickup sound played silence until
// this carried the item itself. An event is a statement about the past; anything a reader needs has to
// be in it.
[GameEvent]
public readonly record struct Used(Entity User, Entity Target)
{
    public RecordId Item { get; init; }      // what was taken; empty if it was not a pickup or did not fit
    public Vector3 Point { get; init; }      // where it happened, for whatever presents it
}

// What the local player is within reach of right now, whether or not they pressed anything. This is
// *state*, not an event, which is why it is a resource and not on the bus: a HUD drawn a frame later
// asks "what is in reach" and must get an answer, not "what changed since you last looked" (04 §3.1).
// It costs one ray per tick.
public sealed class InteractionState
{
    public Entity Hovered;
}

public static class Items
{
    private const float PickupRadius = 0.25f, PickupHeight = 0.5f;

    // Sets an entity up to carry things. Structural, so call it when the entity is created.
    public static void AddInventory(this World world, Entity entity, float capacity = 0f)
    {
        if (!world.Has<Inventory>(entity)) world.Add(entity, Inventory.Create(capacity));
        if (!world.Has<Equipment>(entity)) world.Add(entity, new Equipment());
    }

    public static int CountOf(this World world, Entity entity, RecordId item)
    {
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null) return 0;
        int total = 0;
        foreach (var stack in inventory.Items)
            if (stack.Item == item) total += stack.Count;
        return total;
    }

    // Total weight carried, for the capacity check and (later) encumbrance.
    public static float WeightOf(this World world, Entity entity)
    {
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null) return 0f;
        var records = world.Resources.Get<RecordStore>();
        float total = 0f;
        foreach (var stack in inventory.Items)
            if (records.TryGet(stack.Item, out ItemRecord record)) total += record.Weight * stack.Count;
        return total;
    }

    // Puts items in. Returns false when they don't fit or the record is missing — a caller that is
    // moving items (a pickup, a trade) must not destroy the source until this says yes.
    public static bool Give(this World world, Entity entity, RecordId item, int count = 1) =>
        world.Give(entity, item, null, count);

    // A stack as it is, instance and all (issue #383): what moving one between inventories gives.
    public static bool Give(this World world, Entity entity, in ItemStack stack) =>
        world.Give(entity, stack.Item, stack.Instance, stack.Count);

    // Units of one instance (issue #383). A copy of `instance` goes in, so the caller's stays its own, and
    // one that differs in nothing (IsPlain) goes in as a plain item. Identical instances share a stack up
    // to MaxStack; different ones never do.
    public static bool Give(this World world, Entity entity, RecordId item, ItemInstance? instance, int count = 1)
    {
        if (count <= 0 || !world.IsAlive(entity)) return false;
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(item, out ItemRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"item:{item}", $"No item record {item}");
            return false;
        }
        if (!world.Has<Inventory>(entity))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Warn, $"inventory:{World.Describe(entity)}",
                $"{World.Describe(entity)} has nothing to carry {item} in (call world.AddInventory when it is created)");
            return false;
        }

        ref var inventory = ref world.Get<Inventory>(entity);
        if (!Lifts(world, entity, world.CarryLimitOf(entity), record, count))   // its capacity, or past it with encumbrance rules (#384)
        {
            Log.Info(LogCat.Gameplay, $"{World.Describe(entity)} cannot carry {count}x {record.Describe(item, instance)}: too heavy");
            return false;
        }
        if (instance != null && instance.IsPlain) instance = null;

        int remaining = count;
        int maxStack = Math.Max(record.MaxStack, 1);
        var items = inventory.Items ??= new List<ItemStack>();
        for (int i = 0; i < items.Count && remaining > 0; i++)
        {
            var stack = items[i];
            if (stack.Item != item || stack.Count >= maxStack || !SameKind(stack.Instance, instance)) continue;
            int room = Math.Min(maxStack - stack.Count, remaining);
            stack.Count += room;
            items[i] = stack;
            remaining -= room;
        }
        while (remaining > 0)
        {
            int room = Math.Min(maxStack, remaining);
            ItemInstance? copy = null;
            if (instance != null)
            {
                copy = instance.Clone();
                copy.Id = NextInstanceId(items);
            }
            items.Add(new ItemStack { Item = item, Count = room, Instance = copy });
            remaining -= room;
        }
        return true;
    }

    // Takes items out. Returns false (and takes nothing) unless the whole count is there. Plain units go
    // first (issue #383): selling "a sword" does not sell the one with a name.
    public static bool Take(this World world, Entity entity, RecordId item, int count = 1)
    {
        if (count <= 0 || world.CountOf(entity, item) < count) return false;
        Remove(world, entity, item, count, null);
        return true;
    }

    // Takes `count` units out of the stack at `index` in the inventory, leaving the rest of it where it was
    // (issue #383): how a stack splits on a drop, a move or a trade. `taken` is what came out, with its
    // instance (a copy when the stack was split). False, with nothing taken, unless the stack has that many.
    public static bool TakeAt(this World world, Entity entity, int index, int count, out ItemStack taken)
    {
        taken = default;
        if (count <= 0 || !world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null
            || index < 0 || index >= inventory.Items.Count || inventory.Items[index].Count < count) return false;

        var items = inventory.Items;
        var stack = items[index];
        if (stack.Count == count)
        {
            items.RemoveAt(index);
            taken = stack;
            Released(world, entity, new List<ItemStack> { stack });
            return true;
        }
        var rest = stack;
        rest.Count -= count;
        items[index] = rest;
        taken = new ItemStack { Item = stack.Item, Count = count, Instance = stack.Instance?.Clone() };
        return true;
    }

    // Splits `count` units off the stack at `index` into a stack of their own, right after it (issue #383).
    // Nothing comes or goes, so counts and weight stay; the new stack's instance is a copy with an Id of
    // its own, and whatever was worn stays with the original. False unless the stack has more than `count`.
    public static bool Split(this World world, Entity entity, int index, int count)
    {
        if (count <= 0 || !world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null
            || index < 0 || index >= inventory.Items.Count || inventory.Items[index].Count <= count) return false;

        var items = inventory.Items;
        var stack = items[index];
        var rest = stack;
        rest.Count -= count;
        items[index] = rest;
        ItemInstance? copy = null;
        if (stack.Instance != null)
        {
            copy = stack.Instance.Clone();
            copy.Id = NextInstanceId(items);
        }
        items.Insert(index + 1, new ItemStack { Item = stack.Item, Count = count, Instance = copy });
        return true;
    }

    // Moves `count` units of the stack at `index` into another inventory, instance and all (issue #383):
    // a loot screen, a chest, a trade. False, with nothing moved, when they are not there or do not fit.
    public static bool MoveTo(this World world, Entity from, int index, Entity to, int count)
    {
        if (count <= 0 || !world.IsAlive(to) || !world.TryGet<Inventory>(from, out var source) || source.Items == null
            || index < 0 || index >= source.Items.Count || source.Items[index].Count < count) return false;
        var item = source.Items[index].Item;
        if (!world.TryGet<Inventory>(to, out var target) || !world.Resources.Get<RecordStore>().TryGet(item, out ItemRecord record)
            || (from != to && !Lifts(world, to, world.CarryLimitOf(to), record, count))) return false;

        if (!world.TakeAt(from, index, count, out var taken)) return false;
        if (world.Give(to, in taken)) return true;
        world.Give(from, in taken);   // it was there a moment ago, so it fits again
        return false;
    }

    // Whether `count` more of an item keep a carrier within its capacity.
    private static bool Lifts(World world, Entity entity, float capacity, ItemRecord record, int count) =>
        capacity <= 0f || world.WeightOf(entity) + record.Weight * count <= capacity;

    // Two stacks' instances that may share a stack: both plain, or the same in everything but Id.
    private static bool SameKind(ItemInstance? a, ItemInstance? b) => a == null ? b == null : b != null && a.SameAs(b);

    private static int NextInstanceId(List<ItemStack> items)
    {
        int max = 0;
        foreach (var stack in items)
            if (stack.Instance != null) max = Math.Max(max, stack.Instance.Id);
        return max + 1;
    }

    // Takes `count` of an item, plain units first and then instances, each from the last stack back;
    // `into` gets what came out. The caller has checked they are there.
    private static void Remove(World world, Entity entity, RecordId item, int count, List<ItemStack>? into)
    {
        var items = world.Get<Inventory>(entity).Items;
        List<ItemStack>? gone = null;
        int remaining = count;
        for (int pass = 0; pass < 2 && remaining > 0; pass++)
            for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                var stack = items[i];
                if (stack.Item != item || (stack.Instance == null) != (pass == 0)) continue;
                int taken = Math.Min(stack.Count, remaining);
                remaining -= taken;
                if (stack.Count == taken)
                {
                    items.RemoveAt(i);
                    (gone ??= new List<ItemStack>()).Add(stack);
                    into?.Add(stack);
                }
                else
                {
                    var rest = stack;
                    rest.Count -= taken;
                    items[i] = rest;
                    into?.Add(new ItemStack { Item = item, Count = taken, Instance = stack.Instance?.Clone() });
                }
            }
        Released(world, entity, gone);
    }

    // Something being worn has left the inventory with its stack: it comes off, and its effects with it.
    private static void Released(World world, Entity entity, List<ItemStack>? gone)
    {
        if (gone == null || !world.TryGet<Equipment>(entity, out var equipment) || equipment.Worn == null) return;
        var items = world.Get<Inventory>(entity).Items;
        foreach (var worn in equipment.Worn.ToList())
        {
            if (IndexOf(items, worn.Item, worn.Instance) >= 0) continue;
            ItemInstance? instance = null;
            foreach (var stack in gone)
                if (stack.Item == worn.Item && stack.Instance != null && stack.Instance.Id == worn.Instance) instance = stack.Instance;
            TakeOff(world, entity, worn, instance);
        }
    }

    // The stack holding an item: a plain one for `instance` 0, otherwise the instance with that Id; -1 for none.
    private static int IndexOf(List<ItemStack>? items, RecordId item, int instance)
    {
        if (items == null) return -1;
        for (int i = 0; i < items.Count; i++)
            if (items[i].Item == item && (items[i].Instance?.Id ?? 0) == instance) return i;
        return -1;
    }

    // Wears or wields something already carried. Whatever was in that slot comes off first.
    // Whether this could be equipped, and why not (R17). Asked by a screen to grey a row out, and by
    // `Equip` itself to decide — one set of gates, so the two cannot disagree about what is wieldable.
    public static bool CanEquip(this World world, Entity entity, RecordId item, out string reason)
    {
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(item, out ItemRecord record)) { reason = "there is no such thing"; return false; }
        if (record.Slot.Length == 0) { reason = "not something you can wear or wield"; return false; }
        if (!world.Has<Equipment>(entity)) { reason = "nothing to hold it with"; return false; }
        if (world.CountOf(entity, item) == 0) { reason = "you are not carrying it"; return false; }
        if (Usable(world.Get<Inventory>(entity).Items, item, record) < 0) { reason = "it is broken"; return false; }
        reason = "";
        return true;
    }

    // Equips one of an item: a plain one when there is one, otherwise the first instance of it.
    public static bool Equip(this World world, Entity entity, RecordId item)
    {
        if (!world.CanEquip(entity, item, out string reason))
        {
            // Said once, here, rather than in each caller: the console, a screen and an AI all get the
            // same sentence, and only the ones that ask first avoid needing it.
            Log.Info(LogCat.Gameplay, $"{World.Describe(entity)} cannot equip {item.Name}: {reason}");
            return false;
        }
        var records = world.Resources.Get<RecordStore>();
        return world.EquipAt(entity, Usable(world.Get<Inventory>(entity).Items, item, records.Get<ItemRecord>(item)));
    }

    // The unit of an item to equip: a plain one when there is one, otherwise the first instance of it not
    // broken (issue #382); -1 for none.
    private static int Usable(List<ItemStack> items, RecordId item, ItemRecord record)
    {
        int index = IndexOf(items, item, 0);
        if (index < 0)
            for (int i = 0; i < items.Count && index < 0; i++)
                if (items[i].Item == item && !record.Durability.IsBroken(Durability.ConditionOf(items[i]))) index = i;
        return index;
    }

    // Equips the stack at `index` in the inventory (issue #383): that very unit, so its instance's
    // enchantments go on with the item's own effects and come off with them.
    public static bool EquipAt(this World world, Entity entity, int index)
    {
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null || index < 0 || index >= inventory.Items.Count)
        {
            Log.Info(LogCat.Gameplay, $"{World.Describe(entity)} cannot equip stack {index}: there is no such stack");
            return false;
        }
        var stack = inventory.Items[index];
        if (!world.CanEquip(entity, stack.Item, out string reason))
        {
            Log.Info(LogCat.Gameplay, $"{World.Describe(entity)} cannot equip {stack.Item.Name}: {reason}");
            return false;
        }
        var records = world.Resources.Get<RecordStore>();
        records.TryGet(stack.Item, out ItemRecord record);
        if (record.Durability.IsBroken(Durability.ConditionOf(stack)))   // until it is repaired (issue #382)
        {
            Log.Info(LogCat.Gameplay, $"{World.Describe(entity)} cannot equip {record.Describe(stack.Item, stack.Instance)}: it is broken");
            return false;
        }
        // An instance written straight into a component, never given: it gets its Id now.
        if (stack.Instance != null && stack.Instance.Id == 0) stack.Instance.Id = NextInstanceId(inventory.Items);

        world.Unequip(entity, record.Slot);
        world.Get<Equipment>(entity).Set(record.Slot, stack.Item, stack.Instance?.Id ?? 0);

        // A weapon simply replaces what the wielder swings; combat asks Melee, never the inventory.
        if (!record.Attack.IsEmpty)
        {
            if (!world.Has<Melee>(entity)) world.Add(entity, Melee.With(default));
            ref var melee = ref world.Get<Melee>(entity);
            melee.Attack = record.Attack;
        }
        foreach (var effect in record.Effects) Effects.Apply(world, entity, effect, entity);
        if (stack.Instance?.Effects != null)
            foreach (var effect in stack.Instance.Effects) Effects.Apply(world, entity, effect, entity);

        Log.Debug(LogCat.Gameplay, $"{World.Describe(entity)} equips {record.Describe(stack.Item, stack.Instance)}");
        return true;
    }

    public static void Unequip(this World world, Entity entity, string slot)
    {
        if (!world.TryGet<Equipment>(entity, out var equipment) || equipment.Worn == null) return;
        foreach (var worn in equipment.Worn)
            if (string.Equals(worn.Slot, slot, StringComparison.OrdinalIgnoreCase))
            {
                TakeOff(world, entity, worn, world.InstanceOf(entity, worn.Item, worn.Instance));
                return;
            }
    }

    // The instance worn in a slot (issue #383), or null for a plain item or an empty slot: what a screen
    // names and what durability (#382) wears down.
    public static ItemInstance? WornInstance(this World world, Entity entity, string slot)
    {
        if (!world.TryGet<Equipment>(entity, out var equipment)) return null;
        int id = equipment.InstanceIn(slot);
        return id == 0 ? null : world.InstanceOf(entity, equipment.In(slot), id);
    }

    private static ItemInstance? InstanceOf(this World world, Entity entity, RecordId item, int id)
    {
        if (id == 0 || !world.TryGet<Inventory>(entity, out var inventory)) return null;
        int index = IndexOf(inventory.Items, item, id);
        return index < 0 ? null : inventory.Items[index].Instance;
    }

    private static void TakeOff(World world, Entity entity, EquippedItem worn, ItemInstance? instance)
    {
        world.Get<Equipment>(entity).Set(worn.Slot, default);
        var records = world.Resources.Get<RecordStore>();
        if (records.TryGet(worn.Item, out ItemRecord record))
        {
            foreach (var effect in record.Effects) Effects.Remove(world, entity, effect);
            // The rounds loaded for it go back in the bag (issue #135).
            if (!record.Attack.IsEmpty) Ammunition.Unload(world, entity, record.Attack.Id);
            // Back to bare hands (or whatever the creature was born with).
            if (!record.Attack.IsEmpty && world.Has<Melee>(entity))
            {
                ref var melee = ref world.Get<Melee>(entity);
                melee.Attack = melee.Natural;
            }
        }
        if (instance?.Effects != null)
            foreach (var effect in instance.Effects) Effects.Remove(world, entity, effect);
    }

    // Whether an item may leave its carrier's hands by being dropped or sold (issue #391): not a quest item,
    // which only the story gives and takes (`take_item`, a conversation's `takeItem`).
    public static bool CanDrop(this World world, RecordId item, out string why)
    {
        if (world.Records().TryGet(item, out ItemRecord record) && record.QuestItem)
        {
            why = $"{record.Describe(item)} is needed for a quest";
            return false;
        }
        why = "";
        return true;
    }

    // Puts items on the ground in front of an entity, as a Pickup someone can take again. Plain units
    // go first, as Take takes them; an instance that goes too lies in a pickup of its own. Returns the
    // first pickup.
    public static Entity Drop(this World world, Entity entity, RecordId item, int count = 1)
    {
        if (count <= 0 || world.CountOf(entity, item) < count || !world.CanDrop(item, out _)) return default;
        var gone = new List<ItemStack>();
        Remove(world, entity, item, count, gone);

        var piles = new List<ItemStack>();
        foreach (var stack in gone)
        {
            int at = piles.FindIndex(p => SameKind(p.Instance, stack.Instance));
            if (at < 0) { piles.Add(stack); continue; }
            var pile = piles[at];
            pile.Count += stack.Count;
            piles[at] = pile;
        }
        Entity first = default;
        foreach (var pile in piles)
        {
            var dropped = InFront(world, entity, pile);
            if (first.IsNull) first = dropped;
        }
        return first;
    }

    // Puts `count` units of the stack at `index` on the ground (issue #383): the stack splits, the rest
    // stays in the bag, and the pickup carries the stack's instance.
    public static Entity DropAt(this World world, Entity entity, int index, int count)
    {
        if (!world.Has<Transform>(entity)) return default;
        if (world.TryGet<Inventory>(entity, out var inventory) && inventory.Items is { } items && index >= 0 && index < items.Count
            && !world.CanDrop(items[index].Item, out _)) return default;
        if (!world.TakeAt(entity, index, count, out var taken)) return default;
        return InFront(world, entity, taken);
    }

    private static Entity InFront(World world, Entity entity, in ItemStack stack)
    {
        var pose = world.Get<Transform>(entity);
        float yaw = world.TryGet<PawnIntent>(entity, out var intent) ? intent.Yaw : SageMath.YawOf(pose.LocalRotation);
        var position = pose.LocalPosition + SageMath.ForwardFromYaw(yaw) * 0.8f;
        return world.SpawnPickup(in stack, position);
    }

    // Makes an existing entity an item lying on the ground: its billboard, its Pickup and a small
    // solid body for the interaction queries to find.
    public static bool MakePickup(this World world, Entity entity, RecordId item, int count = 1)
    {
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(item, out ItemRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"item:{item}", $"No item record {item}");
            return false;
        }
        Decorate(world, entity, item, count, record);
        return true;
    }

    // The world entity for an item lying on the ground.
    public static Entity SpawnPickup(this World world, RecordId item, int count, Vector3 position, ItemRecord? record = null)
    {
        var records = world.Resources.Get<RecordStore>();
        if (record == null)
        {
            if (!records.TryGet(item, out ItemRecord found))
            {
                Log.Once(LogCat.Gameplay, LogLevel.Error, $"item:{item}", $"No item record {item}");
                return default;
            }
            record = found;
        }

        var entity = world.Create(Transform.At(position), record.Describe(item));
        Decorate(world, entity, item, count, record);
        world.MakePersistent(entity);   // a dropped item survives a save (phase 4i)
        return entity;
    }

    // A stack lying on the ground, its instance (a copy) with it (issue #383).
    public static Entity SpawnPickup(this World world, in ItemStack stack, Vector3 position)
    {
        var entity = world.SpawnPickup(stack.Item, stack.Count, position);
        if (!entity.IsNull && stack.Instance != null && !stack.Instance.IsPlain)
            world.Get<Pickup>(entity).Instance = stack.Instance.Clone();
        return entity;
    }

    private static void Decorate(World world, Entity entity, RecordId item, int count, ItemRecord record)
    {
        world.Add(entity, new Pickup { Item = item, Count = Math.Max(count, 1) });
        if (!record.Sheet.IsEmpty && !world.Has<SpriteRenderer>(entity))
            world.Add(entity, new SpriteRenderer { Sheet = record.Sheet, Size = record.Size });

        // Small, solid and static: something for the interaction queries to find. Without a body, an
        // item you dropped would be invisible to the very system that picks things up.
        if (!world.Has<Collider>(entity)) world.Add(entity, Collider.Standing(PickupRadius, PickupHeight));
        if (!world.Has<RigidBody>(entity)) world.Add(entity, new RigidBody { Kind = BodyKind.Static });
    }
}

// Gameplay phase: the Use action, as a look-at-and-press (16 §3.2). A ray from the eye, the first
// solid thing it reaches, and if that is something to take, it is taken. Everything else becomes an
// interaction the game reacts to — the entity I/O version of this arrives with 04.
[System("sage.items.use", Phase.Gameplay, Before = new[] { "sage.effects.tick" })]
internal sealed class InteractionSystem : ISystem
{
    private readonly Query<Transform, PawnIntent, CharacterController> _users;
    private readonly RecordStore _records;
    private readonly IPhysicsWorld _space;
    private readonly InteractionState _state;
    private readonly ActionId _use;
    private readonly CVar<float> _range;
    private readonly Deferred<Used> _pending = new();      // acted on after the loop: taking an item
                                                           // destroys an entity, which a query forbids (R14)
    private readonly Entity[] _nearby = new Entity[32];    // reused: the tick budget allows no garbage

    public InteractionSystem(World world, RecordStore records, ActionRegistry actions, CVar<float> range)
    {
        _users = world.Query<Transform, PawnIntent, CharacterController>();
        _records = records;
        _space = world.Resources.Get<IPhysicsWorld>();
        _state = world.Resources.Get<InteractionState>();
        _use = actions.Get(world.Conventions().Actions.Use);
        _range = range;
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        _state.Hovered = default;      // recomputed below; nothing in reach until something is

        foreach (var (transforms, intents, characters, entities) in _users.Chunks)
        {
            var t = transforms.Span;
            var i = intents.Span;
            var c = characters.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                bool pressed = i[n].Pressed.Has(_use);
                bool isPlayer = entity.Tags.Has<PlayerControlled>();
                if (!pressed && !isPlayer) continue;      // creatures only look when they act

                var target = Reach(world, in t[n], in i[n], in c[n]);
                if (isPlayer) _state.Hovered = target;    // for the prompt, pressed or not
                if (pressed && !target.IsNull) _pending.Add(new Used(entity, target));
            }
        }

        foreach (var interaction in _pending.Drain())
        {
            if (!world.IsAlive(interaction.User) || !world.IsAlive(interaction.Target)) continue;

            // Where it happened, read before anything is destroyed.
            Vector3 point = world.TryGet<Transform>(interaction.Target, out var where)
                ? where.LocalPosition : world.Get<Transform>(interaction.User).LocalPosition;
            RecordId got = default;

            // The one interaction the engine knows about: picking something up.
            if (world.TryGet<Pickup>(interaction.Target, out var pickup) &&
                world.Give(interaction.User, pickup.Item, pickup.Instance, pickup.Count))
            {
                got = pickup.Item;
                _records.TryGet(pickup.Item, out ItemRecord record);
                string what = $"{(pickup.Count > 1 ? pickup.Count + "x " : "")}{record?.Describe(pickup.Item, pickup.Instance) ?? pickup.Item.Name}";
                Log.Info(LogCat.Gameplay, $"{World.Describe(interaction.User)} picks up {what}");
                world.Say($"Picked up {what}", MessageKind.Good, 3f);
                // Its wires hear it before it goes (issue #91): firing reads them now and only queues.
                world.FireOutput(interaction.Target, BridgeIO.OnPickedUp, interaction.User);
                world.Destroy(interaction.Target);
            }

            // Using a thing is the oldest output there is: a lever, a button, a door you open by hand
            // (04 §3.4, F17). Fired before the event is sent for the same reason the event is sent last
            // — the target may be about to be destroyed by whatever reads it.
            world.FireOutput(interaction.Target, "OnUse", interaction.User);
            // A load door (issue 4g-5) takes the player through, at the end of the tick.
            Travel.Use(world, interaction.Target, interaction.User);

            // Sent last, and complete: every reader is later than this line — a Fixed one in a later
            // phase, a frame-side one up to a frame later — so by the time anybody reads it the sword it
            // describes has been destroyed. What the event does not carry, nobody can recover.
            world.Events.Send(interaction with { Item = got, Point = point });
        }
    }

    // What the user means by "use". Looking at something wins; otherwise the nearest thing in front
    // of them wins, because a sword lying in the grass is below the eye line and nobody wants to have
    // to stare at their own boots to pick it up.
    //
    // Triggers are invisible to both queries (review #53), so a trigger volume in the way doesn't
    // swallow the press.
    private Entity Reach(World world, in Transform transform, in PawnIntent intent, in CharacterController character)
    {
        var profile = CharacterConventions.Of(world).ProfileOf(_records, character.Profile);
        Vector3 feet = transform.LocalPosition;
        Vector3 eye = CharacterController.EyeOf(feet, in character, profile);
        Vector3 aim = Vector3.Transform(TransformMath.Forward, Quaternion.CreateFromYawPitchRoll(intent.Yaw, intent.Pitch, 0));
        float range = _range.Value;

        var hit = _space.Raycast(eye, aim, range, LayerMask.All);
        if (hit.Hit && Usable(world, hit.Entity)) return hit.Entity;

        // Nothing under the crosshair: take the nearest usable thing within reach and in front.
        int count = _space.OverlapBox(feet + Vector3.UnitY * 0.5f, new Vector3(range, 1.5f, range), _nearby, LayerMask.All);
        Entity best = default;
        float nearest = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var candidate = _nearby[i];
            if (!Usable(world, candidate)) continue;
            Vector3 to = world.Get<Transform>(candidate).LocalPosition - feet;
            float distance = SageMath.DistanceXZ(world.Get<Transform>(candidate).LocalPosition, feet);
            if (distance > range || distance >= nearest) continue;
            if (!SageMath.InCone(intent.Yaw, Vector3.Zero, to, FacingCone)) continue;
            best = candidate;
            nearest = distance;
        }
        return best;
    }

    private const float FacingCone = 140f;   // how generous "in front of them" is, in degrees

    // Something you can pick up, something a game has marked, a load door (4g-5), or **something wired to `OnUse`** (04
    // §3.4, F17). The last one is what makes a door in a map usable without a component or a tag: a
    // mapper draws it, wires `OnUse` to `Open`, and the thing is usable because using it does something.
    private static bool Usable(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity)
        && (world.Has<Pickup>(entity) || entity.Tags.Has<Interactable>() || world.Has<LoadDoor>(entity) || entity.HasOutput("OnUse"));
}
