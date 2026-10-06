#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Footsteps (docs/design/10 "As built (surfaces)", issues #270 and #327): every step looks at what is
// underfoot and makes that surface's noise. A step is taken
//   - every `stride` metres a grounded entity walks (0 turns this off: a skeletal walker steps on its
//     clips' events instead), and
//   - whenever its animation raises the part's `event` ("footstep"), `left` ("foot_left") or `right`
//     ("foot_right") event (anim_events, a sprite sheet's frame events); the two feet's events look
//     under that foot, where foot IK puts it, when the entity has `foot_ik` and a pose;
// and an entity with a character controller also makes a noise when it jumps (it leaves the ground
// going up) and when it lands (it reaches the ground falling faster than `landSpeed`). A short ray down
// (IPhysicsWorld.Raycast, any backend) finds the ground and its surface (RayHit.Surface, a
// physics_material: a collider's, a brush texture's, a terrain layer's). The step sends
//   - `Footstep` (who, where, on what, and whether it was a step, a landing or a jump), for whatever
//     listens: AI hearing, a game's own rules; and
//   - the surface's cue as CueTriggered, which the client plays (its sound, picked from the sound's
//     variations with its jitter; its dust): `footstep`, or `land`/`jump`, which fall back to its
//     `footstep`. Ground with no surface, or a surface with no cue, uses the cues of the conventions'
//     `surface` (the engine's `sage:default` physics_material), which a game patches to say what
//     unmarked ground sounds like.
//
//   part "footsteps": { "stride": 1.6, "reach": 0.4, "landSpeed": 2.5,
//                       "event": "footstep", "left": "foot_left", "right": "foot_right" }
//
// Content, not state: the component is [Transient], and the part puts it back when a load respawns the
// prefab. Allocation-free per tick.

// What a Footstep was.
public enum FootstepKind : byte
{
    Step,   // a stride walked, or a clip's footstep event
    Land,   // reached the ground from a fall or a jump
    Jump,   // left the ground going up
}

// A step was taken: by whom, where the foot came down (on the ground, when the ray found it) and on
// what (empty when nothing said), and what kind. Sent in Phase.Late.
[GameEvent]
public readonly record struct Footstep(Entity Entity, Vector3 Point, RecordId Surface)
{
    public FootstepKind Kind { get; init; }
}

[Transient]
[Component("sage:footsteps")]
public struct Footsteps : IComponent
{
    [Property(Min = 0, Unit = "m", Tooltip = "Metres walked between steps; 0 = only the animation's footstep events")]
    public float Stride;
    [Property(Min = 0, Unit = "m", Tooltip = "How far below the feet the ground is looked for")]
    public float Reach;
    [Property(Min = 0, Unit = "m/s", Tooltip = "How fast a character must be falling when it reaches the ground for a landing noise")]
    public float LandSpeed;
    // The animation events that are steps: null is the default name ("footstep", "foot_left",
    // "foot_right"), empty is none.
    [Property(Tooltip = "The animation event that is a step under the entity's feet; empty = none")]
    public string? Event;
    [Property(Tooltip = "The animation event that is a step of the left foot (under foot_ik's left foot); empty = none")]
    public string? Left;
    [Property(Tooltip = "The animation event that is a step of the right foot (under foot_ik's right foot); empty = none")]
    public string? Right;

    // How far it has walked since its last step, and where it was last tick.
    internal float Travelled;
    internal Vector3 Last;
    internal bool Started;
    // A character's last tick: on the ground, and the fastest it has fallen since it left it.
    internal bool WasGrounded;
    internal float Falling;
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
    [Property(Min = 0, Unit = "m/s", Tooltip = "How fast a character must be falling when it reaches the ground for a landing noise")]
    public float LandSpeed = FootstepSystem.DefaultLandSpeed;
    [Property(Tooltip = "The animation event that is a step under the entity's feet; empty = none")]
    public string Event = FootstepSystem.EventName;
    [Property(Tooltip = "The animation event that is a step of the left foot (under foot_ik's left foot); empty = none")]
    public string Left = FootstepSystem.LeftEventName;
    [Property(Tooltip = "The animation event that is a step of the right foot (under foot_ik's right foot); empty = none")]
    public string Right = FootstepSystem.RightEventName;

    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new Footsteps
        {
            Stride = MathF.Max(0f, Stride),
            Reach = MathF.Max(0f, Reach),
            LandSpeed = MathF.Max(0f, LandSpeed),
            Event = Event?.Trim() ?? "",
            Left = Left?.Trim() ?? "",
            Right = Right?.Trim() ?? "",
        });
}

// Late phase: after the move (PrePhysics), after the character's ground check, after animation has
// raised this tick's events and after foot IK has placed the feet (sage.animation.foot_ik sorts first).
[System(Id, Phase.Late)]
internal sealed class FootstepSystem : ISystem
{
    public const string Id = "sage.animation.footsteps";
    public const string EventName = "footstep";
    public const string LeftEventName = "foot_left";
    public const string RightEventName = "foot_right";
    public const float DefaultLandSpeed = 2.5f;

    // A move longer than this in one tick is a teleport, not a walk: no step for it.
    private const float Teleport = 4f;
    // The ray starts this far above the feet, so a foot a little below the surface still finds it.
    private const float Above = 0.25f;
    // Leaving the ground faster than this (m/s up) is a jump; slower is walking off an edge.
    private const float JumpSpeed = 1f;

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
                var entity = entities.EntityAt(n);
                bool character = world.TryGet<CharacterController>(entity, out var body);
                bool grounded = !character || body.Grounded;
                if (!step.Started)
                {
                    step.Last = at;
                    step.Started = true;
                    step.WasGrounded = grounded;
                    continue;
                }

