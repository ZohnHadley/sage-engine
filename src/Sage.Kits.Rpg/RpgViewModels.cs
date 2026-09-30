#nullable enable
using System;
using System.Collections.Generic;
using Sage.UI;

namespace Sage.Kits.Rpg;

// The RPG kit's screens as view-models (docs/design/13 "As built (the RPG screens)", issue #98): the
// inventory grid with its weight, the equipment, looting a corpse or a chest, and Morrowind's topics.
// Each is an entry of sage.ui's `view_model` vocabulary, named by a `screen` record in the kit's own
// content (`rpg:inventory`, `rpg:equipment`, `rpg:loot`, `rpg:topics`), whose `ui_layout` says what it
// looks like — so a game re-lays out or restyles any of them with a patch and no C#.
//
// The pattern, for the screens that come after (#99):
// - **Refresh reads, and allocates nothing when nothing changed.** It runs every frame a screen shows;
//   it compares what it read last time with the world and rebuilds rows only on a difference, from
//   pools, so the strings a row shows are made when they change and never per frame.
// - **Activate acts, through the rules the rest of the game uses** — Items.Take/Give/Equip,
//   DialogueTopics.Ask — never by writing components the rules own, so a refused move and a greyed
//   row give the same reason (R17). A row's widget carries its row (UiScreen.RowOf); a fixed button is
//   known by its layout node's name.
// - **Who**: UiBindContext's Subject is the player; Other is the corpse being looted or the NPC being
//   asked, set when the screen is opened (UiScreens.OpenScreen(id, new UiBindContext(world, player, npc))).

// Moving stacks about one or more grids with a "hand": confirm on a stack picks it up, confirm on a
// square puts it down there — on the same grid a move, on another a transfer that weight may refuse —
// and Back puts it back. What a gamepad can do with a D-pad and two buttons.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
public abstract class ItemGridView : IViewModel
{
    private readonly ItemGrid[] _grids;
    private GridItem? _styledHeld;
    private int _heldX = -1, _heldY = -1;
    private RecordId _heldItem;
    private ItemGrid? _heldGrid;

    protected ItemGridView(params ItemGrid[] grids) { _grids = grids; }

    // The stack in the hand, or null.
    public GridItem? Held { get; private set; }
    public bool Holding => Held != null;
    public string HeldLabel => Held?.Label ?? "";

    // What the last action said: why a move was refused, what a take-all left behind. Empty when fine.
    public string Message { get; protected set; } = "";

    public virtual void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        var (columns, rows) = RpgConventions.Of(world).GridSize;
        bool changed = false;
        for (int i = 0; i < _grids.Length; i++)
        {
            _grids[i].Resize(columns, rows);
            changed |= _grids[i].Refresh(world, OwnerOf(i, in context));
        }
        if (changed && Held != null)
        {
            // Rebuilt from pools: find the held stack again by where it lay, or let it go.
            var again = _heldGrid?.ItemAt(_heldX, _heldY);
            Hold(again != null && again.Item == _heldItem && again.X == _heldX && again.Y == _heldY ? again : null);
        }
        if (changed || _styledHeld != Held) Restyle();
    }

    // Whose inventory grid i shows.
    protected abstract Entity OwnerOf(int grid, in UiBindContext context);

    public virtual bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { } world || UiScreen.RowOf(widget) is not GridCell cell) return false;
        if (Held == null)
        {
            if (cell.Item == null) return false;
            Hold(cell.Item);
            Message = "";
            Restyle();
            return true;
        }

        var held = Held;
        Hold(null);
        if (cell.Item == held) { Restyle(); return true; }   // put down where it was
        string reason = "";
        bool moved;
        if (cell.Grid == held.Grid)
        {
            moved = cell.Grid.Move(world, held, cell.X, cell.Y);
            if (!moved) reason = RpgText.Of(world).Format("@rpg.grid.no_room", ("item", held.Label));
        }
        else moved = ItemGrid.Transfer(world, held, cell.Grid, cell.X, cell.Y, out reason);
        Message = moved ? "" : reason;
        Refresh(in context);
        Restyle();
        return true;
    }

    public virtual bool Back(in UiBindContext context)
    {
        if (Held == null) return false;
        Hold(null);
        Restyle();
        return true;
    }

    private void Hold(GridItem? item)
    {
        Held = item;
        _heldGrid = item?.Grid;
        _heldItem = item?.Item ?? default;
        _heldX = item?.X ?? -1;
        _heldY = item?.Y ?? -1;
    }

    private void Restyle()
    {
        _styledHeld = Held;
        foreach (var grid in _grids) grid.Restyle(Held);
    }
}

