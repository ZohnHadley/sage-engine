#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Footsteps (docs/design/10 "As built (surfaces)", issue #270): every step looks at what is underfoot
// and makes that surface's noise. A step is taken
//   - every `stride` metres a grounded entity walks (0 turns this off), and
//   - whenever its animation raises a `footstep` event (anim_events, a sprite sheet's frame events),
// and then a short ray down from the feet (IPhysicsWorld.Raycast, any backend) finds the ground and its
// surface (RayHit.Surface, a physics_material). The step sends
//   - `Footstep` (who, where, on what), for whatever listens: AI hearing, a game's own rules; and
//   - the surface's `footstep` cue as CueTriggered, which the client plays (its sound, its dust).
// A surface with no footstep cue, or ground with no surface, is a silent step that still sends Footstep.
//
//   part "footsteps": { "stride": 1.6, "reach": 0.4 }
//
// Content, not state: the component is [Transient], and the part puts it back when a load respawns the
// prefab. Allocation-free per tick.

// A step was taken: by whom, where the foot came down (on the ground, when the ray found it) and on
// what (empty when nothing said). Sent in Phase.Late.
[GameEvent]
public readonly record struct Footstep(Entity Entity, Vector3 Point, RecordId Surface);

[Transient]
[Component("sage:footsteps")]
public struct Footsteps : IComponent
{
    [Property(Min = 0, Unit = "m", Tooltip = "Metres walked between steps; 0 = only the animation's footstep events")]
    public float Stride;
    [Property(Min = 0, Unit = "m", Tooltip = "How far below the feet the ground is looked for")]
    public float Reach;

    // How far it has walked since its last step, and where it was last tick.
    internal float Travelled;
    internal Vector3 Last;
    internal bool Started;
    // What it last stepped on, for debugging and tests.
    public RecordId LastSurface;
}

[PrefabPart("footsteps", Plugin = "sage.gameplay.animation")]
public sealed class FootstepsPart : IPrefabPart
{
    [Property(Min = 0, Unit = "m", Tooltip = "Metres walked between steps; 0 = only the animation's footstep events")]
    public float Stride = 1.6f;
    [Property(Min = 0, Unit = "m", Tooltip = "How far below the feet the ground is looked for")]
    public float Reach = 0.4f;

    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new Footsteps { Stride = MathF.Max(0f, Stride), Reach = MathF.Max(0f, Reach) });
}

// Late phase: after the move (PrePhysics) and after animation has raised this tick's events.
[System(Id, Phase.Late)]
internal sealed class FootstepSystem : ISystem
{
    public const string Id = "sage.animation.footsteps";
    public const string EventName = "footstep";

    // A move longer than this in one tick is a teleport, not a walk: no step for it.
    private const float Teleport = 4f;
    // The ray starts this far above the feet, so a foot a little below the surface still finds it.
    private const float Above = 0.25f;

    private readonly World _world;
    private readonly RecordStore _records;
    private readonly Query<Transform, Footsteps> _walkers;
    private readonly EventReader<AnimationEvent> _animation;
    private readonly IPhysicsWorld? _physics;
    private readonly bool _materials;

    public FootstepSystem(World world, RecordStore records)
    {
        _world = world;
        _records = records;
        _walkers = world.Query<Transform, Footsteps>();
        _animation = world.Events.Reader<AnimationEvent>(this);
        world.Resources.TryGet(out _physics);   // a world may have no physics at all
        _materials = records.TypeNameOf(typeof(PhysicsMaterialRecord)) != null;
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        foreach (var (transforms, steps, entities) in _walkers.Chunks)
        {
            var t = transforms.Span;
            var s = steps.Span;
            for (int n = 0; n < s.Length; n++)
            {
                ref var step = ref s[n];
                var at = t[n].LocalPosition;
                if (!step.Started) { step.Last = at; step.Started = true; continue; }

                var moved = at - step.Last;
                step.Last = at;
                float distance = MathF.Sqrt(moved.X * moved.X + moved.Z * moved.Z);
                if (step.Stride <= 0f || distance > Teleport) continue;

                var entity = entities.EntityAt(n);
                bool grounded = !world.TryGet<CharacterController>(entity, out var character) || character.Grounded;
                if (!grounded) continue;

                step.Travelled += distance;
                if (step.Travelled < step.Stride) continue;
                step.Travelled -= step.Stride * MathF.Floor(step.Travelled / step.Stride);
                Take(world, entity, at, ref step);
            }
        }

        foreach (ref readonly var e in _animation.Read())
        {
            if (!string.Equals(e.Name, EventName, StringComparison.Ordinal) || !world.IsAlive(e.Entity)) continue;
            if (!world.Has<Footsteps>(e.Entity) || !world.Has<Transform>(e.Entity)) continue;
            ref var step = ref world.Get<Footsteps>(e.Entity);
            step.Travelled = 0f;
            Take(world, e.Entity, world.Get<Transform>(e.Entity).LocalPosition, ref step);
        }
    }

    private void Take(World world, Entity entity, Vector3 feet, ref Footsteps step)
    {
        var point = feet;
        RecordId surface = default;
        if (_physics != null)
        {
            var hit = _physics.Raycast(feet + new Vector3(0f, Above, 0f), -Vector3.UnitY, Above + MathF.Max(step.Reach, 0.05f), ignore: entity);
            if (hit.Hit) { point = hit.Position; surface = hit.Surface; }
        }
        step.LastSurface = surface;
        world.Events.Send(new Footstep(entity, point, surface));

        if (!_materials || surface.IsEmpty || !_records.TryGet(surface, out PhysicsMaterialRecord material) || material.Footstep.IsEmpty) return;
        world.Events.Send(new CueTriggered(material.Footstep, entity, point));
    }
}
