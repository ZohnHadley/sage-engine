#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// What an effect does besides changing numbers (docs/REDESIGN.md §4.3, issue #28). Effects could only
// modify attributes and grant tags, so a knockback, a blink or a summoning had nowhere to live but a
// system of its own. An effect now lists `executions`, entries of an open vocabulary:
//
//   { "type": "effect", "id": "shove", "executions": [{ "execution": "knockback", "force": 8, "lift": 2 }] }
//
// They run whenever the effect is applied — an instant one once, a timed one when it starts, is
// refreshed or stacks — and on every period of a periodic one, scaled by the application's magnitude.
// The engine's are `knockback`, `teleport`, `summon` and `dispel`; a game declares its own:
//
//   [EffectExecution("ignite", Plugin = "mygame")] public sealed class Ignite : IEffectExecution { … }
//
// An execution may run inside a system's loop (a periodic tick, a swing's rider), where adding or
// destroying entities throws and another entity's effect list may be mid-walk: anything structural
// is queued (`Defer`) and done by EffectExecutionSystem after the tick, in the same phase.
[Vocabulary("effect_execution", Key = "execution")]
public interface IEffectExecution
{
    void Execute(in EffectExecution execution);
}

public sealed class EffectExecutionAttribute : VocabularyEntryAttribute<IEffectExecution>
{
    public EffectExecutionAttribute(string id) : base(id) { }
}

// One running of an execution: on whom, from whom, which effect, how strongly.
public readonly ref struct EffectExecution
{
    public World World { get; init; }
    public Entity Target { get; init; }
    public Entity Source { get; init; }
    public RecordId Effect { get; init; }
    public EffectRecord Record { get; init; }
    public int Stacks { get; init; }
    public float Magnitude { get; init; }

    // Work that changes the world's structure, done after the effect tick (EffectExecutionSystem).
    public void Defer(Action<World> work) => EffectExecutions.QueueOf(World).Add(work);
}

public static class EffectExecutions
{
    internal static void Run(World world, Entity target, Entity source, RecordId effect, EffectRecord record, int stacks, float magnitude)
    {
        var executions = record.Executions;
        if (executions.Count == 0) return;
        var execution = new EffectExecution
        {
            World = world, Target = target, Source = source, Effect = effect, Record = record,
            Stacks = stacks, Magnitude = magnitude == 0f ? 1f : magnitude,
        };
        foreach (var e in executions) e.Execute(in execution);
    }

    internal static List<Action<World>> QueueOf(World world)
    {
        if (world.Resources.TryGet<EffectExecutionQueue>(out var queue) && queue != null) return queue.Pending;
        queue = new EffectExecutionQueue();
        world.Resources.Add(queue);
        return queue.Pending;
    }

    // Away from `from`, across the ground; the target's back when there is nowhere to be pushed from.
    internal static Vector3 AwayFrom(World world, Entity target, Entity from)
    {
        var at = world.Get<Transform>(target).LocalPosition;
        if (!from.IsNull && world.TryGet<Transform>(from, out var source))
        {
            var away = at - source.LocalPosition;
            away.Y = 0;
            if (away.LengthSquared() > 1e-6f) return Vector3.Normalize(away);
        }
        return -SageMath.ForwardFromYaw(YawOf(world, target));
    }

    internal static float YawOf(World world, Entity entity) =>
        world.TryGet<PawnIntent>(entity, out var intent) ? intent.Yaw : SageMath.YawOf(world.Get<Transform>(entity).LocalRotation);
}

// The work executions put off until after the effect tick.
public sealed class EffectExecutionQueue
{
    public readonly List<Action<World>> Pending = new();
}

// Gameplay phase, after effects tick: does what executions deferred.
[System("sage.effects.executions", Phase.Gameplay, After = new[] { "sage.effects.tick" })]
public sealed class EffectExecutionSystem : ISystem
{
    private readonly List<Action<World>> _doing = new();

    public EffectExecutionSystem(World world) { }

    public void Run(in SystemContext ctx)
    {
        if (!ctx.World.Resources.TryGet<EffectExecutionQueue>(out var queue) || queue == null || queue.Pending.Count == 0) return;
        _doing.Clear();
        _doing.AddRange(queue.Pending);   // work may queue more; that waits for the next tick
        queue.Pending.Clear();
        foreach (var work in _doing) work(ctx.World);
        _doing.Clear();
    }
}

// ---- the engine's executions ----------------------------------------------------------------------

// A shove: the target's character is pushed away from the source (or backwards), `force` metres per
// second across the ground and `lift` up, both scaled by the magnitude. Only a character is pushed —
// a crate has no velocity of its own to change yet.
[EffectExecution("knockback", Plugin = "sage.gameplay.attributes")]
public sealed class KnockbackExecution : IEffectExecution
{
    [Property(Unit = "m/s", Tooltip = "How hard it pushes across the ground")]
    public float Force = 6f;
    [Property(Unit = "m/s", Tooltip = "How hard it pushes up")]
    public float Lift = 2f;