// The player's bag: a grid, what it weighs against what they can carry, and the hand.
//   screen rpg:inventory — layout rpg:inventory
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
[ViewModel("rpg_inventory")]
public sealed class InventoryView : ItemGridView
{
    public InventoryView() : this(new ItemGrid()) { }

    private InventoryView(ItemGrid bag) : base(bag) { Bag = bag; }

    public ItemGrid Bag { get; }

    protected override Entity OwnerOf(int grid, in UiBindContext context) => context.Subject;
}

// Taking from a corpse or a chest (Other) into the bag (Subject): two grids and the hand between them,
// and "take all", which takes every stack that fits and says what it left.
//   screen rpg:loot — layout rpg:loot
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
[ViewModel("rpg_loot")]
public sealed class LootView : ItemGridView
{
    public const string TakeAllButton = "take_all";

    private readonly List<(RecordId Item, int Count)> _scratch = new();
    private Entity _named;

    public LootView() : this(new ItemGrid(), new ItemGrid()) { }

    private LootView(ItemGrid container, ItemGrid bag) : base(container, bag)
    {
        Container = container;
        Bag = bag;
    }

    public ItemGrid Container { get; }
    public ItemGrid Bag { get; }

    // What is being looted, by name.
    public string ContainerName { get; private set; } = "";

    public bool IsEmpty => Container.Items.Count == 0;

    public override void Refresh(in UiBindContext context)
    {
        base.Refresh(in context);
        if (context.World != null && context.Other != _named)
        {
            _named = context.Other;
            ContainerName = context.World.IsAlive(context.Other) ? World.Describe(context.Other) : "";
        }
    }

    protected override Entity OwnerOf(int grid, in UiBindContext context) => grid == 0 ? context.Other : context.Subject;

    public override bool Activate(Widget widget, in UiBindContext context)
    {
        if (widget.Name == TakeAllButton && context.World is { } world)
        {
            TakeAll(world, context.Other, context.Subject);
            base.Back(in context);   // whatever was in the hand is where it was
            Refresh(in context);
            return true;
        }
        return base.Activate(widget, in context);
    }

    // Every stack of `from` into `to` that its capacity allows, in order; Message says what stayed.
    public int TakeAll(World world, Entity from, Entity to)
    {
        var text = RpgText.Of(world);
        if (!world.TryGet<Inventory>(from, out var inventory) || inventory.Items == null) { Message = ""; return 0; }
        _scratch.Clear();
        foreach (var stack in inventory.Items)
            if (stack.Count > 0) _scratch.Add((stack.Item, stack.Count));

        int taken = 0, left = 0;
        string firstLeft = "";
        var records = world.Records();
        foreach (var (item, count) in _scratch)
        {
            records.TryGet(item, out ItemRecord? record);
            float weight = (record?.Weight ?? 0f) * count;
            bool fits = world.TryGet<Inventory>(to, out var bag)
                        && (bag.Capacity <= 0f || world.WeightOf(to) + weight <= bag.Capacity);
            if (fits && world.Take(from, item, count))
            {
                if (world.Give(to, item, count)) { taken++; continue; }
                world.Give(from, item, count);
            }
            left++;
            if (firstLeft.Length == 0) firstLeft = text.Text(record?.Describe(item) ?? item.Name);
        }
        Message = left == 0 ? "" : text.Format("@rpg.loot.left", ("count", left), ("item", firstLeft));
        return taken;
    }
}

