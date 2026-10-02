#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Gameplay;

// Hit locations (issue #137, phase 4e; docs/design/16 "As built (hit locations)"): where on a body a
// strike landed, and what that changes. A head shot hurts twice as much and a helmet stops half of it:
//
//   hit_location   what a place on a body means: a damage multiplier, the attribute that armours it
//                  (`armor_head`), effects a hit there applies, and tags a game's rules read (`vital`)
//   hitboxes       the boxes on one model's skeleton, keyed by the model like `skeleton_sockets`: each
//                  a location, a bone or socket, a shape, a size and an offset from the joint
//   the `hitboxes` part  puts them on an entity: one child per box, following its bone (bone_attachment),
//                  with a collider on the query-only `hitbox` physics layer and a `Hitbox` naming its owner
//
// A strike's query (Hits.Ray, Hits.Sweep) finds the first solid thing and the first hitbox; a hitbox in
// front of that solid thing, or inside it when it is the hitbox's own owner's capsule, is where the
// strike landed. Combat.ApplyHit then deals damage × the location's multiplier × (1 − its resist) × (1 −
// the damage type's resist). A strike on a capsule that misses every hitbox — and on anything with no
// hitboxes at all, a sprite creature — lands on the body: an empty location, no multiplier.
//
// Armour is data: a helmet is an item whose `effects` add `armor_head` while it is worn (Items.Equip).

// What a place on a body means when a strike lands there.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[Record("hit_location", Plugin = "sage.gameplay.combat")]
public sealed class HitLocationRecord
{
    [Property(Min = 0, Tooltip = "The strike's damage is multiplied by this here: 2 for a head, 0.5 for a hand")]
    public float DamageMultiplier = 1f;
    [Property(Tooltip = "An attribute read as a percentage (0..95) of the damage stopped here, before the damage type's own resist: armor_head")]
    public RecordRef<AttributeRecord> Resist;
    [Property(Tooltip = "Applied to the victim when a strike here gets through: a leg shot that slows, a head shot that dazes")]
    public List<RecordRef<EffectRecord>> Effects = new();
    [Property(Tooltip = "What a game's rules read about this place (HitLocations.HasTag): vital, limb")]
    public List<RecordRef<TagRecord>> Tags = new();
}

// One box on a skeleton: which location it is, what it follows and how big it is.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public sealed class HitboxShape
{
    [Property(Tooltip = "The hit_location a strike on this box lands on")]
    public RecordRef<HitLocationRecord> Location;
    [Property(Tooltip = "The joint it follows, by name as the model writes it")]
    public string Bone = "";
    [Property(Tooltip = "Or a socket of the model (skeleton_sockets), which wins over the bone")]
    public string Socket = "";
    [Property(Tooltip = "Box, Sphere or Capsule, in the joint's space")]
    public ColliderShape Shape = ColliderShape.Box;
    [Property(Min = 0, Unit = "m", Tooltip = "Box: full extents. Sphere: X is the radius. Capsule: X radius, Y cylinder length (along the joint's Y)")]
    public Vector3 Size;
    [Property(Unit = "m", Tooltip = "Where its centre sits in the joint's space")]
    public Vector3 Offset;
}

// The hitboxes of one model, by name ("head", "forearm_l"). Several records may name the same model (a
// mod adds a tail): every record's boxes are spawned, in id order, and a name already taken is skipped.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[Record("hitboxes", Plugin = "sage.gameplay.combat")]
public sealed class HitboxesRecord
{
    [Property(Tooltip = "The skinned model (.glb) whose skeleton these boxes are on")]
    [AssetKind("mesh")] public AssetPath Model;
    [Property(Tooltip = "The boxes by name")]
    public Dictionary<string, HitboxShape> Boxes = new(StringComparer.OrdinalIgnoreCase);
}

// On a hitbox entity: whose it is and which location. Never saved: the `hitboxes` part puts the boxes
// back when the owner is rebuilt from its prefab, as it does its capsule.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[Transient]
[Component("sage:hitbox")]
public struct Hitbox : IComponent
{
    [Property(Tooltip = "What takes the damage when this box is struck")]
    public Entity Owner;
    [RecordRef("hit_location"), Property(Tooltip = "Where on the owner this box is")]
    public RecordId Location;
}

