#nullable enable
using System.Collections.Generic;
using System.Numerics;
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

    private sealed class BodyOptions
    {
        public ColliderShape Shape = ColliderShape.Box;
        public Vector3 Size;               // box only: full extents
        public float Radius;               // sphere and capsule
        public float Height;               // capsule only: total height, feet to head
        public float Mass;                 // > 0 = a dynamic body that falls; 0 = static
        public bool Trigger;
        public string Layer = "";
    }

    // "body": { "size": [0.6, 0.6, 0.6], "mass": 8 }
    // "body": { "shape": "Capsule", "radius": 0.35, "height": 2.2 }
    //
    // A part rather than a Collider written by hand because of where a shape sits: a capsule stands
    // *on* the placement point, like the art above it, while a box or a sphere is centred on it.
    // Getting that wrong buries a crate half in the ground and floats every creature (review #44),
    // and the offset lives in Collider.Standing rather than in whoever is writing the JSON.
    //
    // Each shape is given its own dimensions rather than three numbers meaning different things per
    // shape, which is what the Sandbox's old spawn record did and what made review #44 possible.
    public static void Body(World world, Entity entity, JsonNode? options, string where)
    {
        var o = Read<BodyOptions>(world, options, "body", where);

        byte layer = 0;
        var layers = world.Resources.Get<PhysicsSpace>().Layers;
        if (!string.IsNullOrEmpty(o.Layer) && !layers.TryIndexOf(o.Layer, out layer))
            Log.Warn(LogCat.Records, $"{where}: body: no physics layer '{o.Layer}'");

        Collider collider;
        switch (o.Shape)
        {
            case ColliderShape.Capsule:
                if (o.Radius <= 0f || o.Height <= 0f) { Log.Error(LogCat.Records, $"{where}: a capsule body needs \"radius\" and \"height\""); return; }
                collider = Collider.Standing(o.Radius, o.Height, layer);
                break;
            case ColliderShape.Sphere:
                if (o.Radius <= 0f) { Log.Error(LogCat.Records, $"{where}: a sphere body needs a \"radius\""); return; }
                collider = Collider.Sphere(o.Radius, layer);
                break;
            default:
                if (o.Size == Vector3.Zero) { Log.Error(LogCat.Records, $"{where}: a box body needs a \"size\""); return; }
                collider = Collider.Box(o.Size, layer);
                break;
        }

        collider.IsTrigger = o.Trigger;
        world.Add(entity, collider);
        world.Add(entity, o.Mass > 0f ? RigidBody.Dynamic(o.Mass) : new RigidBody { Kind = BodyKind.Static });
    }

    private sealed class SpriteOptions
    {
        public RecordId Sheet;
        public RecordId Material;
        public Vector2 Size;               // metres; 0 = the sheet's own
        public string Animation = "";      // a clip *name*; empty = don't animate
    }

    // "sprite": { "sheet": "goblin", "size": [1.6, 1.9], "animation": "idle" }
    //
    // A part rather than a SpriteRenderer written by hand for the animation: clips are started by
    // name, never by index. Clip 0 is whichever name sorts first, which for a Daggerfall sheet is
    // "attack" — asking by index left every creature in the scene standing frozen mid-swing (12 §3).
    public static void Sprite(World world, Entity entity, JsonNode? options, string where)
    {
        var o = Read<SpriteOptions>(world, options, "sprite", where);
        if (o.Sheet.IsEmpty) { Log.Error(LogCat.Records, $"{where}: sprite needs a \"sheet\""); return; }
        world.Add(entity, new SpriteRenderer { Sheet = o.Sheet, Material = o.Material, Size = o.Size });

        if (string.IsNullOrEmpty(o.Animation)) return;
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(o.Sheet, out SpriteSheetRecord sheet)) return;
        int clip = sheet.ClipIndex(o.Animation);
        if (clip < 0)
        {
            Log.Warn(LogCat.Records, $"{where}: sprite: sheet {o.Sheet} has no clip '{o.Animation}'");
            return;
        }
        world.Add(entity, SpriteAnimator.Play(clip));
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
