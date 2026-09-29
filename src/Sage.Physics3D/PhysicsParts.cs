#nullable enable
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Physics3D;

// The physics plugins' prefab parts (F31, 05 §3.5, issue #17); the rest are in Sage.Gameplay's
// PrefabParts.cs, which says what a part is for.

// ---- physics -------------------------------------------------------------------------------------

// "character": { "layer": "enemy", "profile": "sage:walker" }
//
// A capsule the engine moves, the controller that moves it, and the intent its controller writes —
// four components that have to agree about radius, height and layer, which is why this is a part and
// not four entries under "components".
[PrefabPart("character", Plugin = "sage.gameplay.character")]
public sealed class CharacterPart : IPrefabPart
{
    [Property(Tooltip = "Physics layer by name (player, enemy); empty = default")]
    public string Layer = "";          // a physics layer by name (10 §3.2); empty = the default
    [Property(Tooltip = "How it moves; empty = the engine's default")]
    public RecordRef<MovementProfileRecord> Profile; // movement_profile record; empty = the engine's default

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
    [Property(Category = "Shape", Tooltip = "Box, Sphere or Capsule; a capsule stands on the origin")]
    public ColliderShape Shape = ColliderShape.Box;
    [Property(Category = "Shape", Min = 0, Unit = "m", Tooltip = "Box only: full extents")]
    public Vector3 Size;               // box only: full extents
    [Property(Category = "Shape", Min = 0, Unit = "m", Tooltip = "Sphere and capsule")]
    public float Radius;               // sphere and capsule
    [Property(Category = "Shape", Min = 0, Unit = "m", Tooltip = "Capsule only: total height, feet to head")]
    public float Height;               // capsule only: total height, feet to head
    [Property(Category = "Physics", Min = 0, Unit = "kg", Tooltip = "Above 0 it is a dynamic body that falls; 0 is static")]
    public float Mass;                 // > 0 = a dynamic body that falls; 0 = static
    [Property(Category = "Physics", Tooltip = "Reports overlaps and never blocks")]
    public bool Trigger;
    [Property(Category = "Physics", Tooltip = "Physics layer by name; empty = default")]
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
