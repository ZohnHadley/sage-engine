#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// AI phase (docs/design/16 §3.4): perceive, choose a schedule, run the current task.
//
// Perception and schedule choice run at the profile's think rate (a few times a second), staggered by
// entity id so a crowd doesn't think on the same tick. The current task runs every tick, because it
// writes PawnIntent and the character controller consumes that every tick.
[System("sage.ai.think", Phase.Commands, After = new[] { "sage.character.player_control" })]
public sealed class AIThinkSystem : ISystem
{
    private readonly Query<Transform, AIState, PawnIntent> _agents;
    private readonly Query<Transform> _players;
    private readonly Entity[] _candidates;   // broad-phase scratch, reused every think (F24)
    private readonly EventReader<Damaged> _damage;
    private readonly RecordStore _records;
    private readonly AITaskRegistry _tasks;
    private readonly IPhysicsWorld _space;
    private readonly ActionId _attack;
    private readonly AIConditions _conditions;   // condition names to bits, and the game's to sense (issue #28)
    private static readonly AIProfileRecord FallbackProfile = new();

    public AIThinkSystem(World world, RecordStore records, AITaskRegistry tasks, ActionRegistry actions)
    {
        _agents = world.Query<Transform, AIState, PawnIntent>();
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
        _candidates = new Entity[64];
        _damage = world.Events.Reader<Damaged>(this, Schedule.Fixed);
        _records = records;
        _tasks = tasks;
        _space = world.Resources.Get<IPhysicsWorld>();
        _attack = actions.Get(world.Conventions().Actions.Attack);
        _conditions = AIConditions.Of(world);

        // A creature's path and the place it last saw somebody are positions in origin space, and the
        // origin moves (R6). Nothing else shifts them, so this does — the rule is that *everything*
        // holding a world position subscribes, and F23 added three of them.
        world.Origin().Rebased += offset => RebaseThinking(world, offset);
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;
        float time = (float)ctx.Tick.SimTime;
        var world = ctx.World;

        Provoked(world, time);
        var conventions = world.Conventions();

        foreach (var (transforms, states, intents, entities) in _agents.Chunks)
        {
            var t = transforms.Span;
            var s = states.Span;
            var i = intents.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                var profileId = s[n].Profile.IsEmpty ? conventions.AiProfile.Id : s[n].Profile;
                var profile = !profileId.IsEmpty && _records.TryGet(profileId, out AIProfileRecord found)
                    ? found : FallbackProfile;

                // Think: perception and, if something changed, a new schedule.
                if (time >= s[n].NextThink)
                {
                    float period = 1f / MathF.Max(profile.ThinkRate, 0.1f);
                    s[n].NextThink = time + period * (0.85f + 0.3f * ((entity.Id % 7) / 7f));   // staggered
                    Perceive(world, entity, ref t[n], ref s[n], profile, time);
                    Sense(world, entity, ref t[n], ref s[n], profile, time);
                    ChooseSchedule(world, entity, ref s[n], profile, conventions.Schedules);
                }

                RunTask(world, entity, ref t[n], ref s[n], ref i[n], profile, dt);
            }
        }
    }

    // What the agent knows: is there an enemy, can it see one, is it in reach (16 §3.4).
    private void Perceive(World world, Entity self, ref Transform transform, ref AIState state,
                          AIProfileRecord profile, float time)
    {
        ulong conditions = 0;
        float yaw = SageMath.YawOf(transform.LocalRotation);
        Entity target = FindNearestEnemy(world, self, transform.LocalPosition, yaw, profile, out float distance, out bool visible);

        if (world.Has<Melee>(self)) conditions |= (ulong)AICondition.CanMelee;

        // Nothing in the cone. That is not the same as nothing to chase: a target seen a moment ago is
        // remembered for `memorySeconds`, which is what lets a creature look where it is *going* while it
        // walks round something (F23). When the memory runs out it is `LostEnemy`, and only then.
        if (target.IsNull || (!visible && target != state.Target))
        {
            if (Remembers(world, ref state, time))
            {
                state.Conditions = conditions | (ulong)AICondition.RememberEnemy;
                return;
            }
            bool had = !state.Target.IsNull;
            state.Target = default;
            state.Spell = default;
            state.Conditions = conditions | (ulong)(had ? AICondition.LostEnemy : AICondition.NoEnemy);
            return;
        }

        if (visible)
        {
            state.Target = target;
            state.LastSeen = world.Get<Transform>(target).LocalPosition;
            state.ForgetAt = time + MathF.Max(profile.MemorySeconds, 0f);
            conditions |= (ulong)AICondition.SeeEnemy;
            if (distance <= profile.MeleeRange) conditions |= (ulong)AICondition.EnemyInMeleeRange;
            state.Spell = ChooseSpell(world, self, distance, out bool comingBack, out bool busy);
            if (busy) conditions |= (ulong)AICondition.Casting;
            else if (!state.Spell.IsEmpty) conditions |= (ulong)AICondition.CanCastAtEnemy;
            if (comingBack) conditions |= (ulong)AICondition.SpellComingBack;
        }
        else if (Remembers(world, ref state, time))
        {
            conditions |= (ulong)AICondition.RememberEnemy;
        }
        else if (!state.Target.IsNull)
        {
            state.Target = default;
            conditions |= (ulong)AICondition.LostEnemy;
        }
        else
        {
            conditions |= (ulong)AICondition.NoEnemy;
        }
        if ((conditions & (ulong)(AICondition.CanCastAtEnemy | AICondition.Casting)) == 0) state.Spell = default;
        state.Conditions = conditions;
    }

    // Which spell to throw, if any (16 §3.3). Every gate the cast system would apply is applied here
    // through `AbilityRules`, so a creature never winds up something that would be refused — and the
    // spell's own `Range` decides whether the enemy is close enough, because that is the number the
    // cast will actually be resolved against.
    //
    // It picks the **dearest** one it can cast: a creature uses its best spell first and falls back to
    // cheaper ones as its mana goes. Utility scoring belongs here when there is more to weigh than
    // cost (16 §3.4), and nothing about the tasks changes when it arrives.
    private RecordId ChooseSpell(World world, Entity self, float distance, out bool comingBack, out bool busy)
    {
        comingBack = false;
        busy = false;
        if (!world.TryGet<Abilities>(self, out var abilities) || abilities.Known is not { Count: > 0 }) return default;

        // Already winding one up: there is nothing to choose, and every gate would answer
        // `AlreadyCasting` anyway. Saying so keeps the agent in its cast schedule until the spell
        // goes off, instead of concluding mid-cast that it has no magic and wandering away.
        if (!abilities.Casting.IsEmpty) { busy = true; return abilities.Casting; }

        RecordId best = default;
        float dearest = -1f;
        foreach (var ability in abilities.Known)
        {
            if (!AbilityRules.CanCast(world, _records, self, ability, in abilities, out var record, out var why))
            {
                // Only a cooldown is worth waiting for, and only for a spell that would reach from
                // here. Anything else (no mana, a blocking tag) needs the agent to do something else.
                if (why == CastRefusal.OnCooldown && record != null && distance <= record.Range * 0.9f
                    && !AbilityDeliveries.OnCaster(world, record))
                    comingBack = true;
                continue;
            }
            // A self-targeted spell is a buff, not an attack: casting one *at* an enemy is a decision
            // of its own, and a creature that healed itself instead of fighting would be worse than
            // one that does not try (16 §3.4, left for the ranged/support behaviours).
            if (AbilityDeliveries.OnCaster(world, record)) continue;
            // A little short of the full range: at the very edge a projectile's flight time runs out
            // just as it arrives, and a touch sweep grazes past.
            if (distance > record.Range * 0.9f) continue;
            if (record.Cost <= dearest) continue;
            best = ability;
            dearest = record.Cost;
        }
        return best;
    }

    // The nearest player inside the sight cone with a clear line of sight (a raycast from eye height,
    // so walls and terrain block it). The cone is the agent's own facing (`yaw`) widened by the
    // profile's SightAngleDegrees: before this was checked, creatures noticed you through the back of
    // their heads, which the field was authored to prevent (review #50).
    // The nearest thing this creature considers an enemy (16 §3.5, F24).
    //
    // **Asked of the world, not of the player.** Before factions this looked only at player-controlled
    // entities, because "enemy" had no other meaning; now it is whoever `Factions` says is hostile, which
    // is what lets two creatures fight each other while the player watches. The candidates come from the
    // broad phase, so the cost follows what is *near* a creature rather than how many creatures the world
    // holds — a query over everything would turn a crowd into a quadratic (R18's scale test guards it).
    private Entity FindNearestEnemy(World world, Entity self, Vector3 position, float yaw,
                                    AIProfileRecord profile, out float distance, out bool visible)
    {
        distance = float.MaxValue;
        visible = false;
        Entity best = default;

        float reach = profile.SightRange;

        // The player first, from a query over the handful of player-controlled entities. The broad-phase
        // sweep below can only return as many as the buffer holds, and in a world of two thousand the
        // thing a creature must never fail to notice is the player standing in front of it.
        foreach (var (transforms, entities) in _players.Chunks)
        {
            var pt = transforms.Span;
            for (int n = 0; n < pt.Length; n++)
                Consider(world, self, position, yaw, profile, entities.EntityAt(n), pt[n].LocalPosition,
                         ref best, ref distance);
        }

        int found = _space.OverlapBox(position, new Vector3(reach), _candidates);
        for (int i = 0; i < found; i++)
        {
            var candidate = _candidates[i];
            if (!world.TryGet<Transform>(candidate, out var at)) continue;
            Consider(world, self, position, yaw, profile, candidate, at.LocalPosition, ref best, ref distance);
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

    // Moves every creature's cached positions with the world (R6). The query is the system's own, so
    // this costs one pass over the creatures on the tick a rebase happens and nothing on any other.
    private void RebaseThinking(World world, Vector3 offset)
    {
        foreach (var (_, states, _, _) in _agents.Chunks)
        {
            var s = states.Span;
            for (int n = 0; n < s.Length; n++)
            {
                s[n].LastSeen += offset;
                s[n].Path.Rebase(offset);
            }
        }
    }

    // Is there still a target worth walking to, even out of sight? Only for as long as the profile says,
    // and only while it exists: a remembered target that has been destroyed is no target at all.
    private static bool Remembers(World world, ref AIState state, float time) =>
        !state.Target.IsNull && world.IsAlive(state.Target) && time < state.ForgetAt;

    // Being hit is how you find out somebody is behind you (16 §3.4).
    //
    // Sight is a cone, so without this a creature stabbed in the back never turns round — which mattered
    // little while the only attacker was the player standing in front of it, and matters a great deal now
    // that creatures fight each other (F24). It is also what makes a neutral townsman defend himself: hit
    // him and he has a target, whatever his faction thinks of you.
    private void Provoked(World world, float time)
    {
        foreach (ref readonly var hit in _damage.Read())
        {
            var victim = hit.Hit.Target;
            var attacker = hit.Hit.Attacker;
            if (hit.Applied <= 0f || attacker.IsNull || attacker == victim) continue;
            if (!world.IsAlive(victim) || !world.IsAlive(attacker)) continue;
            if (!world.Has<AIState>(victim) || !world.TryGet<Transform>(attacker, out var at)) continue;

            ref var state = ref world.Get<AIState>(victim);
            // What it is already fighting wins: a creature should not be pulled off a target by every
            // scratch from somewhere else, and the memory it has is the thing keeping it on this one.
            if (!state.Target.IsNull && world.IsAlive(state.Target) && time < state.ForgetAt) continue;

            state.Target = attacker;
            state.LastSeen = at.LocalPosition;
            state.ForgetAt = time + 6f;
        }
    }

    // One candidate: something to fight, hostile, in range, in front. Shared by the player query and the
    // broad-phase sweep so both mean exactly the same thing by "enemy" (16 §3.5).
    private static void Consider(World world, Entity self, Vector3 position, float yaw, AIProfileRecord profile,
                                 Entity candidate, Vector3 at, ref Entity best, ref float distance)
    {
        if (candidate == self || candidate.IsNull || !world.IsAlive(candidate)) return;
        // Scenery and pickups are solid and near and none of a creature's business: a target is a body
        // with attributes that is still alive.
        if (!world.Has<Attributes>(candidate) || world.HasTag(candidate, world.Conventions().Dead)) return;
        if (!Factions.AreEnemies(world, self, candidate)) return;

        float d = SageMath.DistanceXZ(position, at);
        if (d > profile.SightRange || d >= distance) return;
        if (d > 0.01f && !SageMath.InCone(yaw, position, at, profile.SightAngleDegrees)) return;
        best = candidate;
        distance = d;
    }

    // The game's own conditions (issue #28), after the engine's perception: each is asked with what the
    // creature knows so far, in bit order, and sets its bit when it holds. Nothing to do in a game that
    // declares none, which is every game before #28.
    private void Sense(World world, Entity entity, ref Transform transform, ref AIState state, AIProfileRecord profile, float time)
    {
        var sensed = _conditions.Sensed;
        if (sensed.Length == 0) return;
        var perception = new AIPerception
        {
            World = world,
            Entity = entity,
            Transform = ref transform,
            State = ref state,
            Profile = profile,
            Time = time,
            Conditions = state.Conditions,
        };
        foreach (var sensor in sensed)
            if (sensor.Condition.Sense(in perception)) perception.Conditions |= sensor.Bit;
        state.Conditions = perception.Conditions;
    }

    // Which schedule fits what it knows: the profile's selector decides (issue #28), and the engine's
    // own choice — HL1's GetSchedule, in code — is the `default` one (DefaultScheduleSelector). Utility
    // scoring or a behaviour tree is another selector, and nothing about the tasks changes (16 §3.4).
    private void ChooseSchedule(World world, Entity entity, ref AIState state, AIProfileRecord profile, AIScheduleConventions schedules)
    {
        var choice = new AIScheduleChoice
        {
            World = world,
            Entity = entity,
            Conditions = state.Conditions,
            Current = state.Schedule,
            Profile = profile,
            Schedules = schedules,
            Names = _conditions,
        };
        RecordId wanted = AIScheduleSelectors.Of(world, profile).Choose(in choice);

        if (state.Schedule == wanted) return;
        state.Schedule = wanted;
        state.TaskIndex = 0;
        state.TaskTime = 0;
        state.TaskStarted = false;
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
        if ((state.Conditions & schedule.InterruptMask(_conditions)) != 0 && state.TaskIndex > 0)
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

        var step = steps[state.TaskIndex];
        string name = step.Task;
        float param = step.Value;
        var task = _tasks.Find(name);
        if (task == null)
        {
            Log.Once(LogCat.AI, LogLevel.Error, $"ai-task:{name}",
                $"{_records.Where("ai_schedule", state.Schedule, $"Tasks[{state.TaskIndex}]")}: ai_schedule {state.Schedule}: " +
                $"no AI task named '{name}' is registered; the schedule stops here");
            state.TaskIndex = steps.Length;
            return;
        }
        // A number under the wrong name is a number the task would misread: "seconds" given to a task
        // that wants metres. Said once, and the schedule stops rather than running on a guess.
        if (step.Argument != null && task.Argument != null && !string.Equals(step.Argument, task.Argument, StringComparison.OrdinalIgnoreCase))
        {
            Log.Once(LogCat.AI, LogLevel.Error, $"ai-arg:{state.Schedule}:{name}:{step.Argument}",
                $"{_records.Where("ai_schedule", state.Schedule, $"Tasks[{state.TaskIndex}]")}: ai_schedule {state.Schedule}: " +
                $"task '{name}' takes '{task.Argument}', not '{step.Argument}'; the schedule stops here");
            state.TaskIndex = steps.Length;
            return;
        }

        // The body's own movement numbers, for anything that has to produce a walkable result (F23).
        var movement = MovementProfileRecord.Fallback;
        if (world.TryGet<CharacterController>(entity, out var character))
            movement = CharacterConventions.Of(world).ProfileOf(_records, character.Profile);

        var context = new AITaskContext
        {
            World = world,
            Entity = entity,
            State = ref state,
            Intent = ref intent,
            Transform = ref transform,
            Profile = profile,
            Movement = movement,
            Space = _space,
            Attack = _attack,
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
