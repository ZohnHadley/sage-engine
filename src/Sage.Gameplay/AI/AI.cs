#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Gameplay;

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

    // Magic (16 §3.3, F21). `CanCastAtEnemy` is not "knows a spell": it is the whole question asked
    // and answered — a spell it knows, off cooldown, affordable, allowed, and whose own range covers
    // where the enemy is standing. `AbilityRules` answers it, so a creature never winds up something
    // the cast system would refuse.
    CanCastAtEnemy = 1 << 6,
    // A spell that would reach, held up only by its own cooldown. The difference from
    // `CanCastAtEnemy` is *why* it cannot cast, and it decides whether waiting is worth anything:
    // a cooldown ends on its own, an empty mana pool does not.
    SpellComingBack = 1 << 8,
    // Mid wind-up. A think during a cast sees every spell refused as `AlreadyCasting`, and without
    // this the agent decided it had no magic and walked off in the middle of its own spell.
    Casting = 1 << 9,

    // It cannot see the enemy but has not given up on it: the seconds after sight is broken, when a
    // creature walks to where it last saw somebody. **Pathfinding needs this to be worth anything**
    // (F23): walking round a wall means facing the wall's end rather than the target, and sight is a
    // cone, so without memory a creature forgets what it is chasing the moment it sets off round.
    RememberEnemy = 1 << 10,
    // Whether swinging is even an option. Without it a creature with no `Melee` walks into reach and
    // runs a melee schedule that can only fail, once a tick, for ever.
    CanMelee = 1 << 7,

    // Its routine has somewhere for it to be now, and it knows where (issue 4g-4, Routines.cs): an entry is
    // in force at this hour and its anchor is in the world. Named `in_routine` in content.
    InRoutine = 1 << 11,
}

// How an agent senses and fights (16 §3.4). Tuning is data, like movement profiles.
[Record("ai_profile", Plugin = "sage.gameplay.ai")]
public sealed class AIProfileRecord
{
    public float SightRange = 22f;
    // How long a creature keeps chasing something it can no longer see. Without it, pathfinding is
    // decoration: a creature that turns to walk round a wall loses its target on the first step (F23).
    public float MemorySeconds = 6f;
    public float SightAngleDegrees = 200f;   // generous: creatures notice you from the side
    public float MeleeRange = 1.8f;      // how close it wants to be before swinging
    public float ThinkRate = 6f;         // times per second
    public float TurnSpeedDegrees = 360f;

    // How it picks a schedule (issue #28): an `ai_schedule_selector` by id. Empty is `rules` when the
    // profile has rules and `default` (the engine's choice, through the conventions' schedules) when not.
    [VocabularyRef("ai_schedule_selector"), Property(Tooltip = "How it picks a schedule; empty = \"rules\" with rules, else \"default\"")]
    public string Selector = "";
    // The `rules` selector's: the first rule whose conditions hold names the schedule (AIScheduleRule).
    public List<AIScheduleRule> Rules = new();

    // Its day (issue 4g-4): a `routine` record, which picks a schedule and a place by the hour whenever
    // nothing more pressing (a fight) does. An entity's own `routine` part wins over this.
    [Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "Its daily routine; an entity's own `routine` part wins over it")]
    public RecordRef<RoutineRecord> Routine;

    // The selector named, found once (AIScheduleSelectors.Of): a think must not look it up by name.
    internal IAIScheduleSelector? SelectorInstance;
    internal string? SelectorFor;
}

// An ordered task list plus the conditions that interrupt it (16 §3.4). Each task is an object naming
// the task and, if it takes one, its argument by name:
//
//   "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }, "FaceTarget", { "task": "Wait", "seconds": 0.5 }]
//
// A task with no argument may be written as its bare name ("FaceTarget"). The old "Wait:1.5" strings
// are a load error that says what to write instead (issue #22): a number with no name is how a
// designer ends up writing seconds where a task wanted metres.
[Record("ai_schedule", Plugin = "sage.gameplay.ai")]
public sealed class AIScheduleRecord
{
    public List<AITaskStep> Tasks = new();
    [VocabularyRef("ai_condition")]
    public List<string> Interrupts = new();   // AI condition names (issue #28: the engine's and the game's)

