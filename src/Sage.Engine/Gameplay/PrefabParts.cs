#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// The prefab parts the engine's own plugins declare (F31, 05 §3.5, issue #17). A part exists only
// where the setup is *not* one component: `Collider`, `RigidBody`, `SpriteRenderer`, `AIState` and
// `Melee` are plain data and belong under "components", so they have no part here. What is left is
// the handful of cases where several components have to agree, or where a record has to be read to
// build them.
//
// Each class is the part's options — its public fields are exactly what a prefab may write under its
// key — and [PrefabPart] registers it for its plugin (Sage.Generators). A game or a mod declares its
// own the same way; PrefabRegistry says what order parts run in.

// ---- physics -------------------------------------------------------------------------------------

// "character": { "layer": "enemy", "profile": "sage:walker" }
//
// A capsule the engine moves, the controller that moves it, and the intent its controller writes —
// four components that have to agree about radius, height and layer, which is why this is a part and
// not four entries under "components".
[PrefabPart("character", Plugin = "sage.gameplay.character")]
public sealed class CharacterPart : IPrefabPart
{
    public string Layer = "";          // a physics layer by name (10 §3.2); empty = the default
    public RecordId Profile;           // movement_profile record; empty = the engine's default

    public void Apply(in PrefabPartContext ctx)
    {
        var layers = ctx.World.Resources.Get<PhysicsSpace>().Layers;
        byte layer = layers.Default;
        if (!string.IsNullOrEmpty(Layer) && !layers.TryIndexOf(Layer, out layer))
            ctx.Warn($"no physics layer '{Layer}'; using '{layers.Name(layer)}'");
        ctx.World.AddCharacter(ctx.Entity, layer, Profile);
    }
}

// "body": { "size": [0.6, 0.6, 0.6], "mass": 8 }
// "body": { "shape": "Capsule", "radius": 0.35, "height": 2.2 }
//
// A part rather than a Collider written by hand because of where a shape sits: a capsule stands *on*
// the placement point, like the art above it, while a box or a sphere is centred on it. Getting that
// wrong buries a crate half in the ground and floats every creature (review #44), and the offset
// lives in Collider.Standing rather than in whoever is writing the JSON.
//
// Each shape is given its own dimensions rather than three numbers meaning different things per
// shape, which is what the Sandbox's old spawn record did and what made review #44 possible.
[PrefabPart("body", Plugin = "sage.physics3d")]
public sealed class BodyPart : IPrefabPart
{
    public ColliderShape Shape = ColliderShape.Box;
    public Vector3 Size;               // box only: full extents
    public float Radius;               // sphere and capsule
    public float Height;               // capsule only: total height, feet to head
    public float Mass;                 // > 0 = a dynamic body that falls; 0 = static
    public bool Trigger;
    public string Layer = "";

    public void Apply(in PrefabPartContext ctx)
    {
        byte layer = 0;
        var layers = ctx.World.Resources.Get<PhysicsSpace>().Layers;
        if (!string.IsNullOrEmpty(Layer) && !layers.TryIndexOf(Layer, out layer))
            ctx.Warn($"no physics layer '{Layer}'");

        Collider collider;
        switch (Shape)
        {
            case ColliderShape.Capsule:
                if (Radius <= 0f || Height <= 0f) { ctx.Error("a capsule body needs \"radius\" and \"height\""); return; }
                collider = Collider.Standing(Radius, Height, layer);
                break;
            case ColliderShape.Sphere:
                if (Radius <= 0f) { ctx.Error("a sphere body needs a \"radius\""); return; }
                collider = Collider.Sphere(Radius, layer);
                break;
            default:
                if (Size == Vector3.Zero) { ctx.Error("a box body needs a \"size\""); return; }
                collider = Collider.Box(Size, layer);
                break;
        }

        collider.IsTrigger = Trigger;
        ctx.World.Add(ctx.Entity, collider);
        ctx.World.Add(ctx.Entity, Mass > 0f ? RigidBody.Dynamic(Mass) : new RigidBody { Kind = BodyKind.Static });
    }
}

// ---- looks ---------------------------------------------------------------------------------------

// "light": { "colour": [1, 0.85, 0.6], "range": 8, "intensity": 1.4 }
//
// A lamp. The engine carries it because a light is a fact about the world rather than about the
// screen — a headless server has lamps and never draws one — and because a map places them by
// classname like anything else (15 §10a, 06 §3.9).
[PrefabPart("light", Plugin = "sage.gameplay.lights")]
public sealed class LightPart : IPrefabPart
{
    public Vector3 Colour;             // zero = white
    public float Range = 8f;
    public float Intensity = 1f;

    public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new PointLight
    {
        Colour = Colour == Vector3.Zero ? Vector3.One : Colour,
        Range = Range <= 0f ? 8f : Range,
        Intensity = Intensity <= 0f ? 1f : Intensity,
    });
}

// "sprite": { "sheet": "goblin", "size": [1.6, 1.9], "animation": "idle" }
//
// A part rather than a SpriteRenderer written by hand for the animation: clips are started by name,
// never by index. Clip 0 is whichever name sorts first, which for a Daggerfall sheet is "attack" —
// asking by index left every creature in the scene standing frozen mid-swing (12 §3).
[PrefabPart("sprite", Plugin = "sage.gameplay.animation")]
public sealed class SpritePart : IPrefabPart
{
    public RecordId Sheet;
    public RecordId Material;
    public Vector2 Size;               // metres; 0 = the sheet's own
    public string Animation = "";      // a clip *name*; empty = don't animate

