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
// and Back puts it back. What a gamepad can do with a D-pad and two buttons. And, issue #346:
// - **the pointer drags** a stack (its picture follows the pointer, UiRoot.Drag) and lets it go on a
//   square — where the square it was grabbed by lands — or over nothing of the screen, which drops it on
//   the ground; a click is the hand's pick-up and put-down;
// - **Rotate** (R, the left shoulder) turns the stack in the hand, or the focused one where it lies;
//   **Split** (F, the left trigger) halves the focused stack; **Alternate** (Delete, X) drops the one in
//   the hand or the focused one on the ground — only from the screen's subject's own grids (CanDrop);
// - while the hand holds something, the squares it would take under focus or the pointer are shown
//   (rpg:cell_target where it fits, rpg:cell_blocked where not).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
public abstract class ItemGridView : IViewModel
{
    private readonly ItemGrid[] _grids;
    private GridItem? _styledHeld;
    private int _heldX = -1, _heldY = -1;
    private RecordId _heldItem;
    private ItemGrid? _heldGrid;
    private bool _dragging;
    private int _grabX, _grabY;                 // the square of the stack the pointer took it by
    private ItemGrid? _previewGrid;             // where the hand's stack would go, shown
    private int _previewX, _previewY;

    protected ItemGridView(params ItemGrid[] grids) { _grids = grids; }

    // The stack in the hand, or null.
    public GridItem? Held { get; private set; }
    public bool Holding => Held != null;
    public string HeldLabel => Held?.Label ?? "";

    // The stack in the hand will be put down turned the other way from how it lay (Rotate while holding).
    public bool HeldTurned { get; private set; }