    private ulong _mask;
    private List<string>? _maskFor;
    private AITaskStep[]? _steps;

    // As an array, made once: schedules are read every tick.
    public AITaskStep[] Steps() => _steps ??= Tasks.ToArray();

    // The interrupts as condition bits, made again only when a reload replaced the list. An unknown
    // name was a load error (AIChecks.Schedule), and means no bit here.
    public ulong InterruptMask(AIConditions conditions)
    {
        if (ReferenceEquals(_maskFor, Interrupts)) return _mask;
        _mask = conditions.MaskOf(Interrupts);
        _maskFor = Interrupts;
        return _mask;
    }
}

// The agent's running state (16 §4). Saved with the entity (09); Target is rebuilt on load.
[Component("sage:ai_state")]
public struct AIState : IComponent
{
    [RecordRef("ai_profile"), Property(Category = "Brain", Tooltip = "Senses and reach; empty = the game's default (gameplay_conventions)")]
    public RecordId Profile;
    [RecordRef("ai_schedule"), Property(Category = "Brain", Tooltip = "The task list it runs")]
    public RecordId Schedule;
    [Property(Category = "Running", Min = 0, Tooltip = "Which task of the schedule it is on")]
    public int TaskIndex;                  // where in the schedule; bounds-checked on use
    [Transient] public ulong Conditions;   // Perceive rebuilds it wholesale every think
    [Transient] public RecordId Spell;     // what Perceive picked to cast; rebuilt with the conditions
    public Entity Target;                  // by PersistentId in a save; null if it is gone
    [Property(Category = "Running", Unit = "s", Tooltip = "Simulation time of its next think")]
    public float NextThink;
    [Property(Category = "Running", Min = 0, Unit = "s", Tooltip = "How long the current task has been running")]
    public float TaskTime;      // seconds the current task has been running
    public bool TaskStarted;
    // The corners it is walking, if anything is in the way (16 §3.4, F23). Transient like the rest of
    // the thinking: a load starts a creature standing still, working it out again.
    [Transient] public NavPath Path;
    [Transient] public Vector3 LastSeen;    // where the target was when it was last in sight
    [Transient] public float ForgetAt;      // sim time after which it gives up on a target it cannot see

    // Where its routine has it now (issue 4g-4, Routines.cs). Internal, so never saved: the entry is worked
    // out from the clock and the anchor found by name again after a load.
    internal int RoutineEntry;             // the entry in force, plus one; 0 is none
    internal int AnchorFor;                // the RoutineEntry `Anchor` was looked up for
    internal Entity Anchor;                // the entity it is to be at; null when not found or elsewhere
    internal bool AnchorElsewhere;         // the anchor is in a scene that is not loaded (4g-6's)
    internal float AnchorRetryAt;          // sim time to look for a missing anchor again
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
    // How this body moves (radius, step height, slope limit): what a path has to be walkable *by*, so
    // navigation reuses the numbers the character controller already obeys rather than inventing its own.
    public MovementProfileRecord Movement;
    public IPhysicsWorld Space;
    public ActionId Attack;     // the Attack action, so a task can swing the way a player does
    public float Dt;
    public float Param;
}

public interface IAITask
{
    AITaskStatus Start(ref AITaskContext context) => AITaskStatus.Running;
    AITaskStatus Run(ref AITaskContext context);

    // The name of the one number this task takes in a schedule ("seconds", "distance"), which arrives
    // as `AITaskContext.Param`. A schedule that names another is an error when it runs. Null (a game's
    // task that has not said) takes any name.
    string? Argument => null;
}

// One step of a schedule (16 §3.4): which task, and its argument if it has one. `Argument` is the name
// the file gave the number, kept so a mismatch with the task's own can be reported.
[JsonConverter(typeof(AITaskStepJsonConverter))]
public readonly record struct AITaskStep(string Task, string? Argument = null, float Value = 0f);