// "hitboxes": {}  or  "hitboxes": "models/knight.glb"
// One child entity per box of the model's `hitboxes` records: a kinematic collider on the `hitbox` layer
// (query-only: it pushes nothing and only a strike's hitbox query sees it), a bone_attachment to its
// joint, and a Hitbox naming the owner. The model is the part's own, else the skinned_mesh's, else the
// animator's. The boxes follow the pose the Late phase leaves (AttachmentSystem), so a strike in the
// Gameplay phase meets the previous tick's bones on the owner's current position (16 "As built (hit locations)").
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[PrefabPart("hitboxes", Plugin = "sage.gameplay.combat", After = new[] { "skinned_mesh", "animator" }, Shorthand = nameof(Model))]
public sealed class HitboxesPart : IPrefabPart
{
    [AssetKind("mesh"), Property(Tooltip = "The model whose hitboxes records to use; left out, the skinned_mesh's or the animator's")]
    public AssetPath Model;

    public void Apply(in PrefabPartContext ctx)
    {
        var world = ctx.World;
        var model = Model;
        if (model.IsEmpty && world.TryGet<SkinnedMeshRenderer>(ctx.Entity, out var skinned)) model = skinned.Mesh;
        if (model.IsEmpty && world.TryGet<Animator>(ctx.Entity, out var animator)) model = animator.Model;
        if (model.IsEmpty)
        {
            ctx.Error("needs a \"model\" or a skinned_mesh or animator beside it: hitboxes are looked up by model");
            return;
        }
        if (!world.Resources.TryGet<IPhysicsWorld>(out var space) || space == null)
        {
            ctx.Error("needs physics: hitboxes are colliders");
            return;
        }
        int layer = space.Layers.IndexOf(Hitboxes.Layer);
        if (layer < 0)
        {
            ctx.Error($"physics_layers has no '{Hitboxes.Layer}' layer (the engine's has one; a game that replaces the record needs it back)");
            return;
        }
        if (Hitboxes.Spawn(world, ctx.Entity, model, (byte)layer) == 0)
            ctx.Warn($"no hitboxes record for '{model.Path}': it is hit on its body only");
    }
}

