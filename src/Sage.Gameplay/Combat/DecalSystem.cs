#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Where marks go (issue #306; the pool is Sage.Simulation's `Decals`, the drawing the client's).
//
// A decal is named the way a particle effect is: a **cue** names one (`decal` beside `particles`), and
// so does a **damage type**. This system reads the same two events the client's particle system reads
// and puts the mark on a surface:
//   - a cue with a `Normal` (an impact: a shot meeting a wall, a physics_material's `impact` cue) lays it
//     on the surface it names, found by a short ray back into it;
//   - a cue with none (a fireball's burst, a footstep) lays it on what is below its point, within the
//     decal's `reach`;
//   - a hit lays its damage type's decal behind what it hurt, along the blow within `reach` — blood on
//     the wall — and failing that on the floor below.
// Marks land on what stays put — brushes, terrain, static meshes and props — never on a character, a
// body physics moves or a mover (a door), where a mark would hang in the air once it moved.
//
// It runs only in a world that has a `Decals` pool, which the client adds (and a test may): a headless
// server has none and reads its events into nothing. Fixed rate, after gameplay has sent this tick's
// hits; the pool ages here too, so a paused game's marks wait with it.
[System("sage.combat.decals", Phase.Late)]
internal sealed class DecalSystem : ISystem
{
    // A surface the cue names is looked for from this far in front of it, and this far behind.
    private const float Back = 0.1f;
    private const float Into = 0.6f;

    private readonly RecordStore _records;
    private readonly EventReader<CueTriggered> _cues;
    private readonly EventReader<Damaged> _damage;
    private readonly IPhysicsWorld? _space;
    private readonly bool _typed;

    public DecalSystem(World world, RecordStore records)
    {
        _records = records;
        _cues = world.Events.Reader<CueTriggered>(this);
        _damage = world.Events.Reader<Damaged>(this);
        world.Resources.TryGet(out _space);   // a world may have no physics: then no surface to find
        // A server registers no decal records (they are the client's, like particles).
        _typed = records.TypeNameOf(typeof(DecalRecord)) != null && records.TypeNameOf(typeof(CueRecord)) != null;

        // Everything that holds a position follows the world (R6).
        world.Origin().Rebased += offset =>
        {
            if (world.Resources.TryGet<Decals>(out var decals) && decals != null) decals.Rebase(offset);
        };
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        if (!world.Resources.TryGet<Decals>(out var decals) || decals == null || !_typed || _space == null)
        {
            // Drained anyway: a reader that stops reading holds the queue open for everybody (04 §3.1).
            _cues.Read();
            _damage.Read();
            return;
        }

        decals.Update(world, ctx.Tick.Dt);
        foreach (ref readonly var cue in _cues.Read()) Cue(world, decals, in cue);
        foreach (ref readonly var hit in _damage.Read()) Hit(world, decals, in hit);
    }

    private void Cue(World world, Decals decals, in CueTriggered cue)
    {
        if (!_records.TryGet(cue.Cue, out CueRecord record) || record.Decal.IsEmpty) return;
        if (!_records.TryGet(record.Decal.Id, out DecalRecord decal)) return;

        if (cue.Normal.LengthSquared() > 1e-6f)
        {
            var normal = Vector3.Normalize(cue.Normal);
            Land(world, decals, record.Decal.Id, decal, cue.Point + normal * Back, -normal, Back + Into, cue.Source);
        }
        else
        {
            Land(world, decals, record.Decal.Id, decal, cue.Point + Vector3.UnitY * Back, -Vector3.UnitY, Back + decal.Reach, cue.Source);
        }
    }

    private void Hit(World world, Decals decals, in Damaged hit)
    {
        if (hit.Applied <= 0f) return;
        var typeId = hit.Hit.Type.IsEmpty ? GameplayConventions.Of(_records).DamageType.Id : hit.Hit.Type;
        if (typeId.IsEmpty || !_records.TryGet(typeId, out DamageTypeRecord type) || type.Decal.IsEmpty) return;
        if (!_records.TryGet(type.Decal.Id, out DecalRecord decal) || decal.Reach <= 0f) return;

        var target = hit.Hit.Target;
        var blow = hit.Hit.Direction;
        if (blow.LengthSquared() > 1e-6f
            && Land(world, decals, type.Decal.Id, decal, hit.Hit.Point, Vector3.Normalize(blow), decal.Reach, target))
            return;
        Land(world, decals, type.Decal.Id, decal, hit.Hit.Point, -Vector3.UnitY, decal.Reach, target);
    }

    private bool Land(World world, Decals decals, RecordId id, DecalRecord decal, Vector3 from, Vector3 direction,
                      float distance, Entity ignore)
    {
        var hit = _space!.Raycast(from, direction, distance, ignore: ignore);
        if (!hit.Hit || !Holds(world, hit.Entity)) return false;
        return decals.Place(id, decal, hit.Position, hit.Normal, surface: hit.Entity);
    }

    // Whether a mark may stay on this: things that do not move. A character walks off, a dynamic or
    // kinematic body is moved, a mover is a door; what can be hurt is a creature (or a crate that breaks).
    internal static bool Holds(World world, Entity entity)
    {
        if (entity.IsNull) return true;   // the level itself, when the backend names no entity
        if (!world.IsAlive(entity)) return false;
        if (world.Has<CharacterController>(entity) || world.Has<Attributes>(entity) || world.Has<Mover>(entity)) return false;
        if (world.TryGet<RigidBody>(entity, out var body) && body.Kind != BodyKind.Static) return false;
        if (world.TryGet<PhysicsBody>(entity, out var physics) && !physics.IsStatic) return false;
        return true;
    }
}