// `{ "task": "Wait", "seconds": 1.5 }` or `"FaceTarget"`; "Wait:1.5" is refused with the object to write.
[SchemaShape("""
    {
      "description": "A task: its name (\"FaceTarget\"), or { \"task\": \"Wait\", \"seconds\": 1.5 } for one that takes a number.",
      "anyOf": [
        { "type": "string", "pattern": "^[^:]+$" },
        {
          "type": "object",
          "properties": { "task": { "type": "string", "minLength": 1, "description": "The task's name, such as \"Wait\"." } },
          "required": ["task"],
          "additionalProperties": { "type": "number" },
          "maxProperties": 2
        }
      ]
    }
    """)]
internal sealed class AITaskStepJsonConverter : JsonConverter<AITaskStep>
{
    public override AITaskStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string text = reader.GetString()!.Trim();
            int colon = text.IndexOf(':');
            if (colon >= 0)
            {
                string name = text[..colon].Trim(), value = text[(colon + 1)..].Trim();
                throw new JsonException($"\"{text}\" is the old task syntax; write {{ \"task\": \"{name}\", \"{AITaskRegistry.ArgumentOf(name)}\": {value} }}");
            }
            if (text.Length == 0) throw new JsonException("a task needs a name");
            return new AITaskStep(text);
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("a task is { \"task\": \"Wait\", \"seconds\": 1.5 }, or a bare name such as \"FaceTarget\"");

        string? task = null, argument = null;
        float number = 0f;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString()!;
            reader.Read();
            if (string.Equals(key, "task", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { Length: > 0 } name)
                    throw new JsonException("\"task\" is the name of a task, such as \"Wait\"");
                task = name.Trim();
                continue;
            }
            if (argument != null)
                throw new JsonException($"a task takes one argument; this one has '{argument}' and '{key}'");
            if (reader.TokenType != JsonTokenType.Number)
                throw new JsonException($"'{key}' must be a number");
            argument = key;
            number = reader.GetSingle();
        }
        if (task == null)
            throw new JsonException(argument == null ? "a task object needs \"task\": \"<name>\""
                : $"a task object needs \"task\": \"<name>\" beside '{argument}'");
        return new AITaskStep(task, argument, number);
    }

    public override void Write(Utf8JsonWriter writer, AITaskStep value, JsonSerializerOptions options)
    {
        if (value.Argument == null) { writer.WriteStringValue(value.Task); return; }
        writer.WriteStartObject();
        writer.WriteString("task", value.Task);
        writer.WriteNumber(value.Argument, value.Value);
        writer.WriteEndObject();
    }
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
        Register("CastSpell", new CastSpellTask());
        // A routine's (issue 4g-4, Routines.cs).
        Register("MoveToAnchor", new MoveToAnchorTask());
        Register("FaceAnchor", new FaceAnchorTask());
        Register("StayAt", new StayAtTask());
    }

    public void Register(string name, IAITask task) => _tasks[name] = task;

    public IAITask? Find(string name) => _tasks.TryGetValue(name, out var task) ? task : null;

    public IEnumerable<string> Names => _tasks.Keys;

    // The engine's own tasks' argument names, for an error that has no registry to ask (a record
    // being read): what to write in place of an old "Wait:1.5".
    internal static string ArgumentOf(string task) => task.ToLowerInvariant() switch
    {
        "wait" => WaitTask.ArgumentName,
        "movetotarget" => MoveToTargetTask.ArgumentName,
        "meleeattack" => MeleeAttackTask.ArgumentName,
        "castspell" => CastSpellTask.ArgumentName,
        "movetoanchor" => MoveToAnchorTask.ArgumentName,
        "stayat" => StayAtTask.ArgumentName,
        _ => "<argument>",
    };
}

// ---- The built-in tasks --------------------------------------------------------------------------

// Stand still for `seconds` (default 1).
internal sealed class WaitTask : IAITask
{
    public const string ArgumentName = "seconds";
    public string? Argument => ArgumentName;

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

// Walk toward the target until it is `distance` metres away (default: melee range).
//
// **Straight at it while that works, a path when it does not** (16 §3.4, F23). Most of the time the way
// is clear and a plan would be waste; the moment something solid is between them, the creature asks
// `Navigation` for the corners to walk and follows those instead. Before F23 it leaned on the wall.
internal sealed class MoveToTargetTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;