// What is worn, slot by slot, and what else could be: confirm on a slot takes its item off, on a
// candidate puts it on (Items.Equip, whose reasons a refused one shows).
//   screen rpg:equipment — layout rpg:equipment
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
[ViewModel("rpg_equipment")]
public sealed class EquipmentView : IViewModel
{
    public sealed class SlotRow
    {
        public string Slot { get; internal set; } = "";
        public RecordId Item { get; internal set; }
        public string Text { get; internal set; } = "";
        public bool IsEmpty => Item.IsEmpty;
    }

    public sealed class CandidateRow
    {
        public RecordId Item { get; internal set; }
        public string Slot { get; internal set; } = "";
        public string Text { get; internal set; } = "";
    }

    private readonly List<SlotRow> _slotPool = new();
    private readonly List<CandidateRow> _candidatePool = new();
    private readonly List<(string Slot, RecordId Item)> _worn = new();
    private readonly List<(RecordId Item, int Count)> _carried = new();
    private Entity _who;
    private bool _has;

    public List<SlotRow> Slots { get; } = new();
    public List<CandidateRow> Candidates { get; } = new();
    public string Message { get; private set; } = "";

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        var who = context.Subject;
        world.TryGet<Equipment>(who, out var equipment);
        world.TryGet<Inventory>(who, out var inventory);
        if (_has && who == _who && Same(equipment.Worn, inventory.Items)) return;

        _has = true;
        _who = who;
        _worn.Clear();
        if (equipment.Worn != null) foreach (var w in equipment.Worn) _worn.Add((w.Slot, w.Item));
        _carried.Clear();
        if (inventory.Items != null) foreach (var s in inventory.Items) _carried.Add((s.Item, s.Count));
        Build(world, who, equipment);
    }

    private bool Same(List<EquippedItem>? worn, List<ItemStack>? carried)
    {
        if ((worn?.Count ?? 0) != _worn.Count || (carried?.Count ?? 0) != _carried.Count) return false;
        for (int i = 0; i < _worn.Count; i++)
            if (worn![i].Item != _worn[i].Item || !string.Equals(worn[i].Slot, _worn[i].Slot, StringComparison.Ordinal)) return false;
        for (int i = 0; i < _carried.Count; i++)
            if (carried![i].Item != _carried[i].Item || carried[i].Count != _carried[i].Count) return false;
        return true;
    }

    private void Build(World world, Entity who, Equipment equipment)
    {
        var records = world.Records();
        var text = RpgText.Of(world);
        var slots = world.Resources.TryGet<EquipSlots>(out var registered) ? registered.Names : (IReadOnlyList<string>)Array.Empty<string>();

        Slots.Clear();
        for (int i = 0; i < slots.Count; i++)
        {
            if (_slotPool.Count <= i) _slotPool.Add(new SlotRow());
            var row = _slotPool[i];
            row.Slot = slots[i];
            row.Item = equipment.In(slots[i]);
            string name = SlotName(text, slots[i]);
            row.Text = row.Item.IsEmpty
                ? text.Format("@rpg.equipment.empty_slot", ("slot", name))
                : text.Format("@rpg.equipment.slot", ("slot", name), ("item", ItemName(records, text, row.Item)));
            Slots.Add(row);
        }

        Candidates.Clear();
        foreach (var (item, _) in _carried)
        {
            if (!records.TryGet(item, out ItemRecord record) || record.Slot.Length == 0) continue;
            if (equipment.In(record.Slot) == item) continue;
            bool listed = false;
            foreach (var c in Candidates) listed |= c.Item == item;
            if (listed || !world.CanEquip(who, item, out _)) continue;
            if (_candidatePool.Count <= Candidates.Count) _candidatePool.Add(new CandidateRow());
            var row = _candidatePool[Candidates.Count];
            row.Item = item;
            row.Slot = record.Slot;
            row.Text = text.Format("@rpg.equipment.candidate", ("item", ItemName(records, text, item)), ("slot", SlotName(text, record.Slot)));
            Candidates.Add(row);
        }
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { } world) return false;
        switch (UiScreen.RowOf(widget))
        {
            case SlotRow slot when !slot.IsEmpty:
                world.Unequip(context.Subject, slot.Slot);
                Message = "";
                break;
            case CandidateRow candidate:
                bool ok = world.CanEquip(context.Subject, candidate.Item, out string why) && world.Equip(context.Subject, candidate.Item);
                Message = ok ? "" : why;
                break;
            default:
                return false;
        }
        _has = false;   // read it all again
        Refresh(in context);
        return true;
    }

    // "@rpg.slots.mainhand", when the tables have it; the slot's registered name otherwise.
    private static string SlotName(RpgText text, string slot)
    {
        string key = "@rpg.slots." + slot.ToLowerInvariant();
        string shown = text.Text(key);
        return shown == key ? slot : shown;
    }

    private static string ItemName(RecordStore records, RpgText text, RecordId item) =>
        text.Text(records.TryGet(item, out ItemRecord record) ? record.Describe(item) : item.Name);
}

