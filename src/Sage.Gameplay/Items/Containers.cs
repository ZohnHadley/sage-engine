#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Containers and bodies (issue #378, phase 4f): something with an inventory that somebody else opens by
// Use and takes from — a chest, a barrel, a corpse.
//
// - **A chest**, from content: `"container": { "locked": true, "key": "cellar_key", "respawn": 72,
//   "owner": "hermit", "faction": "townsfolk" }`, after the `inventory` part that says what is in it. It
//   makes the thing usable; a game's screen (the RPG kit's rpg:loot) opens on Use.
// - **A body**: a creature that dies with an inventory becomes a container (unlocked, owned by nobody), so
//   its belongings are looted the way a chest's are. The player's own body is not one.
// - **Locked**: Use with the `key` item in the user's pack unlocks it (and opens it); without, it stays
//   shut and says so. Nothing else opens it yet (lockpicking is a kit's, later).
// - **Respawn**: game hours (the world clock) after the first thing was taken, the next Use finds it
//   refilled with what it held when it was first opened. 0: never.
// - **Owned** (`owner`, an entity's name, and/or `faction`): taking from it is theft, and `Stolen` says
//   so, with what was taken and from whom, for a crime system to read. The owner takes freely, as does a
//   member of the owning faction.
//
// Whatever moves items out of a container (a loot screen) calls `Containers.Took` once it has moved them:
// that is where the respawn clock starts and theft is raised.

// Something others open and take from.
[Component("sage:container")]
public struct ItemContainer : IComponent
{
    [Property(Tooltip = "Shut until someone carrying the key uses it")]
    public bool Locked;
    [RecordRef("item"), Property(Tooltip = "The item that unlocks it; empty: nothing does (yet)")]
    public RecordId Key;
    [Property(Min = 0, Unit = "h", Tooltip = "Game hours after it was first taken from until it refills; 0 = never")]
    public float Respawn;
    [Property(Tooltip = "The name of the entity it belongs to; taking from it is theft")]
    public string? Owner;
    [RecordRef("faction"), Property(Tooltip = "The faction it belongs to; taking from it is theft, except for its members")]
    public RecordId Faction;

    public List<ItemStack>? Stock;   // what it refills with: its contents when it was first opened
    public double RestockAt;         // world-clock hours it refills at; 0 = not waiting to

    public readonly bool IsOwned => !string.IsNullOrEmpty(Owner) || !Faction.IsEmpty;
}

// Somebody took what was not theirs: from an owned container (Containers.Took). The fact, for a crime
// system to read; the engine itself does nothing about it.
[GameEvent]
public readonly record struct Stolen(Entity Thief, Entity From, RecordId Item, int Count)
{
    public string Owner { get; init; } = "";      // the container's owner, by name; empty if none
    public RecordId Faction { get; init; }        // the owning faction; empty if none
}

// "container": { "locked": true, "key": "cellar_key", "respawn": 72, "owner": "hermit" }
[PrefabPart("container", Plugin = "sage.gameplay.items", After = new[] { "inventory" })]
public sealed class ContainerPart : IPrefabPart
{
    [Property(Tooltip = "Starts locked: shut until someone carrying the key uses it")]
    public bool Locked;
    [Property(Tooltip = "The item that unlocks it")]
    public RecordRef<ItemRecord> Key;
    [Property(Min = 0, Unit = "h", Tooltip = "Game hours after it was first taken from until it refills; 0 = never")]
    public float Respawn;
    [Property(Tooltip = "The name of the entity it belongs to; taking from it is theft")]
    public string Owner = "";
    [Property(Tooltip = "The faction it belongs to; taking from it is theft, except for its members")]
    public RecordRef<FactionRecord> Faction;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Locked && Key.IsEmpty) ctx.Warn("is locked with no key: nothing will open it");
        if (!ctx.World.Has<Inventory>(ctx.Entity)) ctx.World.Add(ctx.Entity, Inventory.Create());
        ctx.World.Add(ctx.Entity, new ItemContainer
        {
            Locked = Locked,
            Key = Key.Id,
            Respawn = Math.Max(Respawn, 0f),
            Owner = Owner.Length == 0 ? null : Owner,
            Faction = Faction.Id,
        });
        ctx.Entity.AddTag<Interactable>();
    }
}

public static class Containers
{
    // Makes an entity a container (a body, a chest built in code), usable by Use. Structural.
    public static void MakeContainer(this World world, Entity entity, ItemContainer container = default)
    {
        if (!world.Has<Inventory>(entity)) world.Add(entity, Inventory.Create());
        if (world.Has<ItemContainer>(entity)) world.Get<ItemContainer>(entity) = container;
        else world.Add(entity, container);
        entity.AddTag<Interactable>();
    }

