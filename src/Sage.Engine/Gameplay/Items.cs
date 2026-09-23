#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Items, inventory and interaction (docs/design/16 §3.2, TODO F19).
//
// An item is a *record*, not an entity: an inventory holds ids and counts, which is what makes
// stacking, saving and modding cheap (05 §3.5, 09 §3.5). An item only becomes an entity when it is
// lying in the world, as a `Pickup` with a billboard, and stops being one when someone takes it.
//
// Equipping is where items meet the rest of the game: a weapon hands its `attack` record to the
// wielder's `Melee` (16 §3.2), and anything else it carries — armour, a cursed ring — is an effect
// applied while it is worn (16 §3.3). Combat never asks what you are holding; it reads `Melee`.

public enum EquipSlot { None, MainHand, OffHand }

[Record("item")]
public sealed class ItemRecord
{
    public string Label = "";               // what the log and, later, the UI call it
    public RecordId Sheet;                  // the billboard it uses lying on the ground
    public Vector2 Size;                    // ground size in metres; 0 = the sheet's
    public EquipSlot Slot = EquipSlot.None;
    public RecordId Attack;                 // a weapon's swing: equipping puts this in Melee
    public List<RecordId> Effects = new();  // applied while it is equipped, removed when it comes off
    public float Weight = 1f;               // kg, against the carrier's capacity
    public int Value;                       // gold; shops are later
    public int MaxStack = 1;                // > 1 for arrows, potions and the like

    public string Describe(RecordId id) => string.IsNullOrEmpty(Label) ? id.Name : Label;
}

public struct ItemStack
{
    public RecordId Item;
    public int Count;
}

// What something is carrying. `Capacity` is kilograms; 0 means "as much as it likes".
public struct Inventory : IComponent
{
    public List<ItemStack> Items;
    public float Capacity;

    public static Inventory Create(float capacity = 0f) => new() { Items = new List<ItemStack>(), Capacity = capacity };
}

// What it is holding. Two slots in v1: a weapon and a shield is enough to prove the idea.
public struct Equipment : IComponent
{
    public RecordId MainHand;
    public RecordId OffHand;

    public readonly RecordId In(EquipSlot slot) => slot == EquipSlot.OffHand ? OffHand : MainHand;
}

// An item lying in the world, waiting to be picked up.
public struct Pickup : IComponent
{
    public RecordId Item;
    public int Count;
}

// Tag: the Use action does something with this. A `Pickup` is one implicitly; anything else with the
// tag raises an interaction its game can react to (a door, a lever, a corpse).
public struct Interactable : ITag { }

// What the Use action reached this tick. Entity I/O and proper game events replace it with 04.
public readonly record struct Interaction(Entity User, Entity Target);

public sealed class InteractionEvents
{
    public readonly List<Interaction> Interactions = new();

    // What the local player is within reach of right now, whether or not they pressed anything. A HUD
    // needs this to offer the press at all ("E  Pick up a sword"), and it costs one ray per tick.
    public Entity Hovered;

    public void Clear()
    {
        Interactions.Clear();
        Hovered = default;
    }
}

public static class Items
{
    private const float PickupRadius = 0.25f, PickupHeight = 0.5f;

    // Sets an entity up to carry things. Structural, so call it when the entity is created.
    public static void AddInventory(this World world, Entity entity, float capacity = 0f)
    {
        if (!world.Has<Inventory>(entity)) world.Add(entity, Inventory.Create(capacity));
        if (!world.Has<Equipment>(entity)) world.Add(entity, new Equipment());
    }

