#nullable enable
using System;
using System.Collections.Generic;
using Sage.UI;

namespace Sage.Kits.Rpg;

// One of the RPG kit's screens (issue #98); the pattern they share is at the top of InventoryView.cs.

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
        var slots = world.Resources.TryGet<EquipSlots>(out var registered) && registered != null ? registered.Names : (IReadOnlyList<string>)Array.Empty<string>();

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
