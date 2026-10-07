#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Hearing (docs/design/16 §3.4, issue #386): a noise is something that happened somewhere, how far it
// carries and how loud it is. Anything raises one — `Noises.Make`, or `world.Events.Send(new Noise(...))`
// — and anything may listen; the engine's own noises are made by `NoiseSystem` from what already happens:
//   - a shot or a swing leaving the weapon (WeaponFired): a ranged attack is a gunshot, a sweep a swing,
//     each as loud as the conventions' `noise` says, or the attack's own `noiseRadius`;
//   - a blow landing (Damaged), at the victim, made by the attacker;
//   - a step, a jump or a landing (Footstep): a step's radius scales with how fast the walker is going
//     against its movement profile's walking speed, so a run carries further and a crouch less far.
//
// A creature hears a noise inside its radius times the profile's `hearing` (halved again, by default,
// through a wall: `occludedHearing`). What it heard becomes its point of interest (AIState.Heard) and two
// conditions: `HearNoise` on the think after it heard something new, and `Suspicious` while it has a
// point it has not finished looking at. The engine's choice runs the conventions' `investigate` schedule
// then (FaceNoise, MoveToNoise, a look round, ForgetNoise), below a fight and above a routine.
//
// Footsteps of somebody it is not hostile to are not worth turning round for; anything louder is.

// What made a noise, for a listener that cares (a squad's shout is `Alert`; a game's own are `Other`).
public enum NoiseKind : byte
{
    Other,
    Footstep,
    Weapon,     // a shot or a swing leaving the weapon
    Impact,     // a blow landing
    Alert,      // a voice raised on purpose: a warning shout, an alarm
}

// Something made a noise at `Point`: heard out to `Radius` metres in the open (by a listener with hearing
// 1), as loud as `Loudness` at its source (1 is a gunshot). `Source` is who made it, if anybody.
[GameEvent]
public readonly record struct Noise(Entity Source, Vector3 Point, float Radius, float Loudness)
{
    public NoiseKind Kind { get; init; }

    // How loud it is `distance` metres away for a listener whose reach is `scale` times the radius: the
    // loudness falling off to nothing at the edge. Zero (or less) is not heard.
    public float LoudnessAt(float distance, float scale = 1f)
    {
        float reach = Radius * scale;
        return reach <= 0f ? 0f : Loudness * (1f - distance / reach);
    }
}

public static class Noises
{
    // Makes a noise for whatever listens (AI hearing, a game's own rules). A noise nobody could hear (no
    // radius, no loudness) is not sent.
    public static void Make(World world, Entity source, Vector3 point, float radius, float loudness = 1f, NoiseKind kind = NoiseKind.Other)
    {
        if (!(radius > 0f) || !(loudness > 0f) || !float.IsFinite(radius) || !float.IsFinite(loudness)) return;
        world.Events.Send(new Noise(source, point, radius, loudness) { Kind = kind });
    }
}

// How loud the engine's own noises are (issue #386): the conventions' `noise`, which a game patches.
public sealed class NoiseConventions
{
    [Property(Min = 0, Unit = "m", Tooltip = "How far a ranged attack (a `ray` or `projectile` delivery) is heard; an attack's own `noiseRadius` wins")]
    public float GunshotRadius = 50f;
    [Property(Min = 0, Max = 1, Tooltip = "How loud a ranged attack is at the muzzle (1 = the loudest)")]
    public float GunshotLoudness = 1f;
    [Property(Min = 0, Unit = "m", Tooltip = "How far a swing (a `sweep` delivery) is heard; an attack's own `noiseRadius` wins")]
    public float SwingRadius = 5f;
    [Property(Min = 0, Max = 1, Tooltip = "How loud a swing is")]
    public float SwingLoudness = 0.3f;
    [Property(Min = 0, Unit = "m", Tooltip = "How far a blow landing is heard, from the one it landed on")]
    public float HitRadius = 12f;
    [Property(Min = 0, Max = 1, Tooltip = "How loud a blow landing is")]
    public float HitLoudness = 0.6f;
    [Property(Min = 0, Unit = "m", Tooltip = "How far a step at walking speed is heard; it scales with speed (a run further, a crouch less far)")]
    public float FootstepRadius = 6f;
    [Property(Min = 0, Max = 1, Tooltip = "How loud a step is")]
    public float FootstepLoudness = 0.25f;
    [Property(Min = 0, Tooltip = "The most a step's radius is scaled by speed (a sprint's)")]
    public float FootstepMaxScale = 2.5f;
    [Property(Min = 0, Unit = "m", Tooltip = "How far a landing from a fall or a jump is heard")]
    public float LandRadius = 8f;
    [Property(Min = 0, Max = 1, Tooltip = "How loud a landing is")]
    public float LandLoudness = 0.35f;
}