    public static int CountOf(this World world, Entity entity, RecordId item)
    {
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null) return 0;
        int total = 0;
        foreach (var stack in inventory.Items)
            if (stack.Item == item) total += stack.Count;
        return total;
    }

    // Total weight carried, for the capacity check and (later) encumbrance.
    public static float WeightOf(this World world, Entity entity)
    {
        if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null) return 0f;
        var records = world.Resources.Get<RecordStore>();
        float total = 0f;
        foreach (var stack in inventory.Items)
            if (records.TryGet(stack.Item, out ItemRecord record)) total += record.Weight * stack.Count;
        return total;
    }

    // Puts items in. Returns false when they don't fit or the record is missing — a caller that is
    // moving items (a pickup, a trade) must not destroy the source until this says yes.
    public static bool Give(this World world, Entity entity, RecordId item, int count = 1)
    {
        if (count <= 0 || !world.IsAlive(entity)) return false;
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(item, out ItemRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"item:{item}", $"No item record {item}");
            return false;
        }
        if (!world.Has<Inventory>(entity))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Warn, $"inventory:{World.Describe(entity)}",
                $"{World.Describe(entity)} has nothing to carry {item} in (call world.AddInventory when it is created)");
            return false;
        }

        ref var inventory = ref world.Get<Inventory>(entity);
        if (inventory.Capacity > 0f && world.WeightOf(entity) + record.Weight * count > inventory.Capacity)
        {
            Log.Info(LogCat.Gameplay, $"{World.Describe(entity)} cannot carry {count}x {record.Describe(item)}: too heavy");
            return false;
        }

        int remaining = count;
        int maxStack = Math.Max(record.MaxStack, 1);
        var items = inventory.Items ??= new List<ItemStack>();
        for (int i = 0; i < items.Count && remaining > 0; i++)
        {
            if (items[i].Item != item || items[i].Count >= maxStack) continue;
            int room = Math.Min(maxStack - items[i].Count, remaining);
            items[i] = new ItemStack { Item = item, Count = items[i].Count + room };
            remaining -= room;
        }
        while (remaining > 0)
        {
            int room = Math.Min(maxStack, remaining);
            items.Add(new ItemStack { Item = item, Count = room });
            remaining -= room;
        }
        return true;
    }

    // Takes items out. Returns false (and takes nothing) unless the whole count is there.
    public static bool Take(this World world, Entity entity, RecordId item, int count = 1)
    {
        if (count <= 0 || world.CountOf(entity, item) < count) return false;

        ref var inventory = ref world.Get<Inventory>(entity);
        int remaining = count;
        var items = inventory.Items;
        for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
        {
            if (items[i].Item != item) continue;
            int taken = Math.Min(items[i].Count, remaining);
            remaining -= taken;
            if (items[i].Count == taken) items.RemoveAt(i);
            else items[i] = new ItemStack { Item = item, Count = items[i].Count - taken };
        }

        // Something being worn has left the inventory with it.
        if (world.TryGet<Equipment>(entity, out var equipment) && world.CountOf(entity, item) == 0)
        {
            if (equipment.MainHand == item) world.Unequip(entity, EquipSlot.MainHand);
            if (equipment.OffHand == item) world.Unequip(entity, EquipSlot.OffHand);
        }
        return true;
    }

    // Wears or wields something already carried. Whatever was in that slot comes off first.
    public static bool Equip(this World world, Entity entity, RecordId item)
    {
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(item, out ItemRecord record)) return false;
        if (record.Slot == EquipSlot.None)
        {
            Log.Info(LogCat.Gameplay, $"{record.Describe(item)} is not something you can wear or wield");
            return false;
        }
        if (world.CountOf(entity, item) == 0 || !world.Has<Equipment>(entity)) return false;

        world.Unequip(entity, record.Slot);
        ref var equipment = ref world.Get<Equipment>(entity);
        if (record.Slot == EquipSlot.OffHand) equipment.OffHand = item; else equipment.MainHand = item;

        // A weapon simply replaces what the wielder swings; combat asks Melee, never the inventory.
        if (!record.Attack.IsEmpty)
        {
            if (!world.Has<Melee>(entity)) world.Add(entity, Melee.With(default));
            ref var melee = ref world.Get<Melee>(entity);
            melee.Attack = record.Attack;
        }
        foreach (var effect in record.Effects) Effects.Apply(world, entity, effect, entity);

        Log.Debug(LogCat.Gameplay, $"{World.Describe(entity)} equips {record.Describe(item)}");
        return true;
    }

    public static void Unequip(this World world, Entity entity, EquipSlot slot)
    {
        if (!world.Has<Equipment>(entity)) return;
        ref var equipment = ref world.Get<Equipment>(entity);
        var item = equipment.In(slot);
        if (item.IsEmpty) return;

        if (slot == EquipSlot.OffHand) equipment.OffHand = default; else equipment.MainHand = default;
        var records = world.Resources.Get<RecordStore>();
        if (records.TryGet(item, out ItemRecord record))
        {
            foreach (var effect in record.Effects) Effects.Remove(world, entity, effect);
            // Back to bare hands (or whatever the creature was born with).
            if (!record.Attack.IsEmpty && world.Has<Melee>(entity))
            {
                ref var melee = ref world.Get<Melee>(entity);
                melee.Attack = melee.Natural;
            }
        }
    }

    // Puts an item on the ground in front of an entity, as a Pickup someone can take again.
    public static Entity Drop(this World world, Entity entity, RecordId item, int count = 1)
    {
        if (!world.Take(entity, item, count)) return default;
        var records = world.Resources.Get<RecordStore>();
        records.TryGet(item, out ItemRecord record);

        var pose = world.Get<Transform>(entity);
        float yaw = world.TryGet<PawnIntent>(entity, out var intent) ? intent.Yaw : SageMath.YawOf(pose.LocalRotation);
        var position = pose.LocalPosition + SageMath.ForwardFromYaw(yaw) * 0.8f;
        return world.SpawnPickup(item, count, position, record);
    }

    // Makes an existing entity an item lying on the ground: its billboard, its Pickup and a small
    // solid body for the interaction queries to find.
    public static bool MakePickup(this World world, Entity entity, RecordId item, int count = 1)
    {
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(item, out ItemRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"item:{item}", $"No item record {item}");
            return false;
        }
        Decorate(world, entity, item, count, record);
        return true;
    }

    // The world entity for an item lying on the ground.
    public static Entity SpawnPickup(this World world, RecordId item, int count, Vector3 position, ItemRecord? record = null)
    {
        var records = world.Resources.Get<RecordStore>();
        if (record == null && !records.TryGet(item, out record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"item:{item}", $"No item record {item}");
            return default;
        }

        var entity = world.Create(Transform.At(position), record.Describe(item));
        Decorate(world, entity, item, count, record);
        return entity;
    }

    private static void Decorate(World world, Entity entity, RecordId item, int count, ItemRecord record)
    {
        world.Add(entity, new Pickup { Item = item, Count = Math.Max(count, 1) });
        if (!record.Sheet.IsEmpty && !world.Has<SpriteRenderer>(entity))
            world.Add(entity, new SpriteRenderer { Sheet = record.Sheet, Size = record.Size });

        // Small, solid and static: something for the interaction queries to find. Without a body, an
        // item you dropped would be invisible to the very system that picks things up.
        if (!world.Has<Collider>(entity)) world.Add(entity, Collider.Standing(PickupRadius, PickupHeight));
        if (!world.Has<RigidBody>(entity)) world.Add(entity, new RigidBody { Kind = BodyKind.Static });
    }
}

