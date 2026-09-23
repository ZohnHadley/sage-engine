namespace Sandbox;

// The Sandbox's screens (docs/design/13 §3, TODO F38).
//
// This file is the evidence for how F38 was split. A screen is *which panel* and *what Enter does*;
// the list, the selection, the scrolling, the box and the greying-out are the engine's (`Screen`,
// `PanelView`), and what each row says and whether it can be used came from the simulation
// (`GameplayPanels`). So a screen is a dozen lines, and the next one — a merchant, a quest log — is
// a dozen more.

// Your spells: what you know, what each costs, and which one the Cast button fires.
public sealed class SpellbookScreen : Screen
{
    public override string Hint => "↑↓ choose    Enter ready    Esc close";

    public override float Width => 440f;

    public override void Build(World world, Entity player) =>
        GameplayPanels.Spellbook(world, player, Panel);

    // Readying works even for a spell you cannot afford right now, which is the point of readying:
    // you pick what to throw, then find the mana. So it does not ask `Enabled` first.
    public override bool Activate(World world, Entity player, in PanelRow row) =>
        world.Ready(player, row.Id);
}

// Your bag: what you carry, what it weighs, and what is in your hands.
public sealed class InventoryScreen : Screen
{
    public override string Hint => "↑↓ choose    Enter equip    Del drop    Esc close";

    public override float Width => 480f;

    public override void Build(World world, Entity player) =>
        GameplayPanels.Inventory(world, player, Panel);

    // Enter equips, or takes it off again if it is already in a hand: one key for a two-state thing,
    // because that is what a player expects of the row with the tick beside it.
    public override bool Activate(World world, Entity player, in PanelRow row)
    {
        if (row.Selected)
        {
            var records = world.Records();
            if (!records.TryGet(row.Id, out ItemRecord record)) return false;
            world.Unequip(player, record.Slot);
            return true;
        }
        return world.Equip(player, row.Id);
    }

    // Delete drops one. The engine already makes a pickup entity out of it, so it lands in front of
    // you and can be walked over again (16 §3.2).
    public override bool Alternate(World world, Entity player, in PanelRow row) =>
        !world.Drop(player, row.Id).IsNull;
}