    // How close to a corner counts as reaching it, and how often a path is worth re-planning.
    private const float CornerReached = 0.8f;
    private const float ReplanSeconds = 0.6f;
    // After a search that found nothing, ask again far less often. A creature sealed in a room is the
    // case this exists for: it should cost one search every few seconds, not one every half second for
    // as long as it can see you.
    private const float NoRouteSeconds = 2.5f;
    private const float CheckSeconds = 0.2f;       // how long an answer about the way ahead is kept
    private const float TargetMoved = 3f;          // metres before the old plan is stale
    private const float StraightProbe = 0.6f;      // the body's width to check a clear line with
    private const float EdgeReached = 0.5f;        // how close to the edge of closed ground it stops (#271)

    public AITaskStatus Run(ref AITaskContext c)
    {
        if (!c.World.IsAlive(c.State.Target)) { c.Intent.Move = Vector2.Zero; return AITaskStatus.Failed; }

        Vector3 self = c.Transform.LocalPosition;
        // What it can see, or where it last saw it: a creature on its way round a wall is not looking at
        // its target, and a creature that walked to the target's *live* position while blind to it would
        // be cheating (16 §3.4).
        bool visible = (c.State.Conditions & (ulong)AICondition.SeeEnemy) != 0;
        Vector3 target = visible ? c.World.Get<Transform>(c.State.Target).LocalPosition : c.State.LastSeen;
        float stop = c.Param > 0 ? c.Param : c.Profile.MeleeRange;
        if (AIMath.DistanceXZ(self, target) <= stop)
        {
            c.Intent.Move = Vector2.Zero;
            return AITaskStatus.Succeeded;
        }

        WalkToward(ref c, self, target, c.State.Target);
        return AITaskStatus.Running;
    }

    // One tick of walking at `goal` (a target, or a routine's anchor): straight while the way is clear, the
    // corners of a path when it is not, steering off what is ahead. `goalEntity` is left out of the grid,
    // so the thing walked to does not block its own cell.
    internal static void WalkToward(ref AITaskContext c, Vector3 self, Vector3 goal, Entity goalEntity)
    {
        Vector3 steerTo = Navigate(ref c, ref c.State.Path, self, goal, goalEntity, out bool hold);

        float wanted = AIMath.YawTo(self, steerTo);
        float turn = c.Profile.TurnSpeedDegrees * MathF.PI / 180f * c.Dt;
        // Not while crossing a link or a door: steering off the door it is waiting for, or the wall a
        // ladder is on, is exactly wrong.
        if (!c.State.Path.InCrossing) wanted += Avoid(ref c, self, wanted);

        // Round other creatures (#271, Crowd.cs): with somebody near, the crowd picks the velocity, and the
        // creature walks it at once — sideways if need be, since a body does not have to face the way it
        // steps aside — while it turns to face it. With nobody near it walks as below, as it always did.
        if (!hold && !c.State.Path.InCrossing && c.World.Resources.TryGet<Navigation>(out var nav) && nav != null && nav.Avoidance &&
            c.World.TryGet<CharacterController>(c.Entity, out var body))
        {
            float speed = MathF.Max(c.Movement.WalkSpeed, 0.1f);
            var preferred = SageMath.ForwardFromYaw(wanted) * speed;
            if (nav.Crowd.Steer(c.World, c.Space, c.Entity, goalEntity, self, preferred, body.Velocity, c.Movement.Radius, speed,
                                c.Movement.StepHeight + 0.1f, c.Dt, ref c.State.Path.Stuck, out var velocity))
            {
                if (velocity.LengthSquared() > 0.05f * 0.05f) c.Intent.Yaw = AIMath.TurnToward(c.Intent.Yaw, SageMath.YawOf(velocity), turn);
                var forward = SageMath.ForwardFromYaw(c.Intent.Yaw);
                var right = new Vector3(-forward.Z, 0, forward.X);
                c.Intent.Move = new Vector2(Vector3.Dot(velocity, right), Vector3.Dot(velocity, forward)) / speed;
                return;
            }
        }

        c.Intent.Yaw = AIMath.TurnToward(c.Intent.Yaw, wanted, turn);
        c.Intent.Move = hold ? Vector2.Zero : new Vector2(0, 1);   // forward, in the direction it is facing
    }

