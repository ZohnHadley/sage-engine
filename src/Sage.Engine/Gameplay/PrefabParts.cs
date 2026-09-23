#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Friflo.Engine.ECS;

namespace sage_engine;

// The prefab parts the engine's own modules register (F31, 05 §3.5). A part exists only where the
// setup is *not* one component: `Collider`, `RigidBody`, `SpriteRenderer`, `AIState` and `Melee` are
// plain data and belong under "components", so they have no part here. What is left is the handful
// of cases where several components have to agree, or where a record has to be read to build them.
//
// A game or a mod registers its own the same way, in its module's Init:
//
//     ctx.Engine.Prefabs.Register("loot", (world, entity, options, where) => { ... });
public static class PrefabParts
{
    // Reads a part's options as T. An absent or empty body gives the defaults, which is what `{}`
    // means: "yes, this part, nothing to say about it".
    public static T Read<T>(World world, JsonNode? options, string part, string where) where T : class, new()
    {
        if (options is null) return new T();
        var json = world.Engine?.Records.Json;
        try
        {
            return (json is null ? null : JsonSerializer.Deserialize<T>(options.ToJsonString(), json)) ?? new T();
        }
        catch (JsonException ex)
        {
            Log.Error(LogCat.Records, $"{where}: prefab part '{part}': {ex.Message}");
            return new T();
        }
    }

    // ---- physics ---------------------------------------------------------------------------------

    private sealed class CharacterOptions
    {
        public string Layer = "";          // a physics layer by name (10 §3.2); empty = the default
        public RecordId Profile;           // movement_profile record; empty = the engine's default
    }

    // "character": { "layer": "enemy", "profile": "sage:walker" }
    //
    // A capsule the engine moves, the controller that moves it, and the intent its controller writes
    // — four components that have to agree about radius, height and layer, which is why this is a
    // part and not four entries under "components".
    public static void Character(World world, Entity entity, JsonNode? options, string where)
    {
        var o = Read<CharacterOptions>(world, options, "character", where);
        var layers = world.Resources.Get<PhysicsSpace>().Layers;
        byte layer = layers.Default;
        if (!string.IsNullOrEmpty(o.Layer) && !layers.TryIndexOf(o.Layer, out layer))
            Log.Warn(LogCat.Records, $"{where}: character: no physics layer '{o.Layer}'; using '{layers.Name(layer)}'");
        world.AddCharacter(entity, layer, o.Profile);
    }

    // ---- gameplay --------------------------------------------------------------------------------

    private sealed class MeleeOptions { public RecordId Attack; public RecordId Natural; }

    // "melee": { "attack": "sage:claw" }
    //
    // A part rather than a `Melee` component so that one id fills both what it is swinging and what
    // it swings bare-handed: writing only `attack` and leaving `natural` empty means disarming it
    // leaves it unable to fight at all, which is never what was meant (16 §3.2).
    public static void Melee(World world, Entity entity, JsonNode? options, string where)
    {
        var o = Read<MeleeOptions>(world, options, "melee", where);
        if (o.Attack.IsEmpty && o.Natural.IsEmpty) return;
        var melee = sage_engine.Melee.With(o.Attack.IsEmpty ? o.Natural : o.Attack);
        if (!o.Natural.IsEmpty) melee.Natural = o.Natural;
        world.Add(entity, melee);
    }

    private sealed class InventoryOptions
    {
        public float Capacity;
        public List<Stack> Items = new();
        public sealed class Stack { public RecordId Item; public int Count = 1; }
    }

    // "inventory": { "capacity": 40, "items": [ { "item": "bread", "count": 3 } ] }
    public static void Inventory(World world, Entity entity, JsonNode? options, string where)
    {
        var o = Read<InventoryOptions>(world, options, "inventory", where);
        world.AddInventory(entity, o.Capacity);
        foreach (var stack in o.Items)
        {
            if (stack.Item.IsEmpty) continue;
            if (!world.Give(entity, stack.Item, stack.Count))
                Log.Warn(LogCat.Records, $"{where}: inventory: {stack.Count}x {stack.Item} did not fit");
        }
    }

    // "attributes": {} — health, mana and the rest, built from the attribute records (16 §3.3).
    public static void Attributes(World world, Entity entity, JsonNode? options, string where) =>
        world.AddAttributes(entity);

    private sealed class PickupOptions { public RecordId Item; public int Count = 1; }

    // "pickup": { "item": "sage:sword", "count": 1 }
    //
    // The whole lootable thing from the item record: its sprite, its collider and the `Pickup` that
    // says what taking it gives you (F19).
    public static void Pickup(World world, Entity entity, JsonNode? options, string where)
    {
        var o = Read<PickupOptions>(world, options, "pickup", where);
        if (o.Item.IsEmpty) { Log.Error(LogCat.Records, $"{where}: pickup needs an \"item\""); return; }
        world.MakePickup(entity, o.Item, o.Count);
    }

    // "effects": ["sage:tough_hide"] — what it starts with: a creature's hide as armour, a buff.
    // Applied rather than written as a component so a spell can strip it later (16 §3.3).
    public static void Effects(World world, Entity entity, JsonNode? options, string where)
    {
        if (options is not JsonArray)
        {
            Log.Error(LogCat.Records, $"{where}: \"effects\" must be a list of effect ids");
            return;
        }
        foreach (var effect in Read<List<RecordId>>(world, options, "effects", where))
            if (!effect.IsEmpty) sage_engine.Effects.Apply(world, entity, effect);
    }
}
