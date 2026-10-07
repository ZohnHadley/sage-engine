#nullable enable
using System;
using System.Collections.Generic;
using Sage.UI;

namespace Sage.Kits.Rpg;

// One of the RPG kit's screens (issue #98); the pattern they share is at the top of InventoryView.cs.

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
            ContainerName = context.World.IsAlive(context.Other) ? context.Other.Name ?? "" : "";
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
            records.TryGet(item, out ItemRecord record);
            float weight = (record?.Weight ?? 0f) * count;
            bool fits = world.TryGet<Inventory>(to, out var bag)
                        && (bag.Capacity <= 0f || world.WeightOf(to) + weight <= bag.Capacity);
            if (fits && world.Take(from, item, count))
            {
                if (world.Give(to, item, count)) { taken++; Containers.Took(world, to, from, item, count); continue; }
                world.Give(from, item, count);
            }
            left++;
            if (firstLeft.Length == 0) firstLeft = text.Text(record?.Describe(item) ?? item.Name);
        }
        Message = left == 0 ? "" : text.Format("@rpg.loot.left", ("count", left), ("item", firstLeft));
        return taken;
    }
}