    // Where to walk next: the target itself when nothing is in the way, otherwise the next corner of a
    // path. Returns the point to steer at, and keeps the path in step as the creature walks it. `hold`
    // is a crossing saying "stand here, facing that" (a door still opening).
    private static Vector3 Navigate(ref AITaskContext c, ref NavPath path, Vector3 self, Vector3 target, Entity goalEntity, out bool hold)
    {
        hold = false;
        path.ReplanIn -= c.Dt;
        path.CheckIn -= c.Dt;

        // Part way over a crossing (#265) it finishes it, whatever the line to the target says: a creature
        // half up a ladder that sees its target does not let go.
        if (path.InCrossing) return Cross(ref c, ref path, self, target, out hold);

        // The cheap question first, and the one that is usually enough: can it just walk at the thing?
        // A clear line means the path is dropped, so a creature stops following corners the moment it
        // does not need them.
        //
        // Three raycasts, so it is asked about five times a second rather than sixty: a wall does not
        // appear and vanish between ticks, and this is the one cost F23 adds to *every* chasing creature
        // whether or not anything is in its way.
        if (path.CheckIn <= 0f)
        {
            path.CheckIn = CheckSeconds;
            path.LineBlocked = !LineIsWalkable(ref c, self, target);

            // Ground that costs (#271): a clear line is still not the way when a road nearby is cheaper, or
            // when it crosses ground closed to the creature's faction. The navmesh says; with no areas in
            // the game it says "straight" without looking.
            path.LineForbidden = false;
            if (c.World.Resources.TryGet<Navigation>(out var areas) && areas != null)
            {
                var line = areas.StraightLine(c.World, self, target, c.Movement.Radius, c.Movement.StandHeight,
                                              Navigation.FactionOf(c.World, c.Entity), out path.Edge);
                path.LineForbidden = line == NavLine.Forbidden;
                if (line != NavLine.Best) path.LineBlocked = true;
            }

            // The world changed under the path (#265): a blocker came to rest in it, a locked door shut,
            // a link was switched. Plan again now rather than at the next re-plan.
            if (path.Walking && c.World.Resources.TryGet<Navigation>(out var changed) && changed != null &&
                changed.Changed(c.World, path.Version))
                path.ReplanIn = 0f;
        }
        if (!path.LineBlocked)
        {
            path.Clear();
            path.NoWayThrough = false;
            return target;
        }

        bool stale = path.Count == 0 || path.ReplanIn <= 0f ||
                     AIMath.DistanceXZ(path.PlannedFor, target) > TargetMoved;

        if (stale && c.World.Resources.TryGet<Navigation>(out var nav) && nav != null)
        {
            nav.Plan(c.World, c.Space, self, target, c.Movement.Radius, c.Movement.StandHeight, Navigation.FactionOf(c.World, c.Entity),
                     c.Movement.StepHeight, c.Movement.MaxSlopeDegrees, c.Entity, goalEntity, ref path);
            path.ReplanIn = path.NoWayThrough ? NoRouteSeconds : ReplanSeconds;
        }

        // No way through: lean on it as before, and keep trying — unless the way is closed to it (#271), when
        // it walks to the edge of that ground and stands there rather than walking in after what it wants.
        if (!path.Walking)
        {
            if (!path.NoWayThrough || !path.LineForbidden) return target;
            hold = AIMath.DistanceXZ(self, path.Edge) <= EdgeReached;
            return path.Edge;
        }

        // Corners are reached, not aimed at for ever: once inside `CornerReached` (or once the *next* one
        // is visible) it moves on, which is what stops a creature stopping dead on every corner.
        while (path.Walking && !path.InCrossing && AIMath.DistanceXZ(self, path.Next) <= CornerReached) path.Step++;
        if (path.InCrossing) return Cross(ref c, ref path, self, target, out hold);
        if (!path.Walking) { path.Clear(); return target; }
        return path.Next;
    }

    // ---- crossings (#265) -------------------------------------------------------------------------

