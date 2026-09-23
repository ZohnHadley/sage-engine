#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// AI, HL1-style (docs/design/16 §3.4, TODO F22): an agent has *conditions* (what it knows), picks a
// *schedule* (an ordered list of tasks, from a record) and runs one *task* at a time. Tasks write
// PawnIntent, so a creature walks with exactly the same character controller as the player.
//
// Thinking (perception, choosing a schedule) happens a few times a second and is staggered across
// agents; the current task runs every tick, so movement stays smooth.

[Flags]
public enum AICondition : ulong
{
    None = 0,
    SeeEnemy = 1 << 0,
    LostEnemy = 1 << 1,
    EnemyInMeleeRange = 1 << 2,
    NoEnemy = 1 << 3,
    TaskFailed = 1 << 4,
    ScheduleDone = 1 << 5,
}

// How an agent senses and fights (16 §3.4). Tuning is data, like movement profiles.
[Record("ai_profile")]
public sealed class AIProfileRecord
{
    public float SightRange = 22f;
    public float SightAngleDegrees = 200f;   // generous: creatures notice you from the side
    public float MeleeRange = 1.8f;      // how close it wants to be before swinging
    public float ThinkRate = 6f;         // times per second
    public float TurnSpeedDegrees = 360f;

    public static readonly RecordId Default = new("sage", "default_ai");
}

// An ordered task list plus the conditions that interrupt it (16 §3.4). Tasks are named, optionally
// with a number: "MoveToTarget:1.6" means "until 1.6 m away".
[Record("ai_schedule")]
public sealed class AIScheduleRecord
{
    public List<string> Tasks = new();
    public List<string> Interrupts = new();   // AICondition names

    private ulong _mask = ulong.MaxValue;
    private (string Name, float Param)[]? _parsed;

    // "MoveToTarget:1.6" -> ("MoveToTarget", 1.6), parsed once: schedules are read every tick.
    public (string Name, float Param)[] Steps()
    {
        if (_parsed != null) return _parsed;
        var steps = new (string, float)[Tasks.Count];
        for (int i = 0; i < Tasks.Count; i++)
        {
            string entry = Tasks[i];
            int colon = entry.IndexOf(':');
            steps[i] = colon < 0
                ? (entry, 0f)
                : (entry[..colon], float.TryParse(entry.AsSpan(colon + 1), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 0f);
        }
        return _parsed = steps;
    }

    public ulong InterruptMask()
    {
        if (_mask != ulong.MaxValue) return _mask;
        ulong mask = 0;
        foreach (var name in Interrupts)
            if (Enum.TryParse(name, true, out AICondition condition)) mask |= (ulong)condition;
            else Log.Warn(LogCat.AI, $"ai_schedule: unknown interrupt condition '{name}'");
        return _mask = mask;
    }
}

// The agent's running state (16 §4). Saved with the entity (09); Target is rebuilt on load.
public struct AIState : IComponent
{
    public RecordId Profile;
    public RecordId Schedule;
    public int TaskIndex;
    public ulong Conditions;
    public Entity Target;
    public float NextThink;
    public float TaskTime;      // seconds the current task has been running
    public bool TaskStarted;
}

public enum AITaskStatus { Running, Succeeded, Failed }

// What a task may touch. A ref struct, so tasks work on the components in place.
public ref struct AITaskContext
{
    public World World;
    public Entity Entity;
    public ref AIState State;
    public ref PawnIntent Intent;
    public ref Transform Transform;
    public AIProfileRecord Profile;
    public PhysicsSpace Space;
    public ActionId Attack;     // the Attack action, so a task can swing the way a player does
    public float Dt;
    public float Param;
}

public interface IAITask
{
    AITaskStatus Start(ref AITaskContext context) => AITaskStatus.Running;
    AITaskStatus Run(ref AITaskContext context);
}

// Tasks are registered by name, so schedules are data (16 §3.4). Games add their own.
public sealed class AITaskRegistry
{
    private readonly Dictionary<string, IAITask> _tasks = new(StringComparer.OrdinalIgnoreCase);

    public AITaskRegistry()
    {
        Register("Wait", new WaitTask());
        Register("FaceTarget", new FaceTargetTask());
        Register("MoveToTarget", new MoveToTargetTask());
        Register("MeleeAttack", new MeleeAttackTask());
    }

    public void Register(string name, IAITask task) => _tasks[name] = task;

    public IAITask? Find(string name) => _tasks.TryGetValue(name, out var task) ? task : null;
}

// ---- The built-in tasks --------------------------------------------------------------------------

// Stand still for `param` seconds (default 1).
internal sealed class WaitTask : IAITask
{
    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        return c.State.TaskTime >= (c.Param > 0 ? c.Param : 1f) ? AITaskStatus.Succeeded : AITaskStatus.Running;
    }
}

// Turn to face the target; succeeds once it is roughly facing it.
internal sealed class FaceTargetTask : IAITask
{
    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        if (!c.World.IsAlive(c.State.Target)) return AITaskStatus.Failed;