// Gameplay phase: the Use action, as a look-at-and-press (16 §3.2). A ray from the eye, the first
// solid thing it reaches, and if that is something to take, it is taken. Everything else becomes an
// interaction the game reacts to — the entity I/O version of this arrives with 04.
public sealed class InteractionSystem : ISystem
{
    private readonly ArchetypeQuery<Transform, PawnIntent, CharacterController> _users;
    private readonly RecordStore _records;
    private readonly PhysicsSpace _space;
    private readonly InteractionEvents _events;
    private readonly ActionId _use;
    private readonly CVar<float> _range;
    private readonly List<Interaction> _pending = new();   // acted on after the loop: taking an item
                                                           // destroys an entity, which a query forbids
    private readonly Entity[] _nearby = new Entity[32];    // reused: the tick budget allows no garbage

    public InteractionSystem(World world, RecordStore records, ActionRegistry actions, CVar<float> range)
    {
        _users = world.Query<Transform, PawnIntent, CharacterController>();
        _records = records;
        _space = world.Resources.Get<PhysicsSpace>();
        _events = world.Resources.Get<InteractionEvents>();
        _use = actions.Get("Use");
        _range = range;
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        _events.Clear();
        _pending.Clear();

        foreach (var (transforms, intents, characters, entities) in _users.Chunks)
        {
            var t = transforms.Span;
            var i = intents.Span;
            var c = characters.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                bool pressed = i[n].Pressed.Has(_use);
                bool isPlayer = entity.Tags.Has<PlayerControlled>();
                if (!pressed && !isPlayer) continue;      // creatures only look when they act

                var target = Reach(world, in t[n], in i[n], in c[n]);
                if (isPlayer) _events.Hovered = target;   // for the prompt, pressed or not
                if (pressed && !target.IsNull) _pending.Add(new Interaction(entity, target));
            }
        }