    private const float DoorWaitSeconds = 6f;     // a door that has not opened by then is not going to
    private const float CrossingSeconds = 8f;     // nor will a link that has not been crossed by then
    private const float ClimbSpeed = 2f;          // m/s up or down a ladder
    private const float JumpClearance = 0.6f;     // how far above the higher end a jump's arc peaks
    private const float LandedHeight = 1f;        // the far side is reached within this height of it
    private const float LiftOff = 0.2f;           // seconds after a launch before touching ground is landing
    private const float LiftOffHeight = 0.1f;     // how far a climb starts off the ground
    private const float MaxJumpSpeed = 12f;       // m/s across, steering an arc that has gone wrong

    // One tick of crossing a door or a link: it is at the near side and `path.Next` is the far side.
    // Returns where to steer, and `hold` when it should stand still facing it.
    private static Vector3 Cross(ref AITaskContext c, ref NavPath path, Vector3 self, Vector3 target, out bool hold)
    {
        hold = false;
        Vector3 end = path.Next;
        path.CrossingTime += c.Dt;
        var entity = path.CrossingEntity;

        switch (path.Crossing)
        {
            case NavCrossing.Door:
            {
                // Gone (or not a mover any more): there is nothing to open, so walk.
                if (!c.World.IsAlive(entity) || !c.World.TryGet<Mover>(entity, out var mover)) break;
                bool open = mover.Position >= 1f && mover.Direction >= 0;
                if (open) break;
                if (c.World.TryGet<NavDoor>(entity, out var rules) && rules.Locked) return GiveUp(ref path, target);
                if (path.CrossingTime > DoorWaitSeconds) return GiveUp(ref path, target);

                // Neither open nor opening: ask it, as a button would, with itself as the activator. Asked
                // again if it starts to close before it was through (a door on a timer).
                if (mover.Direction <= 0 && c.World.Resources.TryGet<EntityIO>(out var io) && io != null)
                {
                    io.FireInput(entity, "Open", activator: c.Entity, caller: c.Entity);
                    path.CrossingStarted = true;
                }
                hold = true;
                return end;
            }

            case NavCrossing.Teleport:
                c.Transform.LocalPosition = end;
                if (c.World.Has<CharacterController>(c.Entity)) c.World.Get<CharacterController>(c.Entity).Velocity = Vector3.Zero;
                path.Step++;
                return end;

            case NavCrossing.Jump:
            {
                if (!c.World.Has<CharacterController>(c.Entity)) break;
                ref var body = ref c.World.Get<CharacterController>(c.Entity);
                if (!path.CrossingStarted)
                {
                    // From the ground or not at all: face it and wait to land first.
                    if (body.Grounded)
                    {
                        Launch(ref body, self, end, c.Movement.Gravity);
                        path.CrossingStarted = true;
                        path.CrossingTime = 0f;   // from here, the time in the air
                    }
                    else hold = true;
                    return end;
                }
                // In the air it holds the arc's speed across the gap (air control would bend it toward
                // walking pace); on the ground again it has landed, wherever that was.
                if (!body.Grounded || path.CrossingTime < LiftOff) { body.Velocity = Steer(self, end, body.Velocity, c.Movement.Gravity); return end; }
                path.Step++;
                return end;
            }

            case NavCrossing.Ladder:
            {
                if (!c.World.Has<CharacterController>(c.Entity)) break;
                ref var body = ref c.World.Get<CharacterController>(c.Entity);
                bool below = end.Y - self.Y > 0.02f, above = end.Y - self.Y < -0.05f;
                float across = AIMath.DistanceXZ(self, end);
                // At the far side's height and standing, or over it: the rest is a walk.
                if (!below && !above && (body.Grounded || across <= 0.5f)) break;
                if (path.CrossingTime > CrossingSeconds) return GiveUp(ref path, target);

                // Until ladders (#263) give it a volume to hold on to, the climb is the creature's own:
                // straight up (or down) at climbing speed, pressed toward the far side, which it steps
                // onto once it is above the edge.
                float dt = MathF.Max(c.Dt, 1e-4f);
                float vertical = below ? MathF.Min(ClimbSpeed, (end.Y + 0.1f - self.Y) / dt)
                               : above ? -MathF.Min(ClimbSpeed, (self.Y - end.Y) / dt) : 0f;
                var sideways = end - self;
                sideways.Y = 0;
                sideways = across > 1e-3f ? sideways / across * MathF.Min(ClimbSpeed, across / dt) : Vector3.Zero;
                // Gravity is the controller's, and it is added after this: paid in advance.
                body.Velocity = sideways + Vector3.UnitY * (vertical - c.Movement.Gravity * c.Dt);
                // Off the ground first: a climb is slower than the controller's ground check reaches, and
                // it would put the creature back on the floor every tick.
                if (body.Grounded && below) c.Transform.LocalPosition += Vector3.UnitY * LiftOffHeight;
                body.Grounded = false;
                return end;
            }
        }

        // Walking (a door that is open, a walk, a drop, the end of a jump or a climb): to the far side.
        if (AIMath.DistanceXZ(self, end) <= CornerReached && MathF.Abs(self.Y - end.Y) <= LandedHeight)
        {
            path.Step++;
            return end;
        }
        if (path.CrossingTime > CrossingSeconds) return GiveUp(ref path, target);
        return end;
    }

