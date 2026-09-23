#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// AI phase (docs/design/16 §3.4): perceive, choose a schedule, run the current task.
//
// Perception and schedule choice run at the profile's think rate (a few times a second), staggered by
// entity id so a crowd doesn't think on the same tick. The current task runs every tick, because it
// writes PawnIntent and the character controller consumes that every tick.
public sealed class AIThinkSystem : ISystem
{
    private readonly ArchetypeQuery<Transform, AIState, PawnIntent> _agents;
    private readonly ArchetypeQuery<Transform> _players;
    private readonly RecordStore _records;
    private readonly AITaskRegistry _tasks;
    private readonly PhysicsSpace _space;
    private readonly AIEvents _events;
    private static readonly AIProfileRecord FallbackProfile = new();

    public AIThinkSystem(World world, RecordStore records, AITaskRegistry tasks)
    {
        _agents = world.Query<Transform, AIState, PawnIntent>();
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
        _records = records;
        _tasks = tasks;
        _space = world.Resources.Get<PhysicsSpace>();
        _events = world.Resources.Get<AIEvents>();
    }

    public void Run(in SystemContext ctx)
    {
        _events.Clear();
        float dt = ctx.Tick.Dt;
        float time = (float)ctx.Tick.SimTime;
        var world = ctx.World;

        foreach (var (transforms, states, intents, entities) in _agents.Chunks)
        {
            var t = transforms.Span;
            var s = states.Span;
            var i = intents.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                var profile = _records.TryGet(s[n].Profile.IsEmpty ? AIProfileRecord.Default : s[n].Profile, out AIProfileRecord found)
                    ? found : FallbackProfile;

                if (s[n].Cooldown > 0) s[n].Cooldown -= dt;

                // Think: perception and, if something changed, a new schedule.
                if (time >= s[n].NextThink)
                {
                    float period = 1f / MathF.Max(profile.ThinkRate, 0.1f);
                    s[n].NextThink = time + period * (0.85f + 0.3f * ((entity.Id % 7) / 7f));   // staggered
                    Perceive(world, entity, ref t[n], ref s[n], profile);
                    ChooseSchedule(ref s[n]);
                }

                RunTask(world, entity, ref t[n], ref s[n], ref i[n], profile, dt);
            }
        }
    }

    // What the agent knows: is there an enemy, can it see one, is it in reach (16 §3.4).
    private void Perceive(World world, Entity self, ref Transform transform, ref AIState state, AIProfileRecord profile)
    {
        ulong conditions = 0;
        float yaw = SageMath.YawOf(transform.LocalRotation);
        Entity target = FindNearestPlayer(world, transform.LocalPosition, yaw, profile, self, out float distance, out bool visible);

        if (target.IsNull)
        {
            state.Target = default;
            state.Conditions = (ulong)AICondition.NoEnemy;
            return;
        }

        if (visible)
        {
            state.Target = target;
            conditions |= (ulong)AICondition.SeeEnemy;
            if (distance <= profile.MeleeRange) conditions |= (ulong)AICondition.EnemyInMeleeRange;
        }
        else if (!state.Target.IsNull)
        {
            conditions |= (ulong)AICondition.LostEnemy;
        }
        else
        {
            conditions |= (ulong)AICondition.NoEnemy;
        }
        state.Conditions = conditions;
    }

    // The nearest player inside the sight cone with a clear line of sight (a raycast from eye height,
    // so walls and terrain block it). The cone is the agent's own facing (`yaw`) widened by the
    // profile's SightAngleDegrees: before this was checked, creatures noticed you through the back of
    // their heads, which the field was authored to prevent (review #50).
    private Entity FindNearestPlayer(World world, Vector3 position, float yaw, AIProfileRecord profile, Entity self, out float distance, out bool visible)
    {
        distance = float.MaxValue;
        visible = false;
        Entity best = default;

        foreach (var (transforms, entities) in _players.Chunks)
        {
            var t = transforms.Span;
            for (int n = 0; n < t.Length; n++)
            {
                float d = SageMath.DistanceXZ(position, t[n].LocalPosition);
                if (d > profile.SightRange || d >= distance) continue;
                if (d > 0.01f && !SageMath.InCone(yaw, position, t[n].LocalPosition, profile.SightAngleDegrees)) continue;
                best = entities.EntityAt(n);
                distance = d;
            }
        }
        if (best.IsNull) return default;

        Vector3 eye = position + Vector3.UnitY * 1.4f;
        Vector3 targetEye = world.Get<Transform>(best).LocalPosition + Vector3.UnitY * 1.2f;
        Vector3 toTarget = targetEye - eye;
        float length = toTarget.Length();
        if (length < 0.001f) { visible = true; return best; }

        var hit = _space.Raycast(eye, toTarget / length, length, LayerMask.All.Except(_space.Layers.Enemy));
        visible = !hit.Hit || hit.Entity == best;
        return best;
    }

    // Which schedule fits what it knows. Code, like HL1's GetSchedule; utility scoring or a behaviour
    // tree can replace this without touching the tasks (16 §3.4).
    private static void ChooseSchedule(ref AIState state)
    {
        var conditions = (AICondition)state.Conditions;
        RecordId wanted =
            conditions.HasFlag(AICondition.EnemyInMeleeRange) ? Schedules.Attack :
            conditions.HasFlag(AICondition.SeeEnemy) ? Schedules.Chase :
            Schedules.Idle;

        if (state.Schedule == wanted) return;
        state.Schedule = wanted;
        state.TaskIndex = 0;
        state.TaskTime = 0;
        state.TaskStarted = false;
    }

    public static class Schedules
    {
        public static readonly RecordId Idle = new("sage", "idle");
        public static readonly RecordId Chase = new("sage", "chase");
        public static readonly RecordId Attack = new("sage", "melee_attack");
    }

    // Runs the current task, advancing through the schedule as tasks succeed. An interrupt condition
    // or a failed task ends the schedule early, and the next think picks another one.
    private void RunTask(World world, Entity entity, ref Transform transform, ref AIState state, ref PawnIntent intent, AIProfileRecord profile, float dt)
    {
        if (state.Schedule.IsEmpty || !_records.TryGet(state.Schedule, out AIScheduleRecord schedule))
        {
            intent.Move = Vector2.Zero;
            return;
        }

        var steps = schedule.Steps();
        if ((state.Conditions & schedule.InterruptMask()) != 0 && state.TaskIndex > 0)
        {
            state.TaskIndex = steps.Length;   // interrupted: let the next think choose again
        }

        if (state.TaskIndex >= steps.Length)
        {
            intent.Move = Vector2.Zero;
            state.Conditions |= (ulong)AICondition.ScheduleDone;
            state.TaskIndex = 0;
            state.TaskStarted = false;
            state.TaskTime = 0;
            return;
        }

        var (name, param) = steps[state.TaskIndex];
        var task = _tasks.Find(name);
        if (task == null)
        {
            Log.Once(LogCat.AI, LogLevel.Error, $"ai-task:{name}", $"{state.Schedule}: no AI task named '{name}' is registered; the schedule stops here");
            state.TaskIndex = steps.Length;
            return;
        }

        var context = new AITaskContext
        {
            World = world,
            Entity = entity,
            State = ref state,
            Intent = ref intent,
            Transform = ref transform,
            Profile = profile,
            Space = _space,
            Dt = dt,
            Param = param,
        };

        AITaskStatus status;
        if (!state.TaskStarted)
        {
            state.TaskStarted = true;
            state.TaskTime = 0;
            status = task.Start(ref context);
            if (status == AITaskStatus.Running) status = task.Run(ref context);
        }
        else
        {
            state.TaskTime += dt;
            status = task.Run(ref context);
        }

        switch (status)
        {
            case AITaskStatus.Succeeded:
                state.TaskIndex++;
                state.TaskStarted = false;
                state.TaskTime = 0;
                break;
            case AITaskStatus.Failed:
                state.Conditions |= (ulong)AICondition.TaskFailed;
                state.TaskIndex = steps.Length;
                state.TaskStarted = false;
                break;
        }
    }

}