    public void Apply(in PrefabPartContext ctx)
    {
        if (Sheet.IsEmpty) { ctx.Error("needs a \"sheet\""); return; }
        ctx.World.Add(ctx.Entity, new SpriteRenderer { Sheet = Sheet, Material = Material, Size = Size });

        if (string.IsNullOrEmpty(Animation)) return;
        var records = ctx.World.Resources.Get<RecordStore>();
        if (!records.TryGet(Sheet, out SpriteSheetRecord sheet)) return;
        int clip = sheet.ClipIndex(Animation);
        if (clip < 0)
        {
            ctx.Warn($"sheet {Sheet} has no clip '{Animation}'");
            return;
        }
        ctx.World.Add(ctx.Entity, SpriteAnimator.Play(clip));
    }
}

// ---- gameplay ------------------------------------------------------------------------------------

// "attributes": {} — health, mana and the rest, built from the attribute records (16 §3.3).
[PrefabPart("attributes", Plugin = "sage.gameplay.attributes")]
public sealed class AttributesPart : IPrefabPart
{
    public void Apply(in PrefabPartContext ctx) => ctx.World.AddAttributes(ctx.Entity);
}

// "effects": ["sage:tough_hide"] — what it starts with: a creature's hide as armour, a buff. Applied
// rather than written as a component so a spell can strip it later (16 §3.3). After `attributes`,
// because an effect changes attributes and never adds the components that hold them (Effects.Apply).
[PrefabPart("effects", Plugin = "sage.gameplay.attributes", After = new[] { "attributes" }, Shorthand = nameof(Ids))]
public sealed class EffectsPart : IPrefabPart
{
    public List<RecordId> Ids = new();

    public void Apply(in PrefabPartContext ctx)
    {
        foreach (var effect in Ids)
            if (!effect.IsEmpty) Effects.Apply(ctx.World, ctx.Entity, effect);
    }
}

// "melee": { "attack": "sage:claw" }
//
// A part rather than a `Melee` component so that one id fills both what it is swinging and what it
// swings bare-handed: writing only `attack` and leaving `natural` empty means disarming it leaves it
// unable to fight at all, which is never what was meant (16 §3.2).
[PrefabPart("melee", Plugin = "sage.gameplay.combat")]
public sealed class MeleePart : IPrefabPart
{
    public RecordId Attack;
    public RecordId Natural;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Attack.IsEmpty && Natural.IsEmpty) return;
        var melee = Melee.With(Attack.IsEmpty ? Natural : Attack);
        if (!Natural.IsEmpty) melee.Natural = Natural;
        ctx.World.Add(ctx.Entity, melee);
    }
}

// "inventory": { "capacity": 40, "items": [ { "item": "bread", "count": 3 } ] }
[PrefabPart("inventory", Plugin = "sage.gameplay.items")]
public sealed class InventoryPart : IPrefabPart
{
    public float Capacity;
    public List<Stack> Items = new();

    public sealed class Stack { public RecordId Item; public int Count = 1; }

    public void Apply(in PrefabPartContext ctx)
    {
        ctx.World.AddInventory(ctx.Entity, Capacity);
        foreach (var stack in Items)
        {
            if (stack.Item.IsEmpty) continue;
            if (!ctx.World.Give(ctx.Entity, stack.Item, stack.Count))
                ctx.Warn($"{stack.Count}x {stack.Item} did not fit");
        }
    }
}

// "pickup": { "item": "sage:sword", "count": 1 }
//
// The whole lootable thing from the item record: its sprite, its collider and the `Pickup` that says
// what taking it gives you (F19). After `sprite` and `body`, because it adds its own of each only when
// the prefab gave it none (Items.Decorate).
[PrefabPart("pickup", Plugin = "sage.gameplay.items", After = new[] { "sprite", "body" })]
public sealed class PickupPart : IPrefabPart
{
    public RecordId Item;
    public int Count = 1;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Item.IsEmpty) { ctx.Error("needs an \"item\""); return; }
        ctx.World.MakePickup(ctx.Entity, Item, Count);
    }
}

// "abilities": ["fireball", "heal"] — what it can cast (16 §3.3, F21).
[PrefabPart("abilities", Plugin = "sage.gameplay.abilities", Shorthand = nameof(Ids))]
public sealed class AbilitiesPart : IPrefabPart
{
    public List<RecordId> Ids = new();

    public void Apply(in PrefabPartContext ctx)
    {
        foreach (var ability in Ids)
            if (!ability.IsEmpty) ctx.World.Teach(ctx.Entity, ability);
    }
}

// "faction": "sandbox:townsfolk" — who it belongs to (16 §3.5, F24). A bare id, because a faction is
// one thing and a part that takes an object for a single value is a form to fill in.
[PrefabPart("faction", Plugin = "sage.gameplay.factions", Shorthand = nameof(Id))]
public sealed class FactionPart : IPrefabPart
{
    public RecordId Id;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Id.IsEmpty) { ctx.Error("needs a record id"); return; }
        ctx.World.Add(ctx.Entity, new Faction { Id = Id });
    }
}

// "dialogue": "sandbox:innkeeper" — somebody worth talking to (16 §3.5, F24). It also makes the entity
// `Interactable`, because "you can talk to it" and "Use does something" are the same claim.
[PrefabPart("dialogue", Plugin = "sage.gameplay.factions", Shorthand = nameof(Id))]
public sealed class DialoguePart : IPrefabPart
{
    public RecordId Id;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Id.IsEmpty) { ctx.Error("needs a record id"); return; }
        ctx.World.Add(ctx.Entity, new Dialogue { Record = Id });
        ctx.Entity.AddTag<Interactable>();
    }
}
