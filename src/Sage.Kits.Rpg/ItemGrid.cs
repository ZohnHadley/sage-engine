#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Sage.UI;

namespace Sage.Kits.Rpg;

// The inventory as a grid (docs/design/13 "As built (the RPG screens)", issue #98): S.T.A.L.K.E.R.'s
// bag, where a rifle takes more room than a bandage and what you can carry is weight, not squares.
//
// - **A footprint is the kit's, not the base's.** The base `item` record has no size on a grid, because
//   a platformer's items have none; an `rpg_item` record with the item's id adds it —
//   `{ "type": "rpg_item", "id": "rifle", "grid": [4, 2] }` — and an item without one is 1×1.
// - **Where each stack lies is saved on the carrier** (`rpg:item_grid`), and only once the player has
//   moved something. Everything without a place is put in the first free spot, row by row, in the
//   inventory's own order — so the same bag is laid out the same way every time, and a save from before
//   the grid existed loads with its items packed from the top left.
// - **Weight is the limit**, as Items.Give has it: a move from another inventory that would go over the
//   carrier's capacity is refused with the reason, and the grid grows downwards rather than refusing
//   for want of squares.
// - **Nothing here reads a key or draws.** An ItemGrid is plain data a view-model binds — its Cells for
//   focus and navigation (one widget per square), its Items for a renderer that draws one picture
//   across an item's squares.

// The kit's fields for an item, by the item's own id (an `item` record must exist with it).
[Record("rpg_item", Plugin = RpgKitModule.Id)]
public sealed class RpgItemRecord
{
    [Property(Min = 1, Max = 16, Tooltip = "The squares it takes in an inventory grid, [columns, rows]; 1×1 when there is no rpg_item for it")]
    public Vector2 Grid = Vector2.One;

    // Whole squares, at least one each way.
    public (int Width, int Height) Footprint => (Math.Max((int)MathF.Round(Grid.X), 1), Math.Max((int)MathF.Round(Grid.Y), 1));

    public static (int Width, int Height) Of(RecordStore records, RecordId item) =>
        records.TypeNameOf(typeof(RpgItemRecord)) != null && records.TryGet(item, out RpgItemRecord record) ? record.Footprint : (1, 1);

    internal static void Check(RpgItemRecord record, RecordCheck check)
    {
        if (!check.Exists("item", check.Id))
            check.Error(null, $"it is the kit's fields for the item {check.Id}, and there is no item {check.Id}");
        if (record.Grid.X < 1 || record.Grid.Y < 1 || record.Grid.X != MathF.Round(record.Grid.X) || record.Grid.Y != MathF.Round(record.Grid.Y))
            check.Error(nameof(Grid), $"a footprint is whole squares, at least [1, 1]; it is [{record.Grid.X}, {record.Grid.Y}]");
    }
}

// Where the carrier's stacks lie on its grid, saved. A stack is matched to the first unused placement
// of its item, so two swords keep their own places and one that is gone frees its square.
[Component("rpg:item_grid")]
public struct ItemGridPlacements : IComponent
{
    [Property(Tooltip = "Where each stack lies, by item, in the inventory's order")]
    public List<GridPlacement>? Placed;
}

public struct GridPlacement
{
    public RecordId Item;
    public int X;
    public int Y;
}

// One stack on the grid.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
public sealed class GridItem
{
    public RecordId Item { get; internal set; }
    public int Count { get; internal set; }
    public int X { get; internal set; }
    public int Y { get; internal set; }
    public int Width { get; internal set; } = 1;
    public int Height { get; internal set; } = 1;

    // What it is called (localised), and "×12" for a stack.
    public string Label { get; internal set; } = "";

    // The name, how many and how heavy, for a tooltip.
    public string Tooltip { get; internal set; } = "";

    // Kilograms, for the whole stack.
    public float Weight { get; internal set; }

    public ItemGrid Grid { get; internal set; } = null!;