// Hitboxes from code, and where a strike landed.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public static class Hitboxes
{
    // The physics layer hitboxes live on: query-only (engine_content/data/physics.json).
    public const string Layer = "hitbox";

    // Gives `owner` the boxes of every `hitboxes` record for `model` (see HitboxesPart) and returns how
    // many. Content time: it allocates.
    public static int Spawn(World world, Entity owner, AssetPath model, byte layer)
    {
        ArgumentNullException.ThrowIfNull(world);
        var records = world.Resources.Get<RecordStore>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (var record in records.All<HitboxesRecord>())
        {
            if (record.Model != model) continue;
            foreach (var (name, box) in record.Boxes)
            {
                if (box == null || !taken.Add(name)) continue;
                var child = world.Create(Transform.Identity, $"{owner.Name ?? "entity"} hitbox {name}");
                world.SetParent(child, owner);
                world.Add(child, new BoneAttachment { Socket = box.Socket ?? "", Bone = box.Bone ?? "", Offset = box.Offset });
                world.Add(child, new Collider { Shape = box.Shape, Size = box.Size, Layer = layer });
                world.Add(child, RigidBody.Kinematic());
                world.Add(child, new Hitbox { Owner = owner, Location = box.Location.Id });
                count++;
            }
        }
        if (count > 0) owner.AddComponent(new HitboxSet { On = true, Want = true });   // the budget's (HitboxBudget.cs)
        return count;
    }

    // The hitbox layer as a query mask, or none when this world's physics has no such layer.
    public static LayerMask Mask(IPhysicsWorld space)
    {
        ArgumentNullException.ThrowIfNull(space);
        int layer = space.Layers.IndexOf(Layer);
        return new LayerMask(layer < 0 ? 0u : 1u << layer);
    }

    // A landing on a hitbox, as a landing on its owner at its location (what Combat.ApplyHit deals);
    // anything else as it is. A location the result already names wins.
    public static HitResult Resolve(in HitResult result)
    {
        if (result.Target.IsNull || !result.Target.TryGetComponent<Hitbox>(out var box) || box.Owner.IsNull) return result;
        return result with { Target = box.Owner, Location = result.Location.IsEmpty ? box.Location : result.Location };
    }

    // Did a hitbox query (`box`, on the hitbox layer alone) find where a strike that met `solid` (or
    // nothing) landed? Yes when the box is someone's other than the striker's and it is in front of the
    // solid thing — an arm held out past a capsule, or a creature with no capsule at all — or inside it
    // when the solid thing is the box's own owner (the strike went into the capsule and met the head).
    internal static bool Landed(Entity attacker, Entity boxEntity, float boxDistance, Entity solid, float solidDistance, out Hitbox box)
    {
        box = default;
        if (boxEntity.IsNull || !boxEntity.TryGetComponent(out box) || box.Owner.IsNull || box.Owner == attacker) return false;
        return solid.IsNull || boxDistance <= solidDistance || box.Owner == solid;
    }

    // Content checks (a mistake is a load error, at its line; issue #22).
    internal static void Check(HitboxesRecord record, RecordCheck check)
    {
        if (record.Model.IsEmpty) check.Error("model", "names no model: hitboxes are looked up by the model their skeleton comes from");
        foreach (var (name, box) in record.Boxes)
        {
            if (box == null) { check.Error($"boxes.{name}", "is empty"); continue; }
            if (box.Location.IsEmpty) check.Error($"boxes.{name}.location", "names no hit_location");
            if (string.IsNullOrWhiteSpace(box.Bone) && string.IsNullOrWhiteSpace(box.Socket))
                check.Error($"boxes.{name}", "names neither a bone nor a socket to follow");
            bool sized = box.Shape switch
            {
                ColliderShape.Box => box.Size.X > 0f && box.Size.Y > 0f && box.Size.Z > 0f,
                ColliderShape.Sphere => box.Size.X > 0f,
                ColliderShape.Capsule => box.Size.X > 0f && box.Size.Y >= 0f,
                _ => false,
            };
            if (box.Shape == ColliderShape.Mesh) check.Error($"boxes.{name}.shape", "a hitbox is a Box, a Sphere or a Capsule");
            else if (!sized) check.Error($"boxes.{name}.size", $"is not a {box.Shape}'s size (a box: three extents; a sphere: X the radius; a capsule: X the radius)");
        }
    }
}

// Reading a location.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public static class HitLocations
{
    // Does `location` carry `tag` (`Damaged.Location`, say, and `vital`)? False for the body (empty).
    public static bool HasTag(RecordStore records, RecordId location, RecordId tag)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (location.IsEmpty || !records.TryGet(location, out HitLocationRecord record)) return false;
        foreach (var t in record.Tags)
            if (t.Id == tag) return true;
        return false;
    }
}

// PrePhysics, before the physics sync: a hitbox whose owner is gone goes with it, before it can get (or
// keep) a body. Allocation-free when there is nothing to do.
[System(Id, Phase.PrePhysics, Before = new[] { "?sage.physics.sync" })]
internal sealed class HitboxCleanupSystem : ISystem
{
    public const string Id = "sage.combat.hitboxes";

    private readonly Query<Hitbox> _boxes;
    private readonly List<Entity> _orphans = new();

    public HitboxCleanupSystem(World world) => _boxes = world.Query<Hitbox>();

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        foreach (var (boxes, entities) in _boxes.Chunks)
        {
            var b = boxes.Span;
            for (int n = 0; n < b.Length; n++)
                if (!world.IsAlive(b[n].Owner)) _orphans.Add(entities.EntityAt(n));
        }
        if (_orphans.Count == 0) return;
        foreach (var orphan in _orphans)
            if (world.IsAlive(orphan)) world.Destroy(orphan);
        _orphans.Clear();
    }
}