    public void Execute(in EffectExecution e)
    {
        if (!e.World.IsAlive(e.Target) || !e.World.Has<CharacterController>(e.Target) || !e.World.Has<Transform>(e.Target)) return;
        var push = EffectExecutions.AwayFrom(e.World, e.Target, e.Source) * Force + Vector3.UnitY * Lift;
        ref var character = ref e.World.Get<CharacterController>(e.Target);
        character.Velocity += push * e.Magnitude;
        if (Lift > 0f) character.Grounded = false;
    }
}

// A blink: the target moves by `offset` — metres right, up and forward of the way it faces — or, with
// `toSource`, to that offset from the source (a pull). Not interpolated: it is there.
[EffectExecution("teleport", Plugin = "sage.gameplay.attributes")]
public sealed class TeleportExecution : IEffectExecution
{
    [Property(Unit = "m", Tooltip = "Where to, as right, up and forward of the one it is measured from")]
    public Vector3 Offset = new(0, 0, 5);
    [Property(Tooltip = "Measure from the source (a pull) rather than the target (a blink)")]
    public bool ToSource;

    public void Execute(in EffectExecution e)
    {
        var from = ToSource && !e.Source.IsNull && e.World.IsAlive(e.Source) ? e.Source : e.Target;
        if (!e.World.IsAlive(e.Target) || !e.World.TryGet<Transform>(from, out var anchor) || !e.World.Has<Transform>(e.Target)) return;

        float yaw = EffectExecutions.YawOf(e.World, from);
        var forward = SageMath.ForwardFromYaw(yaw);
        var right = new Vector3(-forward.Z, 0, forward.X);
        var to = anchor.LocalPosition + right * Offset.X + Vector3.UnitY * Offset.Y + forward * Offset.Z;

        var moved = e.World.Get<Transform>(e.Target);
        moved.LocalPosition = to;
        e.World.Teleport(e.Target, moved);
        if (e.World.Has<CharacterController>(e.Target)) e.World.Get<CharacterController>(e.Target).Velocity = Vector3.Zero;
    }
}

// Something appears: `count` of `prefab`, `distance` in front of the target, fanned out.
[EffectExecution("summon", Plugin = "sage.gameplay.attributes")]
public sealed class SummonExecution : IEffectExecution
{
    [Property(Tooltip = "What appears")]
    public RecordRef<PrefabRecord> Prefab;
    [Property(Min = 1, Tooltip = "How many")]
    public int Count = 1;
    [Property(Min = 0, Unit = "m", Tooltip = "How far in front of the target")]
    public float Distance = 1.5f;

    public void Execute(in EffectExecution e)
    {
        if (Prefab.IsEmpty || !e.World.IsAlive(e.Target) || !e.World.TryGet<Transform>(e.Target, out var at)) return;
        float yaw = EffectExecutions.YawOf(e.World, e.Target);
        var origin = at.LocalPosition;
        var prefab = Prefab.Id;
        int count = Math.Max(Count, 1);
        float distance = Distance;
        e.Defer(world =>
        {
            for (int i = 0; i < count; i++)
            {
                float angle = yaw + (i - (count - 1) * 0.5f) * 0.6f;
                world.Spawn(prefab, origin + SageMath.ForwardFromYaw(angle) * distance, angle * 180f / MathF.PI);
            }
        });
    }
}

// Removes running effects from the target: the ones listed in `effects`, and any that grant one of
// `tags` (a cleanse: "everything that burns"). Lists nothing, removes nothing.
[EffectExecution("dispel", Plugin = "sage.gameplay.attributes")]
public sealed class DispelExecution : IEffectExecution
{
    [Property(Tooltip = "Effects to remove")]
    public List<RecordRef<EffectRecord>> Effects = new();
    [Property(Tooltip = "Remove every effect that grants one of these tags")]
    public List<RecordRef<TagRecord>> Tags = new();

    public void Execute(in EffectExecution e)
    {
        if (Effects.Count == 0 && Tags.Count == 0) return;
        var target = e.Target;
        var effects = Effects;
        var tags = Tags;
        e.Defer(world =>
        {
            if (!world.IsAlive(target) || !world.Has<ActiveEffects>(target)) return;
            var records = world.Records();
            world.Get<ActiveEffects>(target).Effects?.RemoveAll(running =>
            {
                foreach (var effect in effects) if (running.Record == effect.Id) return true;
                if (tags.Count == 0 || !records.TryGet(running.Record, out EffectRecord record)) return false;
                foreach (var granted in record.GrantTags)
                    foreach (var tag in tags)
                        if (granted.Id == tag.Id) return true;
                return false;
            });
        });
    }
}