    // The crossing did not happen: forget the path and leave it a while before asking again.
    private static Vector3 GiveUp(ref NavPath path, Vector3 target)
    {
        path.Clear();
        path.NoWayThrough = true;
        path.ReplanIn = NoRouteSeconds;
        return target;
    }

    // A jump's launch: up enough to clear the higher end, and across at whatever speed lands it at the far
    // end on the way down.
    private static void Launch(ref CharacterController body, Vector3 from, Vector3 to, float gravity)
    {
        float g = MathF.Max(MathF.Abs(gravity), 0.1f);
        float rise = to.Y - from.Y;
        float peak = MathF.Max(rise, 0f) + JumpClearance;
        float up = MathF.Sqrt(2f * g * peak);
        // The later of the two times it is at the far end's height: falling onto it, not rising past it.
        float time = (up + MathF.Sqrt(MathF.Max(up * up - 2f * g * rise, 0f))) / g;
        var across = to - from;
        across.Y = 0;
        body.Velocity = across / MathF.Max(time, 0.1f) + Vector3.UnitY * up;
        body.Grounded = false;
    }

    // The jump's horizontal velocity again, from where it is now: whatever lands it on the far end in the
    // time its fall has left. Air control bleeds a jump toward walking pace every tick, so the arc is
    // flown rather than thrown.
    private static Vector3 Steer(Vector3 self, Vector3 end, Vector3 velocity, float gravity)
    {
        float g = MathF.Max(MathF.Abs(gravity), 0.1f);
        var across = end - self;
        across.Y = 0;
        float distance = across.Length();
        if (distance < 0.05f) return velocity with { X = 0, Z = 0 };
        float above = self.Y - end.Y;
        float time = (velocity.Y + MathF.Sqrt(MathF.Max(velocity.Y * velocity.Y + 2f * g * above, 0f))) / g;
        // Below the far end already, it has missed: no faster than a hard run into the side of it.
        return across / distance * MathF.Min(distance / MathF.Max(time, 0.05f), MaxJumpSpeed) + Vector3.UnitY * velocity.Y;
    }

