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