    public bool Covers(int x, int y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

// One square: what a widget in the grid is bound to.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
public sealed class GridCell
{
    public const string EmptyStyle = "rpg:cell", ItemStyle = "rpg:cell_item", HeldStyle = "rpg:cell_held";

    public int X { get; internal set; }
    public int Y { get; internal set; }

    // The stack lying on it, if any; the same one for every square of its footprint.
    public GridItem? Item { get; internal set; }

    // The item's label on the square at its top left, nothing on the rest.
    public string Label { get; internal set; } = "";
    public string Tooltip { get; internal set; } = "";

    // rpg:cell, rpg:cell_item or rpg:cell_held (the item being moved): ui_style ids.
    public string Style { get; internal set; } = EmptyStyle;

    public ItemGrid Grid { get; internal set; } = null!;

    public bool IsEmpty => Item == null;
}

// One inventory on a grid. Refresh reads the carrier; nothing changes the world but Move and Transfer.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
public sealed class ItemGrid
{
    private readonly List<(RecordId Item, int Count)> _stacks = new();
    private readonly List<GridPlacement> _placed = new();
    private readonly List<GridItem> _itemPool = new();
    private readonly List<GridCell> _cellPool = new();
    private GridItem?[] _occupied = Array.Empty<GridItem?>();
    private float _capacity = -1f;
    private bool _has;

    public ItemGrid(int columns = 8, int minRows = 6)
    {
        Columns = Math.Max(columns, 1);
        MinRows = Math.Max(minRows, 1);
        Rows = MinRows;
    }

    // Whose inventory it shows.
    public Entity Owner { get; private set; }

    public int Columns { get; private set; }

    // At least MinRows, and one free row below the lowest item, so there is always somewhere to move to.
    public int Rows { get; private set; }
    public int MinRows { get; private set; }

    // Columns × Rows squares, row by row: what a `grid` widget binds its rows to.
    public List<GridCell> Cells { get; } = new();

    // Every stack, in the inventory's order.
    public List<GridItem> Items { get; } = new();

    // Kilograms carried, and the most it may carry (0: no limit); a tenth of a kilo at most.
    public float Weight { get; private set; }
    public float Capacity { get; private set; }
    public bool Limited => Capacity > 0f;
    public bool Unlimited => Capacity <= 0f;

    // Moves each time the grid is laid out again, for a view-model that keeps something about it.
    public int Version { get; private set; }

    // Sets the grid's shape (rpg_conventions `inventoryGrid`); laid out again at the next Refresh.
    public void Resize(int columns, int minRows)
    {
        columns = Math.Max(columns, 1);
        minRows = Math.Max(minRows, 1);
        if (columns == Columns && minRows == MinRows) return;
        Columns = columns;
        MinRows = minRows;
        _has = false;
    }

    // Reads `owner`'s inventory and saved places. Returns whether anything changed (and the grid was
    // laid out again); when nothing did, it compares and returns, allocating nothing.
    public bool Refresh(World world, Entity owner)
    {
        bool alive = world.IsAlive(owner);
        List<ItemStack>? stacks = null;
        float capacity = 0f;
        if (alive && world.TryGet<Inventory>(owner, out var inventory))
        {
            stacks = inventory.Items;
            capacity = inventory.Capacity;
        }
        List<GridPlacement>? placed = alive && world.TryGet<ItemGridPlacements>(owner, out var places) ? places.Placed : null;

        if (_has && owner == Owner && capacity == _capacity && Same(stacks, placed)) return false;

        Owner = owner;
        _has = true;
        _capacity = capacity;
        _stacks.Clear();
        if (stacks != null)
            foreach (var stack in stacks)
                if (stack.Count > 0) _stacks.Add((stack.Item, stack.Count));
        _placed.Clear();
        if (placed != null) _placed.AddRange(placed);
        Build(world);
        return true;
    }

    private bool Same(List<ItemStack>? stacks, List<GridPlacement>? placed)
    {
        int n = 0;
        if (stacks != null)
            for (int i = 0; i < stacks.Count; i++)
            {
                if (stacks[i].Count <= 0) continue;
                if (n >= _stacks.Count || _stacks[n].Item != stacks[i].Item || _stacks[n].Count != stacks[i].Count) return false;
                n++;
            }
        if (n != _stacks.Count) return false;
        int p = placed?.Count ?? 0;
        if (p != _placed.Count) return false;
        for (int i = 0; i < p; i++)
            if (placed![i].Item != _placed[i].Item || placed[i].X != _placed[i].X || placed[i].Y != _placed[i].Y) return false;
        return true;
    }

    // The stack on a square, or null.
    public GridItem? ItemAt(int x, int y) =>
        x >= 0 && y >= 0 && x < Columns && y < Rows ? _occupied[y * Columns + x] : null;

    public GridCell? CellAt(int x, int y) =>
        x >= 0 && y >= 0 && x < Columns && y < Rows ? Cells[y * Columns + x] : null;

    // Whether a footprint fits with its top left at (x, y), `ignore` (the item being moved) aside.
    // Below the last row is room: the grid grows.
    public bool Fits(int width, int height, int x, int y, GridItem? ignore = null)
    {
        if (x < 0 || y < 0 || x + width > Columns) return false;
        for (int dy = 0; dy < height; dy++)
            for (int dx = 0; dx < width; dx++)
            {
                var at = ItemAt(x + dx, y + dy);
                if (at != null && at != ignore) return false;
            }
        return true;
    }

    // Moves a stack of this grid to (x, y), and saves where everything lies. False, with nothing
    // changed, when it would not fit there.
    public bool Move(World world, GridItem item, int x, int y)
    {
        if (item.Grid != this || !Items.Contains(item) || !Fits(item.Width, item.Height, x, y, item)) return false;
        if (item.X == x && item.Y == y) return true;
        item.X = x;
        item.Y = y;
        Save(world);
        Refresh(world, Owner);
        return true;
    }

    // Moves a whole stack from one grid's inventory into another's (a loot screen, a chest), at (x, y)
    // on the other or, with x < 0, wherever it first fits. Refused, with the reason and nothing moved,
    // when the stack would take the carrier over its capacity — the weight check Items.Give makes,
    // asked first so the screen can say why.
    public static bool Transfer(World world, GridItem item, ItemGrid to, int x, int y, out string reason)
    {
        var from = item.Grid;
        var text = RpgText.Of(world);
        if (from == to) { reason = ""; return x < 0 || to.Move(world, item, x, y); }
        if (!world.IsAlive(to.Owner) || !world.Has<Inventory>(to.Owner))
        {
            reason = text.Format("@rpg.grid.cannot_hold", ("who", World.Describe(to.Owner)));
            return false;
        }
        if (x >= 0 && !to.Fits(item.Width, item.Height, x, y))
        {
            reason = text.Format("@rpg.grid.no_room", ("item", item.Label));
            return false;
        }
        var inventory = world.Get<Inventory>(to.Owner);
        float now = world.WeightOf(to.Owner);
        if (inventory.Capacity > 0f && now + item.Weight > inventory.Capacity)
        {
            reason = text.Format("@rpg.grid.too_heavy", ("item", item.Label), ("weight", Tenths(item.Weight)),
                                 ("total", Tenths(now + item.Weight)), ("capacity", Tenths(inventory.Capacity)));
            return false;
        }

        var id = item.Item;
        int count = item.Count;
        int stacksBefore = CountStacks(world, to.Owner);
        if (!world.Take(from.Owner, id, count))
        {
            reason = text.Format("@rpg.grid.gone", ("item", item.Label));
            return false;
        }
        if (!world.Give(to.Owner, id, count))
        {
            world.Give(from.Owner, id, count);   // it was there a moment ago, so it fits again
            reason = text.Format("@rpg.grid.too_heavy", ("item", item.Label), ("weight", Tenths(item.Weight)),
                                 ("total", Tenths(now + item.Weight)), ("capacity", Tenths(inventory.Capacity)));
            return false;
        }

        from.Refresh(world, from.Owner);
        to.Refresh(world, to.Owner);
        // A new stack (not one merged into a stack already there) goes where it was put down.
        if (x >= 0 && CountStacks(world, to.Owner) > stacksBefore && to.LastOf(id) is { } placed)
            to.Move(world, placed, x, y);
        reason = "";
        return true;
    }

    private static int CountStacks(World world, Entity owner) =>
        world.TryGet<Inventory>(owner, out var inventory) && inventory.Items != null ? inventory.Items.Count : 0;

    private GridItem? LastOf(RecordId item)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].Item == item) return Items[i];
        return null;
    }

    internal static float Tenths(float kg) => MathF.Round(kg * 10f) / 10f;

    // ---- layout ---------------------------------------------------------------------------------------

    private void Build(World world)
    {
        var records = world.Records();
        var text = RpgText.Of(world);
        Version++;

        // The stacks, from the pool.
        Items.Clear();
        float weight = 0f;
        for (int i = 0; i < _stacks.Count; i++)
        {
            if (_itemPool.Count <= i) _itemPool.Add(new GridItem());
            var item = _itemPool[i];
            var (id, count) = _stacks[i];
            records.TryGet(id, out ItemRecord? record);
            var (w, h) = RpgItemRecord.Of(records, id);
            float each = record?.Weight ?? 0f;
            if (item.Item != id || item.Count != count || item.Width != w || item.Height != h || item.Weight != each * count || item.Label.Length == 0)
            {
                string name = text.Text(record?.Describe(id) ?? id.Name);
                item.Label = count > 1 ? text.Format("@rpg.grid.stack", ("item", name), ("count", count)) : name;
                item.Tooltip = text.Format("@rpg.grid.tooltip", ("item", name), ("count", count), ("weight", Tenths(each * count)));
            }
            item.Item = id;
            item.Count = count;
            item.Width = Math.Min(w, Columns);
            item.Height = h;
            item.Weight = each * count;
            item.Grid = this;
            item.X = item.Y = -1;
            weight += item.Weight;
            Items.Add(item);
        }
        Weight = Tenths(weight);
        Capacity = _capacity;

        // Saved places first, where they still fit; then the rest, first fit, row by row.
        Rows = MinRows;
        _occupied = new GridItem?[Columns * Rows];
        Span<bool> used = _placed.Count <= 256 ? stackalloc bool[_placed.Count] : new bool[_placed.Count];
        foreach (var item in Items)
            for (int p = 0; p < _placed.Count; p++)
            {
                if (used[p] || _placed[p].Item != item.Item) continue;
                used[p] = true;
                if (Fits(item.Width, item.Height, _placed[p].X, _placed[p].Y)) Occupy(item, _placed[p].X, _placed[p].Y);
                break;
            }
        foreach (var item in Items)
        {
            if (item.X >= 0) continue;
            for (int y = 0; item.X < 0; y++)
                for (int x = 0; x + item.Width <= Columns; x++)
                    if (Fits(item.Width, item.Height, x, y)) { Occupy(item, x, y); break; }
        }
        int bottom = 0;
        foreach (var item in Items) bottom = Math.Max(bottom, item.Y + item.Height);
        Grow(Math.Max(MinRows, bottom + 1));

        // One cell per square.
        while (_cellPool.Count < Columns * Rows) _cellPool.Add(new GridCell());
        Cells.Clear();
        for (int y = 0; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
            {
                var cell = _cellPool[y * Columns + x];
                var item = _occupied[y * Columns + x];
                cell.X = x;
                cell.Y = y;
                cell.Grid = this;
                cell.Item = item;
                cell.Label = item != null && item.X == x && item.Y == y ? item.Label : "";
                cell.Tooltip = item?.Tooltip ?? "";
                cell.Style = item == null ? GridCell.EmptyStyle : GridCell.ItemStyle;
                Cells.Add(cell);
            }
    }

    // Marks the squares of the stack being held (or none) with rpg:cell_held.
    public void Restyle(GridItem? held)
    {
        foreach (var cell in Cells)
            cell.Style = cell.Item == null ? GridCell.EmptyStyle : cell.Item == held ? GridCell.HeldStyle : GridCell.ItemStyle;
    }

    private void Occupy(GridItem item, int x, int y)
    {
        Grow(y + item.Height);
        item.X = x;
        item.Y = y;
        for (int dy = 0; dy < item.Height; dy++)
            for (int dx = 0; dx < item.Width; dx++)
                _occupied[(y + dy) * Columns + x + dx] = item;
    }

    private void Grow(int rows)
    {
        if (rows <= Rows && _occupied.Length == Columns * Rows) return;
        rows = Math.Max(rows, Rows);
        var grown = new GridItem?[Columns * rows];
        Array.Copy(_occupied, grown, Math.Min(_occupied.Length, grown.Length));
        _occupied = grown;
        Rows = rows;
    }

    // Where every stack lies now, onto the carrier.
    private void Save(World world)
    {
        if (!world.IsAlive(Owner)) return;
        var list = new List<GridPlacement>(Items.Count);
        foreach (var item in Items) list.Add(new GridPlacement { Item = item.Item, X = item.X, Y = item.Y });
        if (world.Has<ItemGridPlacements>(Owner)) world.Get<ItemGridPlacements>(Owner).Placed = list;
        else world.Add(Owner, new ItemGridPlacements { Placed = list });
    }
}

// The kit's words, through the game's string tables when there are any (sage.ui's Localisation).
internal readonly struct RpgText
{
    private readonly Localisation? _text;

    private RpgText(Localisation? text) { _text = text; }

    public static RpgText Of(World world) =>
        new(world.Resources.TryGet<Localisation>(out var text) ? text : null);

    public string Text(string text) => _text?.Text(text) ?? text;

    public string Format(string text, params (string Name, object? Value)[] args)
    {
        if (_text != null) return _text.Format(text, args);
        var parts = new List<string>(args.Length);
        foreach (var (name, value) in args) parts.Add($"{name}={value}");
        return $"{text}({string.Join(", ", parts)})";
    }
}