    // Whether it is shut to everybody until its key comes (a game's screen asks before opening).
    public static bool IsLocked(World world, Entity container) =>
        world.TryGet<ItemContainer>(container, out var c) && c.Locked;

    // Whether taking from it is theft for `taker`: it is owned, and not by them or their faction.
    public static bool IsTheft(World world, Entity taker, Entity container)
    {
        if (!world.TryGet<ItemContainer>(container, out var c) || !c.IsOwned) return false;
        if (!string.IsNullOrEmpty(c.Owner) && taker.Name == c.Owner) return false;
        if (!c.Faction.IsEmpty && world.TryGet<Faction>(taker, out var faction) && faction.Id == c.Faction) return false;
        return true;
    }

    // Opening by Use: a locked one unlocks for the key's carrier (or refuses), a due one refills.
    // True when it is open to `user` now.
    public static bool Open(World world, Entity user, Entity container)
    {
        if (!world.Has<ItemContainer>(container)) return false;
        ref var c = ref world.Get<ItemContainer>(container);
        if (c.Locked)
        {
            if (c.Key.IsEmpty || world.CountOf(user, c.Key) == 0)
            {
                if (user.Tags.Has<PlayerControlled>()) world.Say("Locked", MessageKind.Bad, 2f);
                return false;
            }
            c.Locked = false;
            var records = world.Resources.Get<RecordStore>();
            records.TryGet(c.Key, out ItemRecord key);
            Log.Info(LogCat.Gameplay, $"{World.Describe(user)} unlocks {World.Describe(container)} with {key?.Describe(c.Key) ?? c.Key.Name}");
            if (user.Tags.Has<PlayerControlled>()) world.Say($"Unlocked with {key?.Describe(c.Key) ?? c.Key.Name}", MessageKind.Info, 2f);
        }

        ref var inventory = ref world.Get<Inventory>(container);
        inventory.Items ??= new List<ItemStack>();
        if (c.Respawn > 0f)
        {
            c.Stock ??= new List<ItemStack>(inventory.Items);
            if (c.RestockAt > 0.0 && Now(world) >= c.RestockAt)
            {
                inventory.Items.Clear();
                inventory.Items.AddRange(c.Stock);
                c.RestockAt = 0.0;
            }
        }
        return true;
    }

    // Items were moved out of `container` by `taker` (a loot screen's take, its take-all): starts the
    // respawn clock and raises `Stolen` when it was not theirs to take. A no-op for anything else.
    public static void Took(World world, Entity taker, Entity container, RecordId item, int count)
    {
        if (count <= 0 || !world.Has<ItemContainer>(container)) return;
        ref var c = ref world.Get<ItemContainer>(container);
        if (c.Respawn > 0f)
        {
            if (c.Stock == null)   // never opened by Use (a console's ui_open): what it held is now plus this
            {
                c.Stock = world.TryGet<Inventory>(container, out var inventory) && inventory.Items != null
                    ? new List<ItemStack>(inventory.Items) : new List<ItemStack>();
                c.Stock.Add(new ItemStack { Item = item, Count = count });
            }
            if (c.RestockAt <= 0.0) c.RestockAt = Now(world) + c.Respawn;
        }
        if (!IsTheft(world, taker, container)) return;
        Log.Info(LogCat.Gameplay, $"{World.Describe(taker)} steals {count}x {item.Name} from {World.Describe(container)}");
        world.Events.Send(new Stolen(taker, container, item, count) { Owner = c.Owner ?? "", Faction = c.Faction });
    }

    private static double Now(World world) => WorldClock.Of(world).Elapsed;
}

// Gameplay phase, after the Use action: Use on a container opens it (Containers.Open: the lock, the
// refill), and a death leaves a body with an inventory a container. Reading the result: a game's screen
// asks Containers.IsLocked after this has run.
[System("sage.items.containers", Phase.Gameplay, After = new[] { "sage.items.use" })]
internal sealed class ContainerSystem : ISystem
{
    private readonly EventReader<Used> _used;
    private readonly EventReader<Died> _died;

    public ContainerSystem(World world)
    {
        _used = world.Events.Reader<Used>(this);
        _died = world.Events.Reader<Died>(this);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        if (_died.HasPending)
            foreach (ref readonly var died in _died.Read())
                if (world.IsAlive(died.Victim) && world.Has<Inventory>(died.Victim)
                    && !died.Victim.Tags.Has<PlayerControlled>() && !world.Has<ItemContainer>(died.Victim))
                    world.MakeContainer(died.Victim);

        if (!_used.HasPending) return;
        foreach (ref readonly var used in _used.Read())
            if (world.IsAlive(used.User) && world.IsAlive(used.Target) && world.Has<ItemContainer>(used.Target))
                Containers.Open(world, used.User, used.Target);
    }
}
