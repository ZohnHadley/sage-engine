#nullable enable
using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace Sage.Gameplay;

// The panels the engine's own features can fill (docs/design/13 §3, TODO F38).
//
// Each builder lives with the feature that owns the data — a spellbook is abilities' business, a bag
// is items' — and every one of them answers the same three questions a screen asks: what is in the
// list, what should each row say, and can this row be used right now (and if not, why not).
//
// The last question is the one worth having. Before this, a screen would have had to re-derive it:
// read `Attributes`, look up the cost, compare, and hope it matched what `AbilitySystem` would decide
// a tick later. It asks `AbilityRules` instead (R17), so the greyed-out row and the refused cast give
// the same answer for the same reason.
//
// A game's own screens add their own builders; nothing here is privileged.
public static class GameplayPanels
{
    // What an entity can cast, what it has readied, and what it cannot afford right now.
    public static Panel Spellbook(World world, Entity who, Panel? into = null)
    {
        var panel = into ?? new Panel();
        var records = world.Records();

        if (!world.TryGet<Abilities>(who, out var abilities) || abilities.Known is not { Count: > 0 })
        {
            panel.Begin("Spells", who, world.Has<Abilities>(who) ? "" : $"{World.Describe(who)} knows no magic at all");
            return panel;
        }

        panel.Begin("Spells", who);
        foreach (var id in abilities.Known)
        {
            if (!records.TryGet(id, out AbilityRecord record))
            {
                // An ability whose record went away with a mod, or a composed spell that was forgotten
                // (F21). It stays in the book because the book is the player's, and it says so.
                panel.Add(PanelRow.Of(id, id.Name, "", 1, abilities.Selected == id, false, "no longer exists"));
                continue;
            }

            bool can = AbilityRules.CanCast(world, records, who, id, in abilities, out _, out var why);
            panel.Add(PanelRow.Of(
                id,
                record.Name.Length > 0 ? record.Name : id.Name,
                record.Cost > 0f ? $"{record.Cost:F0} {record.CostAttribute.Name}" : "free",
                1,
                abilities.Selected == id,
                can,
                can ? "" : AbilityRules.Explain(why)));
        }
        return panel;
    }

    // What an entity is carrying: stacks with their weight, what is in its hands, and what it could
    // put there.
    public static Panel Inventory(World world, Entity who, Panel? into = null)
    {
        var panel = into ?? new Panel();
        var records = world.Records();

        if (!world.TryGet<Inventory>(who, out var inventory) || inventory.Items == null)
        {
            panel.Begin("Carrying", who, $"{World.Describe(who)} carries nothing (it has no inventory)");
            return panel;
        }

        world.TryGet<Equipment>(who, out var equipment);
        float carried = world.WeightOf(who);
        panel.Begin(inventory.Capacity > 0f
            ? $"Carrying — {carried:0.#} / {inventory.Capacity:0.#} kg"
            : $"Carrying — {carried:0.#} kg", who);

        // Two swords are two stacks of one, because a sword does not stack (05 §3.5) — and only one of
        // them is the one in your hand. Without this, both rows drew the "equipped" tick, which a
        // screenshot showed and no amount of reading the code would have.
        var ticked = new HashSet<RecordId>();

        foreach (var stack in inventory.Items)
        {
            if (stack.Count <= 0) continue;
            records.TryGet(stack.Item, out ItemRecord record);
            bool equipped = record != null && record.Slot != EquipSlot.None
                         && equipment.In(record.Slot) == stack.Item && ticked.Add(stack.Item);
            bool can = Items.CanEquip(world, who, stack.Item, out string why);

            panel.Add(PanelRow.Of(
                stack.Item,
                record?.Describe(stack.Item) ?? stack.Item.Name,
                record != null ? $"{record.Weight * stack.Count:0.#} kg" : "",
                stack.Count,
                equipped,
                // Already equipped is not a reason to grey a row out: a screen wants the tick, and
                // "take it off" is the action then.
                can || equipped,
                equipped ? "" : why));
        }
        return panel;
    }

    // What a spell can be built out of, and what each effect costs (F21's spellmaker). The prices are
    // the effect records' own, so a mod that adds an effect shows up here by adding it.
    public static Panel SpellEffects(World world, Panel? into = null)
    {
        var panel = (into ?? new Panel()).Begin("Effects you can build with");
        var records = world.Records();

        foreach (var id in records.Ids("effect"))
        {
            if (!records.TryGet(id, out EffectRecord effect) || effect.Cost <= 0f) continue;
            panel.Add(PanelRow.Of(id, id.Name, Describe(effect), 1, false, true, ""));
        }
        return panel;
    }

    // The spells this world has composed (F21), with what each would cost to make again — which is
    // what a spellmaker screen shows beside the button that unmakes one.
    public static Panel ComposedSpells(World world, Panel? into = null)
    {
        var panel = (into ?? new Panel()).Begin("Spells you invented");
        var records = world.Records();

        foreach (var draft in Spellmaker.Book(world).Drafts)
        {
            var id = new RecordId(Spellmaker.Namespace, Spellmaker.Slug(draft.Name));
            bool exists = records.Exists(id);
            panel.Add(PanelRow.Of(id, draft.Name, $"{Spellmaker.Price(records, draft):F0} mana", 1,
                                  false, exists, exists ? "" : "not composed yet"));
        }
        return panel;
    }

    private static string Describe(EffectRecord effect)
    {
        var parts = new List<string>();
        foreach (var modifier in effect.Modifiers)
            parts.Add($"{modifier.Attribute.Name} {(modifier.Value >= 0 ? "+" : "")}{modifier.Value:0.##}");
        if (effect.Duration == EffectDuration.Timed) parts.Add($"{effect.Time:0.#}s");
        else if (effect.Duration == EffectDuration.Infinite) parts.Add("lasting");
        return string.Join(", ", parts);
    }
}