// Morrowind's topics (issue #93's DialogueTopics): what the player (Subject) can ask the NPC (Other),
// sorted by keyword, and the last answer. Confirm on a topic asks it — the answer's `then` runs, and a
// topic it teaches appears in the list at the next refresh.
//   screen rpg:topics — layout rpg:topics
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
[ViewModel("rpg_topics")]
public sealed class TopicsView : IViewModel
{
    public const string AskedStyle = "rpg:topic_asked", TopicStyle = "rpg:topic";

    public sealed class TopicRow
    {
        public RecordId Topic { get; internal set; }

        // As content wrote it: maybe a `@key`, which the label resolves.
        public string Keyword { get; internal set; } = "";
        public bool Asked { get; internal set; }
        public string Style => Asked ? AskedStyle : TopicStyle;
    }

    private readonly List<AvailableTopic> _available = new();
    private readonly List<TopicRow> _pool = new();
    private readonly HashSet<RecordId> _asked = new();
    private Entity _named;

    public List<TopicRow> Topics { get; } = new();

    // Who is speaking, by name.
    public string Speaker { get; private set; } = "";

    // What they said last: an info's text as written (maybe a `@key`); empty before anything is asked.
    public string Answer { get; private set; } = "";

    // The keyword of the topic answered last.
    public string Asked { get; private set; } = "";

    public bool HasTopics => Topics.Count > 0;
    public bool NoTopics => Topics.Count == 0;

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        if (context.Other != _named)
        {
            _named = context.Other;
            Speaker = world.IsAlive(context.Other) ? World.Describe(context.Other) : "";
            _asked.Clear();
            Answer = Asked = "";
        }
        DialogueTopics.Available(world, context.Other, context.Subject, _available);
        if (Same()) return;

        Topics.Clear();
        for (int i = 0; i < _available.Count; i++)
        {
            if (_pool.Count <= i) _pool.Add(new TopicRow());
            var row = _pool[i];
            row.Topic = _available[i].Topic;
            row.Keyword = _available[i].Keyword;
            row.Asked = _asked.Contains(row.Topic);
            Topics.Add(row);
        }
    }

    private bool Same()
    {
        if (_available.Count != Topics.Count) return false;
        for (int i = 0; i < Topics.Count; i++)
            if (Topics[i].Topic != _available[i].Topic || !ReferenceEquals(Topics[i].Keyword, _available[i].Keyword)) return false;
        return true;
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { } world || UiScreen.RowOf(widget) is not TopicRow row) return false;
        Ask(world, context.Other, context.Subject, row);
        Refresh(in context);
        return true;
    }

    // Asks it, as the screen does on confirm: the answer and what it does (DialogueTopics.Ask).
    public TopicInfo? Ask(World world, Entity speaker, Entity listener, TopicRow row)
    {
        var info = DialogueTopics.Ask(world, speaker, listener, row.Topic);
        Answer = info?.Text ?? "@rpg.topics.no_answer";
        Asked = row.Keyword;
        _asked.Add(row.Topic);
        row.Asked = true;
        return info;
    }
}