// The engine's noises, from the events that already say what happened (issue #386). Late, after the
// footsteps of this tick; a creature hears them on its next think.
[System("sage.ai.noise", Phase.Late, After = new[] { FootstepSystem.Id })]
internal sealed class NoiseSystem : ISystem
{
    private readonly RecordStore _records;
    private readonly EventReader<WeaponFired> _fired;
    private readonly EventReader<Damaged> _hits;
    private readonly EventReader<Footstep> _steps;

    public NoiseSystem(World world, RecordStore records)
    {
        _records = records;
        _fired = world.Events.Reader<WeaponFired>(this);
        _hits = world.Events.Reader<Damaged>(this);
        _steps = world.Events.Reader<Footstep>(this);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var noise = world.Conventions().Noise;

        foreach (ref readonly var shot in _fired.Read())
        {
            if (!world.IsAlive(shot.Shooter) || !world.TryGet<Transform>(shot.Shooter, out var at)) continue;
            AttackRecord? attack = !shot.Attack.IsEmpty && _records.TryGet(shot.Attack, out AttackRecord record) ? record : null;
#pragma warning disable SAGE0127   // hit deliveries are phase 4e's experimental API
            bool ranged = attack != null && !string.IsNullOrEmpty(attack.Delivery) &&
                          !string.Equals(attack.Delivery, HitDeliveries.Default, StringComparison.OrdinalIgnoreCase);
#pragma warning restore SAGE0127
            float radius = attack is { NoiseRadius: >= 0f } ? attack.NoiseRadius : ranged ? noise.GunshotRadius : noise.SwingRadius;
            float loudness = ranged ? noise.GunshotLoudness : noise.SwingLoudness;
            Noises.Make(world, shot.Shooter, at.LocalPosition + Vector3.UnitY * 1.4f, radius, loudness, NoiseKind.Weapon);
        }

        foreach (ref readonly var hit in _hits.Read())
        {
            var victim = hit.Hit.Target;
            if (!world.IsAlive(victim) || !world.TryGet<Transform>(victim, out var at)) continue;
            Noises.Make(world, hit.Hit.Attacker, at.LocalPosition + Vector3.UnitY, noise.HitRadius, noise.HitLoudness, NoiseKind.Impact);
        }

        foreach (ref readonly var step in _steps.Read())
        {
            if (step.Kind == FootstepKind.Land)
            {
                Noises.Make(world, step.Entity, step.Point, noise.LandRadius, noise.LandLoudness, NoiseKind.Footstep);
                continue;
            }
            Noises.Make(world, step.Entity, step.Point, noise.FootstepRadius * SpeedScale(world, step.Entity, noise.FootstepMaxScale),
                        noise.FootstepLoudness, NoiseKind.Footstep);
        }
    }

    // How fast it is going against its walking speed: 1 at a walk, more at a run, less creeping. Something
    // that is not a character (a tween, an animation's step) walks.
    private float SpeedScale(World world, Entity walker, float max)
    {
        if (!world.IsAlive(walker) || !world.TryGet<CharacterController>(walker, out var body)) return 1f;
        float walk = CharacterConventions.Of(world).ProfileOf(_records, body.Profile).WalkSpeed;
        if (!(walk > 0f)) return 1f;
        float speed = MathF.Sqrt(body.Velocity.X * body.Velocity.X + body.Velocity.Z * body.Velocity.Z);
        return Math.Clamp(speed / walk, 0f, MathF.Max(max, 0f));
    }
}

// What a creature does with a noise (issue #386), for AIThinkSystem: who heard it, and what each one now
// thinks is worth looking at.
internal sealed class Hearing
{
    // Hearing is clamped to this, so the broad-phase box round a noise holds everybody who could hear it.
    public const float MaxHearing = 2f;

    private readonly IPhysicsWorld _space;
    private readonly Entity[] _listeners = new Entity[256];   // broad-phase scratch, reused

    public Hearing(IPhysicsWorld space) => _space = space;

