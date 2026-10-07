#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sage.Gameplay;

// Equipment slots written in data (issue #384). A kit or a game could always register a slot from C#
// (EquipSlots.Register, in Init); a game made of data alone, or a mod adding a helmet to one, names it
// in an `equip_slot` record instead:
//
//     { "type": "equip_slot", "id": "head", "name": "Head" }
//
// Both kinds end up in EquipSlots.Names, the code's first and then the data's (by `order`, then name),
// and an item's `slot` is checked against both as content loads. A record naming a slot code already
// registered (the kit's MainHand) is the same slot, not a second one.
[Record("equip_slot", Plugin = "sage.gameplay.items")]
public sealed class EquipSlotRecord
{
    [Property(Tooltip = "The slot's name, as items write it (matched ignoring case); empty = the record's id")]
    public string Name = "";
    [Property(Tooltip = "Where it comes among the data's slots on an equipment screen: lower first")]
    public int Order;

    public string NameOf(RecordId id) => string.IsNullOrWhiteSpace(Name) ? id.Name : Name.Trim();
}

public sealed partial class EquipSlots
{
    // The data's slots, after every load (hot reload too): the code's stay, the data's are replaced.
    internal void SetData(IEnumerable<string> names)
    {
        _all.Clear();
        _all.AddRange(_names);
        foreach (string name in names)
            if (Find(name) == null) _all.Add(name);
    }

    // The equip_slot records' names, in screen order.
    internal static IEnumerable<string> InData(RecordStore records)
    {
        var slots = new List<(int Order, string Name)>();
        foreach (var id in records.Ids("equip_slot"))
            if (records.TryGet(id, out EquipSlotRecord record)) slots.Add((record.Order, record.NameOf(id)));
        return slots.OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => s.Name).ToList();
    }

    // An item's slot must be one somebody has: registered in code, or an equip_slot in the content
    // being loaded (whose names are not in Names until the load is done).
    internal void Check(ItemRecord item, RecordCheck check)
    {
        if (item.Slot.Length == 0 || Find(item.Slot) != null) return;
        var data = new List<string>();
#pragma warning disable SAGE0131   // RecordCheck.TryGet: reading another record while checking is what this check is
        foreach (var id in check.Ids("equip_slot"))
            if (check.TryGet(id, out EquipSlotRecord? slot)) data.Add(slot.NameOf(id));
#pragma warning restore SAGE0131
        if (data.Any(n => string.Equals(n, item.Slot, StringComparison.OrdinalIgnoreCase))) return;

        var known = Names.Concat(data).ToList();
        check.Error(nameof(ItemRecord.Slot), $"no equipment slot '{item.Slot}'" + (known.Count == 0
            ? " (this game has none: an equip_slot record, or a kit or the game's module in Init with EquipSlots.Register)"
            : Spelling.Suggest(item.Slot, known)));
    }
}