    // The stack in the hand is being dragged by the pointer (not carried square by square).
    public bool Dragging => Held != null && _dragging;

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
            bool turned = HeldTurned, dragging = _dragging;
            Hold(again != null && again.Item == _heldItem && again.X == _heldX && again.Y == _heldY ? again : null, dragging);
            if (Held != null) HeldTurned = turned;
        }
        if (changed || _styledHeld != Held) Restyle();
    }

    // Whose inventory grid i shows.
    protected abstract Entity OwnerOf(int grid, in UiBindContext context);

    // Whether a stack of grid i may be dropped on the ground from this screen: the subject's own.
    protected virtual bool CanDrop(int grid, in UiBindContext context) => OwnerOf(grid, in context) == context.Subject;

    // Moves a held stack onto another grid (ItemGrid.Transfer); a shop trades instead (issue #380).
    private protected virtual bool Transfer(World world, GridItem held, ItemGrid to, int x, int y, bool rotated, out string reason) =>
        ItemGrid.Transfer(world, held, to, x, y, rotated, out reason);

    // A stack went from one grid to another (a shop prices it). Called after the move succeeded.
    protected virtual void Moved(World world, RecordId item, int count, ItemGrid from, ItemGrid to) { }

    public virtual bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { } world || UiScreen.RowOf(widget) is not GridCell cell) return false;
        if (Held == null)
        {
            if (cell.Item == null) return false;
            Hold(cell.Item, dragging: false);
            Message = "";
            Restyle();
            return true;
        }

        var held = Held;
        bool turned = HeldTurned;
        Hold(null, false);
        if (cell.Item == held && !turned && cell.X == held.X && cell.Y == held.Y) { Restyle(); return true; }   // put down where it was
        PutDown(world, held, cell.Grid, cell.X, cell.Y, turned != held.Rotated);
        Refresh(in context);
        Restyle();
        return true;
    }

    public virtual bool Back(in UiBindContext context)
    {
        if (Held == null) return false;
        Hold(null, false);
        Restyle();
        return true;
    }

    // ---- the pointer and the other commands (issue #346) ----------------------------------------------

    public virtual bool DragStart(in UiDrag drag, in UiBindContext context)
    {
        if (drag.Payload is not GridCell { Item: { } item } cell || Array.IndexOf(_grids, cell.Grid) < 0) return false;
        Hold(item, dragging: true);
        Message = "";
        _grabX = _grabY = 0;
        if (GridOf(drag.Source) is { } grid && grid.CellAt(drag.Start, out int column, out int row))
        {
            _grabX = Math.Clamp(column - item.X, 0, item.Width - 1);
            _grabY = Math.Clamp(row - item.Y, 0, item.Height - 1);
        }
        Restyle();
        return true;
    }

    public virtual void DragMove(in UiDrag drag, in UiBindContext context)
    {
        if (Held == null) return;
        if (Under(in drag) is { } at) Aim(at.Grid, at.X, at.Y);
        else Aim(null, 0, 0);
        Restyle();
    }

    public virtual bool Drop(in UiDrag drag, in UiBindContext context)
    {
        if (context.World is not { } world || Held == null) return false;
        var held = Held;
        bool turned = HeldTurned;
        var under = Under(in drag);   // asked while the hand still has it: its turn and its grab
        Hold(null, false);
        if (!drag.Cancelled)
        {
            // Let go over the world — nothing of the screen, or only its backdrop (the layout's root,
            // which a style with a background makes solid) — it is dropped on the ground.
            if (drag.Target == null || drag.Target.Parent == drag.Target.Root?.Content) DropOnGround(world, held, in context);
            else if (under is { } at) PutDown(world, held, at.Grid, at.X, at.Y, turned != held.Rotated);
            // over the window but no square: it goes back where it was
        }
        Refresh(in context);
        Restyle();
        return true;
    }

    public virtual bool Command(UiCommand command, Widget? target, in UiBindContext context)
    {
        if (context.World is not { } world) return false;
        var cell = UiScreen.RowOf(target) as GridCell;
        if (cell != null && Array.IndexOf(_grids, cell.Grid) < 0) cell = null;
        string reason = "";
        switch (command)
        {
            case UiCommand.Rotate when Held != null:
                HeldTurned = !HeldTurned;
                Message = "";
                Restyle();
                return true;
            case UiCommand.Rotate when cell?.Item is { } item:
                if (!cell.Grid.Rotate(world, item)) reason = RpgText.Of(world).Format("@rpg.grid.no_room_to_turn", ("item", item.Label));
                break;
            case UiCommand.Split when Held == null && cell?.Item is { } item:
                cell.Grid.Split(world, item, out reason);
                break;
            case UiCommand.Alternate when (Held ?? cell?.Item) is { } item:
                Hold(null, false);
                DropOnGround(world, item, in context);
                reason = Message;
                break;
            default:
                return false;
        }
        Message = reason;
        Refresh(in context);
        Restyle();
        return true;
    }

    public virtual void Focused(Widget? widget, in UiBindContext context)
    {
        if (Held == null || _dragging) return;
        if (UiScreen.RowOf(widget) is GridCell cell && Array.IndexOf(_grids, cell.Grid) >= 0) Aim(cell.Grid, cell.X, cell.Y);
        else Aim(null, 0, 0);
        Restyle();
    }

    // ---- moving ---------------------------------------------------------------------------------------

    // Puts `held` down with its top left at (x, y) of `to`: a move on its own grid, a transfer onto another.
    private void PutDown(World world, GridItem held, ItemGrid to, int x, int y, bool rotated)
    {
        var from = held.Grid;
        var item = held.Item;
        int count = held.Count;
        string reason = "";
        bool moved;
        if (to == from)
        {
            moved = to.Move(world, held, x, y, rotated);
            if (!moved) reason = RpgText.Of(world).Format("@rpg.grid.no_room", ("item", held.Label));
        }
        else moved = Transfer(world, held, to, x, y, rotated, out reason);
        Message = moved ? "" : reason;
        if (moved && to != from) Moved(world, item, count, from, to);
    }

    private void DropOnGround(World world, GridItem item, in UiBindContext context)
    {
        int grid = Array.IndexOf(_grids, item.Grid);
        if (grid < 0 || !CanDrop(grid, in context))
        {
            Message = RpgText.Of(world).Format("@rpg.grid.cannot_drop", ("item", item.Label));
            return;
        }
        item.Grid.Drop(world, item, out string reason);
        Message = reason;
    }

    // The square of a grid the dragged stack's top left is over: the square under the pointer, less the
    // square of the stack it was grabbed by. Null when the pointer is over no grid of this screen.
    private (ItemGrid Grid, int X, int Y)? Under(in UiDrag drag)
    {
        if (Held == null || GridOf(drag.Target) is not { } widget || !widget.CellAt(drag.Pointer, out int column, out int row)) return null;
        ItemGrid? grid = null;
        for (int i = 0; i < widget.ChildCount && grid == null; i++)
            if (widget.Child(i).Data is GridCell cell && Array.IndexOf(_grids, cell.Grid) >= 0) grid = cell.Grid;
        if (grid == null) return null;
        var (w, h) = Held.FootprintIf(HeldTurned != Held.Rotated);
        return (grid, column - Math.Min(_grabX, w - 1), row - Math.Min(_grabY, h - 1));
    }

    private static Grid? GridOf(Widget? widget)
    {
        for (var w = widget; w != null; w = w.Parent)
            if (w is Grid grid) return grid;
        return null;
    }

    private void Aim(ItemGrid? grid, int x, int y)
    {
        _previewGrid = grid;
        _previewX = x;
        _previewY = y;
    }

    private void Hold(GridItem? item, bool dragging)
    {
        Held = item;
        HeldTurned = false;
        _dragging = item != null && dragging;
        _heldGrid = item?.Grid;
        _heldItem = item?.Item ?? default;
        _heldX = item?.X ?? -1;
        _heldY = item?.Y ?? -1;
        if (item == null) Aim(null, 0, 0);
    }

    private void Restyle()
    {
        _styledHeld = Held;
        foreach (var grid in _grids)
        {
            grid.Restyle(Held, spread: !_dragging);
            if (Held != null && grid == _previewGrid)
            {
                var (w, h) = Held.FootprintIf(HeldTurned != Held.Rotated);
                grid.Preview(_previewX, _previewY, w, h, Held);
            }
        }
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