        float wanted = AIMath.YawTo(c.Transform.LocalPosition, c.World.Get<Transform>(c.State.Target).LocalPosition);
        c.Intent.Yaw = AIMath.TurnToward(c.Intent.Yaw, wanted, c.Profile.TurnSpeedDegrees * MathF.PI / 180f * c.Dt);
        return MathF.Abs(AIMath.WrapPi(wanted - c.Intent.Yaw)) < 0.15f ? AITaskStatus.Succeeded : AITaskStatus.Running;
    }
}

// Walk toward the target until it is `param` metres away (default: melee range).
internal sealed class MoveToTargetTask : IAITask
{
    public AITaskStatus Run(ref AITaskContext c)
    {
        if (!c.World.IsAlive(c.State.Target)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }

        Vector3 self = c.Transform.LocalPosition;
        Vector3 target = c.World.Get<Transform>(c.State.Target).LocalPosition;
        float stop = c.Param > 0 ? c.Param : c.Profile.MeleeRange;
        if (AIMath.DistanceXZ(self, target) <= stop)
        {
            c.Intent.Move = Vector2.Zero;
            return AITaskStatus.Succeeded;
        }

        float wanted = AIMath.YawTo(self, target);
        wanted += Avoid(ref c, self, wanted);
        c.Intent.Yaw = AIMath.TurnToward(c.Intent.Yaw, wanted, c.Profile.TurnSpeedDegrees * MathF.PI / 180f * c.Dt);
        c.Intent.Move = new Vector2(0, 1);   // forward, in the direction it is facing
        return AITaskStatus.Running;
    }

    // Steering around what is in the way, with raycasts (16 §3.2: navmesh pathfinding is F23). If
    // something blocks the way ahead, it turns toward whichever side has more room.
    private static float Avoid(ref AITaskContext c, Vector3 self, float wantedYaw)
    {
        const float Probe = 2.2f, Side = 0.7f;   // metres, radians
        // Just above step height: anything lower it simply walks over, anything higher is in the way.
        Vector3 eye = self + Vector3.UnitY * 0.5f;
        var mask = LayerMask.All.Except(c.Space.Layers.Enemy);   // not other creatures: chasing handles them

        float ahead = Clearance(ref c, eye, wantedYaw, Probe, mask);
        if (ahead >= Probe) return 0f;

        float left = Clearance(ref c, eye, wantedYaw + Side, Probe, mask);
        float right = Clearance(ref c, eye, wantedYaw - Side, Probe, mask);
        if (left <= ahead && right <= ahead) return 0f;   // boxed in: keep pushing forward
        return left >= right ? Side : -Side;
    }

    private static float Clearance(ref AITaskContext c, Vector3 from, float yaw, float distance, LayerMask mask)
    {
        var hit = c.Space.Raycast(from, SageMath.ForwardFromYaw(yaw), distance, mask);
        return hit.Hit ? hit.Distance : distance;
    }
}

// Swing at the target: the agent presses the same Attack action a player does, and
// MeleeCombatSystem does the rest (16 §3.2). Reach, timing, hit detection and damage belong to the
// attack record, so a creature and a player with the same weapon fight identically — and an AI can
// miss. `param` is how long to keep trying before giving up (default 1.5 s), which is what makes it
// wait out its own cooldown instead of failing the moment it is not ready.
internal sealed class MeleeAttackTask : IAITask
{
    public AITaskStatus Start(ref AITaskContext c)
    {
        if (c.World.Has<Melee>(c.Entity)) c.World.Get<Melee>(c.Entity).Swung = false;
        return AITaskStatus.Running;
    }

    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        if (!c.World.IsAlive(c.State.Target)) return AITaskStatus.Failed;
        if (!c.World.Has<Melee>(c.Entity))
        {
            Log.Once(LogCat.AI, LogLevel.Warn, $"ai-melee:{World.Describe(c.Entity)}",
                $"{World.Describe(c.Entity)} runs a MeleeAttack task but has no Melee component, so it cannot swing (16 §3.2)");
            return AITaskStatus.Failed;
        }

        Vector3 self = c.Transform.LocalPosition;
        Vector3 target = c.World.Get<Transform>(c.State.Target).LocalPosition;
        if (SageMath.DistanceXZ(self, target) > c.Profile.MeleeRange * 1.2f) return AITaskStatus.Failed;

        ref var melee = ref c.World.Get<Melee>(c.Entity);
        if (melee.Phase != MeleePhase.Ready) return AITaskStatus.Running;   // mid-swing: let it finish
        if (melee.Swung) return AITaskStatus.Succeeded;                     // it landed, or it missed

        c.Intent.Pressed = c.Intent.Pressed.With(c.Attack);
        return c.State.TaskTime > (c.Param > 0 ? c.Param : 1.5f) ? AITaskStatus.Failed : AITaskStatus.Running;
    }
}

// Was the AI's private copy of the angle helpers, which is how the conventions drifted (review #43).
// Everything here now lives in SageMath, which games can use too: IAITask is public, so a game's own
// task needs the same maths the engine's tasks use.
public static class AIMath
{
    public static float YawTo(Vector3 self, Vector3 target) => SageMath.YawTo(self, target);

    public static float DistanceXZ(Vector3 a, Vector3 b) => SageMath.DistanceXZ(a, b);

    public static float WrapPi(float angle) => SageMath.WrapPi(angle);

    public static float TurnToward(float from, float to, float maxStep) => SageMath.TurnToward(from, to, maxStep);
}