    // One noise, to everybody near enough to hear it.
    public void Hear(World world, RecordStore records, in Noise noise, float time, RecordId defaultProfile, AIProfileRecord fallback)
    {
        float reach = noise.Radius * MaxHearing;
        int found = _space.OverlapBox(noise.Point, new Vector3(reach), _listeners);
        var dead = world.Conventions().Dead;
        for (int i = 0; i < found; i++)
        {
            var listener = _listeners[i];
            if (listener == noise.Source || !world.IsAlive(listener) || !world.Has<AIState>(listener)) continue;
            if (!world.TryGet<Transform>(listener, out var at) || world.HasTag(listener, dead)) continue;
            // Somebody it has no quarrel with walking about is not news; a shot, a blow or a shout is.
            if (noise.Kind == NoiseKind.Footstep && !noise.Source.IsNull && !Factions.AreEnemies(world, listener, noise.Source)) continue;

            ref var state = ref world.Get<AIState>(listener);
            var profileId = state.Profile.IsEmpty ? defaultProfile : state.Profile;
            var profile = !profileId.IsEmpty && records.TryGet(profileId, out AIProfileRecord own) ? own : fallback;
            float scale = Math.Clamp(profile.Hearing, 0f, MaxHearing);
            if (scale <= 0f) continue;

            Vector3 ear = at.LocalPosition + Vector3.UnitY * 1.4f;
            float distance = Vector3.Distance(ear, noise.Point);
            if (distance >= noise.Radius * scale) continue;
            if (Occluded(noise.Point, ear, listener)) scale *= Math.Clamp(profile.OccludedHearing, 0f, 1f);
            float heard = noise.LoudnessAt(distance, scale);
            if (heard <= 0f) continue;

            // Its target, out of sight but making a noise: now it knows where it is.
            if (!noise.Source.IsNull && noise.Source == state.Target && world.IsAlive(state.Target))
            {
                state.LastSeen = noise.Point;
                state.ForgetAt = MathF.Max(state.ForgetAt, time + MathF.Max(profile.MemorySeconds, 0f));
            }

            // A louder noise wins over the one it is already looking into; a quieter one waits its turn.
            if (time < state.HeardUntil && heard < state.HeardLoudness) continue;
            state.Heard = noise.Point;
            state.HeardLoudness = heard;
            state.HeardSource = noise.Source;
            state.HeardUntil = time + MathF.Max(profile.InterestSeconds, 0f);
            state.HeardNew = true;
        }
    }

    // A wall between them: a ray from the noise to the ear, through bodies (creatures and players do not
    // muffle anything).
    private bool Occluded(Vector3 from, Vector3 ear, Entity listener)
    {
        var to = ear - from;
        float length = to.Length();
        if (length < 0.01f) return false;
        var hit = _space.Raycast(from, to / length, length, LayerMask.All.Except(_space.Layers.Enemy).Except(_space.Layers.Player));
        return hit.Hit && hit.Entity != listener;
    }

    // The conditions it knows from hearing, after perception: `HearNoise` once for something new, and
    // `Suspicious` while it has a point of interest. Seeing an enemy settles it: that was the noise.
    public static ulong Conditions(ref AIState state, float time)
    {
        ulong conditions = 0;
        if ((state.Conditions & (ulong)AICondition.SeeEnemy) != 0)
        {
            state.HeardNew = false;
            state.HeardUntil = 0f;
            return 0;
        }
        if (state.HeardNew) conditions |= (ulong)AICondition.HearNoise;
        state.HeardNew = false;
        if (time < state.HeardUntil) conditions |= (ulong)AICondition.Suspicious;
        return conditions;
    }

    public static void Forget(ref AIState state)
    {
        state.HeardUntil = 0f;
        state.HeardLoudness = 0f;
        state.HeardSource = default;
        state.HeardNew = false;
    }
}

// ---- the tasks an investigation is made of ----------------------------------------------------------

// Turn to face what it heard; succeeds once it roughly does, fails with nothing heard.
internal sealed class FaceNoiseTask : IAITask
{
    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        if (c.State.HeardUntil <= 0f) return AITaskStatus.Failed;
        float wanted = AIMath.YawTo(c.Transform.LocalPosition, c.State.Heard);
        c.Intent.Yaw = AIMath.TurnToward(c.Intent.Yaw, wanted, c.Profile.TurnSpeedDegrees * MathF.PI / 180f * c.Dt);
        return MathF.Abs(AIMath.WrapPi(wanted - c.Intent.Yaw)) < 0.15f ? AITaskStatus.Succeeded : AITaskStatus.Running;
    }
}

// Walk to what it heard, until it is `distance` metres away (default 1.5), round what is in the way as a
// chase does; fails with nothing heard.
internal sealed class MoveToNoiseTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;

    public AITaskStatus Run(ref AITaskContext c)
    {
        if (c.State.HeardUntil <= 0f) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }
        Vector3 self = c.Transform.LocalPosition;
        if (AIMath.DistanceXZ(self, c.State.Heard) <= (c.Param > 0 ? c.Param : 1.5f))
        {
            c.Intent.Move = Vector2.Zero;
            return AITaskStatus.Succeeded;
        }
        MoveToTargetTask.WalkToward(ref c, self, c.State.Heard, default);
        return AITaskStatus.Running;
    }
}

// Done looking: the point of interest is dropped, and `Suspicious` with it.
internal sealed class ForgetNoiseTask : IAITask
{
    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        Hearing.Forget(ref c.State);
        return AITaskStatus.Succeeded;
    }
}