    // Is there room to walk straight there? A ray down the middle and one along each shoulder, so a
    // doorway narrower than the body is not mistaken for a clear line.
    private static bool LineIsWalkable(ref AITaskContext c, Vector3 self, Vector3 target)
    {
        Vector3 eye = self + Vector3.UnitY * (c.Movement.StepHeight + 0.1f);
        Vector3 to = target - self;
        to.Y = 0;
        float distance = to.Length();
        if (distance < 0.01f) return true;

        Vector3 direction = to / distance;
        Vector3 side = new Vector3(-direction.Z, 0, direction.X) * StraightProbe * 0.5f;
        var mask = LayerMask.All.Except(c.Space.Layers.Enemy).Except(c.Space.Layers.Player);

        return !c.Space.Raycast(eye, direction, distance, mask).Hit
            && !c.Space.Raycast(eye + side, direction, distance, mask).Hit
            && !c.Space.Raycast(eye - side, direction, distance, mask).Hit;
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
// miss. `giveUpAfter` is how many seconds to keep trying (default 1.5), which is what makes it
// wait out its own cooldown instead of failing the moment it is not ready.
internal sealed class MeleeAttackTask : IAITask
{
    public const string ArgumentName = "giveUpAfter";
    public string? Argument => ArgumentName;

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

// Cast the spell the think picked (16 §3.3, §3.4). The agent asks for the cast the same way the
// console and a pressed button do — `world.Cast` queues it and `AbilitySystem` applies the rules — so
// a creature's fireball is the player's fireball, cooldown, mana and all.
//
// It aims with **pitch as well as yaw**, because a projectile leaves the caster's eye and the target's
// chest is lower: without it a creature firing across a room shoots over your head.
//
// `giveUpAfter` is how many seconds to keep trying (default 2), which covers a wind-up and lets
// the schedule end tidily if something eats the cast.
internal sealed class CastSpellTask : IAITask
{
    public const string ArgumentName = "giveUpAfter";
    public string? Argument => ArgumentName;

    public AITaskStatus Start(ref AITaskContext c)
    {
        if (c.State.Spell.IsEmpty) return AITaskStatus.Failed;
        if (!c.World.IsAlive(c.State.Target)) return AITaskStatus.Failed;
        if (!c.World.Has<Abilities>(c.Entity))
        {
            Log.Once(LogCat.AI, LogLevel.Warn, $"ai-cast:{World.Describe(c.Entity)}",
                $"{World.Describe(c.Entity)} runs a CastSpell task but has no Abilities component, so it cannot cast (16 §3.3)");
            return AITaskStatus.Failed;
        }

        c.World.Cast(c.Entity, c.State.Spell);
        return AITaskStatus.Running;
    }

    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        if (!c.World.IsAlive(c.State.Target)) return AITaskStatus.Failed;

        // Keep tracking through the wind-up: the aim is read when the spell goes off, not when it
        // was asked for, so a target that steps aside during a slow cast is still followed.
        Vector3 self = c.Transform.LocalPosition;
        Vector3 target = c.World.Get<Transform>(c.State.Target).LocalPosition;
        c.Intent.Yaw = AIMath.TurnToward(c.Intent.Yaw, AIMath.YawTo(self, target),
                                         c.Profile.TurnSpeedDegrees * MathF.PI / 180f * c.Dt);
        c.Intent.Pitch = SageMath.PitchTo(self + Vector3.UnitY * 1.4f, target + Vector3.UnitY * 1.0f);

        // Still winding up. `Casting` is set by AbilitySystem on the tick after the ask (the AI phase
        // runs after Gameplay), so "empty" only means finished once it has had a tick to start.
        if (!c.World.TryGet<Abilities>(c.Entity, out var abilities)) return AITaskStatus.Failed;
        if (!abilities.Casting.IsEmpty) return AITaskStatus.Running;
        if (c.State.TaskTime < c.Dt * 2f) return AITaskStatus.Running;

        // Gone off, or refused — either way this agent has had its go. A refusal sent `CastRefused`
        // and the next think will find `CanCastAtEnemy` false, so it chases or waits instead of
        // standing here asking again.
        return c.State.TaskTime > (c.Param > 0 ? c.Param : 2f) ? AITaskStatus.Failed : AITaskStatus.Succeeded;
    }
}

// Was the AI's private copy of the angle helpers, which is how the conventions drifted (review #43).
// Everything here now lives in SageMath, which games can use too: IAITask is public, so a game's own
// task needs the same maths the engine's tasks use.
internal static class AIMath
{
    public static float YawTo(Vector3 self, Vector3 target) => SageMath.YawTo(self, target);

    public static float DistanceXZ(Vector3 a, Vector3 b) => SageMath.DistanceXZ(a, b);

    public static float WrapPi(float angle) => SageMath.WrapPi(angle);

    public static float TurnToward(float from, float to, float maxStep) => SageMath.TurnToward(from, to, maxStep);
}