                var moved = at - step.Last;
                step.Last = at;
                bool teleported = moved.LengthSquared() > Teleport * Teleport;

                // Leaving the ground and reaching it again: a character's only (a tween has no air).
                if (character)
                {
                    // On a ladder, swimming or flying it neither walks, jumps nor lands: it is "on the
                    // ground" again for when it steps off.
                    if (body.Climbing || body.Swimming || body.Mode is MovementMode.Fly or MovementMode.Noclip)
                    {
                        step.WasGrounded = true;
                        step.Falling = 0f;
                        continue;
                    }
                    bool was = step.WasGrounded;
                    step.WasGrounded = grounded;
                    if (!grounded)
                    {
                        step.Falling = MathF.Max(was ? 0f : step.Falling, -body.Velocity.Y);
                        if (was && !teleported && body.Velocity.Y >= JumpSpeed) Take(world, entity, at, ref step, FootstepKind.Jump);
                        continue;
                    }
                    if (!was)
                    {
                        float falling = step.Falling;
                        step.Falling = 0f;
                        step.Travelled = 0f;   // the next stride counts from where it came down
                        if (!teleported && falling >= step.LandSpeed) Take(world, entity, at, ref step, FootstepKind.Land);
                        continue;
                    }
                }

                float distance = MathF.Sqrt(moved.X * moved.X + moved.Z * moved.Z);
                if (step.Stride <= 0f || teleported) continue;
                step.Travelled += distance;
                if (step.Travelled < step.Stride) continue;
                step.Travelled -= step.Stride * MathF.Floor(step.Travelled / step.Stride);
                Take(world, entity, at, ref step, FootstepKind.Step);
            }
        }

        foreach (ref readonly var e in _animation.Read())
        {
            if (!world.IsAlive(e.Entity) || !world.Has<Footsteps>(e.Entity) || !world.Has<Transform>(e.Entity)) continue;
            ref var step = ref world.Get<Footsteps>(e.Entity);
            int foot;   // 0 the feet, -1 the left, 1 the right
            if (Is(e.Name, step.Event ?? EventName)) foot = 0;
            else if (Is(e.Name, step.Left ?? LeftEventName)) foot = -1;
            else if (Is(e.Name, step.Right ?? RightEventName)) foot = 1;
            else continue;
            step.Travelled = 0f;
            var at = foot == 0 || !TryGetFoot(world, e.Entity, foot < 0, out var placed)
                ? world.Get<Transform>(e.Entity).LocalPosition
                : placed;
            Take(world, e.Entity, at, ref step, FootstepKind.Step);
        }
    }

    private static bool Is(string name, string wanted) =>
        wanted.Length > 0 && string.Equals(name, wanted, StringComparison.Ordinal);

    // Where foot IK's foot is now, in the world: the ankle the entity's pose puts it at.
    private static bool TryGetFoot(World world, Entity entity, bool left, out Vector3 at)
    {
        at = default;
#pragma warning disable SAGE0126   // foot IK is skeletal animation's API, still settling
        if (!world.TryGet<FootIk>(entity, out var ik) || !Animators.TryGetPose(world, entity, out var pose)
            || !ReferenceEquals(ik.ResolvedFor, pose.Skeleton)) return false;
        int joint = left ? ik.LeftFoot : ik.RightFoot;
        if (joint < 0 || joint >= pose.ModelSpace.Length) return false;
        at = Vector3.Transform(pose.ModelSpace[joint].Translation, FootIkSystem.WorldPose(entity).ToMatrix());
#pragma warning restore SAGE0126
        return true;
    }

    private void Take(World world, Entity entity, Vector3 feet, ref Footsteps step, FootstepKind kind)
    {
        var point = feet;
        RecordId surface = default;
        if (_physics != null)
        {
            var hit = _physics.Raycast(feet + new Vector3(0f, Above, 0f), -Vector3.UnitY, Above + MathF.Max(step.Reach, 0.05f), ignore: entity);
            if (hit.Hit) { point = hit.Position; surface = hit.Surface; }
        }
        step.LastSurface = surface;
        world.Events.Send(new Footstep(entity, point, surface) { Kind = kind });

        var cue = CueOf(surface, kind);
        if (!cue.IsEmpty) world.Events.Send(new CueTriggered(cue, entity, point));
    }

    // The surface's cue for this kind of step, else its footstep; failing both, the conventions'
    // `surface`'s (the engine's sage:default).
    internal RecordId CueOf(RecordId surface, FootstepKind kind)
    {
        if (!_materials) return default;
        var cue = surface.IsEmpty ? default : Of(surface, kind);
        if (!cue.IsEmpty) return cue;
        var fallback = GameplayConventions.Of(_records).Surface.Id;
        return fallback.IsEmpty || fallback == surface ? default : Of(fallback, kind);
    }

    private RecordId Of(RecordId surface, FootstepKind kind)
    {
        if (!_records.TryGet(surface, out PhysicsMaterialRecord material)) return default;
        var cue = kind switch
        {
            FootstepKind.Land => material.Land,
            FootstepKind.Jump => material.Jump,
            _ => default,
        };
        return cue.IsEmpty ? material.Footstep : cue;
    }
}