        foreach (var interaction in _pending)
        {
            if (!world.IsAlive(interaction.User) || !world.IsAlive(interaction.Target)) continue;
            _events.Interactions.Add(interaction);

            // The one interaction the engine knows about: picking something up.
            if (!world.TryGet<Pickup>(interaction.Target, out var pickup)) continue;
            if (!world.Give(interaction.User, pickup.Item, pickup.Count)) continue;

            _records.TryGet(pickup.Item, out ItemRecord record);
            string what = $"{(pickup.Count > 1 ? pickup.Count + "x " : "")}{record?.Describe(pickup.Item) ?? pickup.Item.Name}";
            Log.Info(LogCat.Gameplay, $"{World.Describe(interaction.User)} picks up {what}");
            world.Say($"Picked up {what}", MessageKind.Good, 3f);
            world.Destroy(interaction.Target);
        }
    }

    // What the user means by "use". Looking at something wins; otherwise the nearest thing in front
    // of them wins, because a sword lying in the grass is below the eye line and nobody wants to have
    // to stare at their own boots to pick it up.
    //
    // Triggers are invisible to both queries (review #53), so a trigger volume in the way doesn't
    // swallow the press.
    private Entity Reach(World world, in Transform transform, in PawnIntent intent, in CharacterController character)
    {
        var profile = _records.TryGet(character.Profile.IsEmpty ? MovementProfileRecord.Default : character.Profile,
                                      out MovementProfileRecord found) ? found : MovementProfileRecord.Fallback;
        Vector3 feet = transform.LocalPosition;
        Vector3 eye = CharacterController.EyeOf(feet, in character, profile);
        Vector3 aim = Vector3.Transform(TransformMath.Forward, Quaternion.CreateFromYawPitchRoll(intent.Yaw, intent.Pitch, 0));
        float range = _range.Value;

        var hit = _space.Raycast(eye, aim, range, LayerMask.All);
        if (hit.Hit && Usable(world, hit.Entity)) return hit.Entity;

        // Nothing under the crosshair: take the nearest usable thing within reach and in front.
        int count = _space.OverlapBox(feet + Vector3.UnitY * 0.5f, new Vector3(range, 1.5f, range), _nearby, LayerMask.All);
        Entity best = default;
        float nearest = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            var candidate = _nearby[i];
            if (!Usable(world, candidate)) continue;
            Vector3 to = world.Get<Transform>(candidate).LocalPosition - feet;
            float distance = SageMath.DistanceXZ(world.Get<Transform>(candidate).LocalPosition, feet);
            if (distance > range || distance >= nearest) continue;
            if (!SageMath.InCone(intent.Yaw, Vector3.Zero, to, FacingCone)) continue;
            best = candidate;
            nearest = distance;
        }
        return best;
    }

    private const float FacingCone = 140f;   // how generous "in front of them" is, in degrees

    private static bool Usable(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && (world.Has<Pickup>(entity) || entity.Tags.Has<Interactable>());
}
