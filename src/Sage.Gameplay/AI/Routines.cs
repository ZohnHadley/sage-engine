#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Gameplay;

// NPC routines (issue 4g-4, REDESIGN §5 phase 4g): a smith at the forge by day and at home by night.
//
//   { "type": "routine", "id": "smith",
//     "entries": [ { "from": 8, "to": 20, "schedule": "work", "at": "forge" },
//                  { "from": 20, "to": 8, "schedule": "go_home", "at": "smith_bed" },
//                  { "from": 9, "to": 12, "days": ["Sunday"], "schedule": "pray", "at": "bed", "scene": "chapel" } ] }
//
// **A routine picks a schedule; it does not replace one** (plan decision 6). Every think, the entry in
// force at the clock's hour (the first that matches, in order) names an `ai_schedule` and a place, its
// *anchor*. When the anchor is in the world the creature has the `in_routine` condition, and the `default`
// selector (and so `rules`, which falls back to it) runs the entry's schedule wherever it would otherwise
// have idled. A fight still comes first; when it is over the schedule starts again from its first task, so
// the creature walks back (test: AFightInterruptsTheRoutineAndHeGoesBackToIt). When the entry changes, the
// schedule starts again too, even when both entries name the same one (test:
// TheSmithWalksToTheForgeAtEightAndHomeAtTwenty).
//
// **Entries.** `from` and `to` are hours, and a window may wrap past midnight (20 to 8 is the night);
// `from` equal to `to` (0 and 24) is the whole day. `days`, when given, are the weekdays it holds on, as the
// world's calendar names them (4g-2); a night that began on a listed day holds until its morning.
//
// **Anchors.** `at` names an entity: a placement's `name`, a map entity's `targetname`. With `scene` as
// well, the anchor is an entry in that scene. While that scene is not the one loaded the creature has no
// `in_routine` and idles where it is, and `Routines.Target` says where it ought to be: that is the seam for
// off-screen simulation (4g-6), which moves it there through the scene's door
// (test: AnAnchorInASceneThatIsNotLoadedIsRecordedNotWalkedTo).
//
// **The tasks.** `MoveToAnchor` walks there through the navigation grid, as `MoveToTarget` chases;
// `FaceAnchor` turns to face the way the anchor faces (or toward it, from further than a step away);
// `StayAt` stands there until the entry changes, and fails, so the schedule starts again and walks back,
// if it is pushed more than `distance` away.
//
// **Passing time** (`Time.Pass`, 4g-2) is caught up by arithmetic, not by ticks: on `TimePassed` a creature
// whose routine has an entry in force is put at that entry's anchor if it could have walked there since the
// entry began, at its movement profile's walking speed in a straight line. One that could not walks the
// rest of the way when the game resumes; one in a fight is left to it (test:
// PassingTimeSnapsTheSmithToWhereHisRoutineHasHim).
//
// Saved: the `routine` component's record and the AI state's schedule and task. Where it is in its routine
// is worked out again from the clock, and the anchor found again by name, so a creature saved on its way
// carries on after a load (test: SavedMidWalkHeArrivesAfterALoad).
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Record("routine", Plugin = "sage.gameplay.ai")]
public sealed class RoutineRecord
{
    [Property(Tooltip = "What it does when, in order: the first entry whose hours (and days) hold is the one in force")]
    public List<RoutineEntry> Entries = new();
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class RoutineEntry
{
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour it begins (inclusive); 8.5 is 08:30")]
    public double From;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour it ends (exclusive); less than From wraps past midnight, equal is all day")]
    public double To;
    [Property(Tooltip = "The weekdays it holds on, as the calendar names them; empty = every day")]
    public List<string> Days = new();
    [Property(Tooltip = "The schedule it runs, usually MoveToAnchor, FaceAnchor and StayAt")]
    public RecordRef<AIScheduleRecord> Schedule;
    [Property(Tooltip = "Where: the name of an entity (a placement's name, a map entity's targetname)")]
    public string At = "";
    [RecordRef("scene"), Property(Tooltip = "The scene `at` is in, when it is not the one this creature is in; empty = this one")]
    public RecordId Scene;
}

// "routine": "smith" — its day, by record, on this entity; wins over its AI profile's `routine`.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Component("sage:routine")]
public struct Routine : IComponent
{
    [RecordRef("routine"), Property(Tooltip = "Its daily routine")]
    public RecordId Id;
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[PrefabPart("routine", Plugin = "sage.gameplay.ai", Shorthand = nameof(Id))]
public sealed class RoutinePart : IPrefabPart
{
    [Property(Tooltip = "Its daily routine")]
    public RecordRef<RoutineRecord> Id;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Id.IsEmpty) { ctx.Error("needs a record id"); return; }
        ctx.World.Add(ctx.Entity, new Routine { Id = Id });
    }
}

// Where a creature's routine has it now (`Routines.Target`). `Entry` is -1 when no entry is in force.
// `Elsewhere` is an anchor in a scene that is not loaded: `Scene` and `At` say which, and off-screen
// simulation (4g-6) is what takes the creature there.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly record struct RoutineTarget(RecordId RoutineId, int Entry, RecordId Schedule, string At, RecordId Scene,
                                            Entity Anchor, bool Elsewhere)
{
    public bool InForce => Entry >= 0;
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class Routines
{
    // How long a creature waits before looking again for an anchor that was not there (a sector still
    // streaming in, a door not yet placed): a name lookup is a scan, so not every think.
    private const float RetrySeconds = 2f;

    private static readonly AIProfileRecord NoProfile = new();

    // The routine an entity follows: its `routine` component's, else its AI profile's; null for none.
    public static RoutineRecord? Of(World world, Entity entity) =>
        Of(world, world.Records(), entity, ProfileOf(world, entity));

    internal static RoutineRecord? Of(World world, RecordStore records, Entity entity, AIProfileRecord profile)
    {
        RecordId id = world.TryGet<Routine>(entity, out var own) && !own.Id.IsEmpty ? own.Id : profile.Routine.Id;
        return !id.IsEmpty && records.TryGet(id, out RoutineRecord routine) ? routine : null;
    }

    // The id of the routine an entity follows, or empty (4g-6: an off-screen agent keeps it).
    internal static RecordId IdOf(World world, Entity entity) => IdOf(world, entity, ProfileOf(world, entity));

    private static RecordId IdOf(World world, Entity entity, AIProfileRecord profile) =>
        world.TryGet<Routine>(entity, out var own) && !own.Id.IsEmpty ? own.Id : profile.Routine.Id;

    // The entry in force at `elapsed` (the clock's `Elapsed`: game hours since day 0), or -1.
    public static int EntryAt(RoutineRecord routine, CalendarRecord calendar, double elapsed)
    {
        int day = (int)Math.Floor(elapsed / WorldClock.HoursPerDay);
        double hour = elapsed - day * WorldClock.HoursPerDay;
        for (int i = 0; i < routine.Entries.Count; i++)
        {
            var entry = routine.Entries[i];
            double from = Wrap(entry.From), to = Wrap(entry.To);
            if (from != to && !WorldClock.Between(hour, from, to)) continue;
            if (entry.Days.Count > 0 && !OnDay(entry, calendar, hour >= from ? day : day - 1)) continue;
            return i;
        }
        return -1;
    }

    // When the time of the entry in force at `elapsed` began, as `Elapsed` hours: today's `from`, or
    // yesterday's when the window opened before midnight.
    public static double StartOf(RoutineEntry entry, double elapsed)
    {
        double day = Math.Floor(elapsed / WorldClock.HoursPerDay);
        double hour = elapsed - day * WorldClock.HoursPerDay;
        double from = Wrap(entry.From);
        return (hour >= from ? day : day - 1) * WorldClock.HoursPerDay + from;
    }

    // Where its routine has a creature now: the entry, its schedule and its anchor, looked up by name. For
    // tools and for off-screen simulation (4g-6), which takes over an `Elsewhere` anchor.
    public static RoutineTarget Target(World world, Entity entity)
    {
        var profile = ProfileOf(world, entity);
        var records = world.Records();
        var routineId = IdOf(world, entity, profile);
        var routine = Of(world, records, entity, profile);
        int index = routine == null ? -1 : EntryAt(routine, Calendars.Of(world), ElapsedOf(world));
        if (index < 0) return new RoutineTarget(routineId, -1, default, "", default, default, false);

        var entry = routine!.Entries[index];
        var anchor = entry.At.Length > 0 ? world.FindByName(entry.At) : default;
        return new RoutineTarget(routineId, index, entry.Schedule, entry.At, entry.Scene, anchor,
                                 anchor.IsNull && IsElsewhere(world, records, entry));
    }

    // ---- the think (AIThinkSystem) ----------------------------------------------------------------------

    // Sets `in_routine` and finds the schedule the routine runs now (empty: none). True when the entry in
    // force is not the one it was at the last think, so a schedule both entries share starts again.
    internal static bool Think(World world, RecordStore records, Entity entity, ref AIState state,
                               AIProfileRecord profile, float time, out RecordId schedule)
    {
        schedule = default;
        var routine = Of(world, records, entity, profile);
        int index = routine == null ? -1 : EntryAt(routine, Calendars.Of(world), ElapsedOf(world));
        bool changed = index + 1 != state.RoutineEntry;
        state.RoutineEntry = index + 1;
        if (index < 0) return changed;

        var entry = routine!.Entries[index];
        if (!TryAnchor(world, records, ref state, entry, time)) return changed;
        state.Conditions |= (ulong)AICondition.InRoutine;
        schedule = entry.Schedule;
        return changed;
    }

    // Finds the anchor of the entry in force (`state.RoutineEntry`), once per entry, and again every few
    // seconds while it is missing; true when it is in the world.
    private static bool TryAnchor(World world, RecordStore records, ref AIState state, RoutineEntry entry, float time)
    {
        if (state.AnchorFor == state.RoutineEntry)
        {
            if (world.IsAlive(state.Anchor)) return true;
            if (time < state.AnchorRetryAt) return false;
        }
        state.AnchorFor = state.RoutineEntry;
        state.Anchor = entry.At.Length > 0 ? world.FindByName(entry.At) : default;
        state.AnchorElsewhere = state.Anchor.IsNull && IsElsewhere(world, records, entry);
        state.AnchorRetryAt = time + RetrySeconds;
        if (state.Anchor.IsNull && !state.AnchorElsewhere)
            Log.Once(LogCat.AI, LogLevel.Warn, $"routine-anchor:{entry.At}",
                $"a routine's anchor '{entry.At}' is not in '{world.Name}'; creatures that go there idle until it is");
        return !state.Anchor.IsNull;
    }

    // An anchor named in a scene that is not the one the world has placed.
    private static bool IsElsewhere(World world, RecordStore records, RoutineEntry entry)
    {
        if (entry.Scene.IsEmpty) return false;
        var current = world.Engine?.Scenes.Scene(world);
        return current == null || !records.TryGet(entry.Scene, out SceneRecord scene) || !ReferenceEquals(scene, current);
    }

    // ---- passing time (AIThinkSystem, on TimePassed) ---------------------------------------------------

    // Puts a creature where its routine has it at the clock's new time, if it could have walked there since
    // the entry began; true when it did. Its schedule is the entry's, from the first task, which finds it
    // already there.
    internal static bool CatchUp(World world, RecordStore records, Entity entity, ref Transform transform, ref AIState state,
                                 ref PawnIntent intent, AIProfileRecord profile, double elapsed)
    {
        if (!state.Target.IsNull && world.IsAlive(state.Target)) return false;   // a fight is its own business
        var routine = Of(world, records, entity, profile);
        if (routine == null) return false;
        int index = EntryAt(routine, Calendars.Of(world), elapsed);
        if (index < 0) return false;

        // The anchor it has, when it is this entry's; else found by name. Nothing about the creature changes
        // unless it is moved: one that cannot get there finds its new entry at its next think, as in play.
        var entry = routine.Entries[index];
        Entity anchor = state.AnchorFor == index + 1 && world.IsAlive(state.Anchor) ? state.Anchor
            : entry.At.Length > 0 ? world.FindByName(entry.At) : default;
        if (anchor.IsNull) return false;

        var movement = MovementProfileRecord.Fallback;
        if (world.TryGet<CharacterController>(entity, out var character))
            movement = CharacterConventions.Of(world).ProfileOf(records, character.Profile);
        double seconds = (elapsed - StartOf(entry, elapsed)) * 3600.0;
        PositionOf(world, anchor, out var at, out float yaw);
        if (SageMath.DistanceXZ(transform.LocalPosition, at) > movement.WalkSpeed * seconds) return false;

        var placed = transform;
        placed.LocalPosition = at;
        placed.LocalRotation = SageMath.RotationFromYaw(yaw);
        world.Teleport(entity, placed);
        intent.Yaw = yaw;
        intent.Move = Vector2.Zero;
        if (world.Has<CharacterController>(entity)) world.Get<CharacterController>(entity).Velocity = Vector3.Zero;

        state.RoutineEntry = state.AnchorFor = index + 1;
        state.Anchor = anchor;
        state.AnchorElsewhere = false;
        state.Schedule = entry.Schedule;
        state.TaskIndex = 0;
        state.TaskTime = 0;
        state.TaskStarted = false;
        state.Path.Clear();
        return true;
    }

    // ---- for the tasks -----------------------------------------------------------------------------------

    // The anchor the running task works toward. False with the status to return when there is none: Running
    // while the think has not looked yet (the first tick after a load), Failed when it looked and found none.
    internal static bool AnchorFor(ref AITaskContext c, out Entity anchor, out AITaskStatus status)
    {
        anchor = c.State.Anchor;
        status = AITaskStatus.Running;
        if (!anchor.IsNull && c.World.IsAlive(anchor)) return true;
        c.Intent.Move = Vector2.Zero;
        if (c.State.RoutineEntry != 0 && c.State.AnchorFor == c.State.RoutineEntry) status = AITaskStatus.Failed;
        return false;
    }

    // Where an anchor is and which way it faces, in origin space.
    internal static void PositionOf(World world, Entity anchor, out Vector3 position, out float yaw)
    {
        if (anchor.Parent.IsNull || !world.TryGet<GlobalTransform>(anchor, out var global))
        {
            ref readonly var local = ref world.Get<Transform>(anchor);
            position = local.LocalPosition;
            yaw = SageMath.YawOf(local.LocalRotation);
            return;
        }
        position = global.Current.Position;
        yaw = SageMath.YawOf(global.Current.Rotation);
    }

    private static double ElapsedOf(World world) =>
        world.Resources.TryGet<WorldClock>(out var clock) && clock != null ? clock.Elapsed : 12.0;

    private static AIProfileRecord ProfileOf(World world, Entity entity)
    {
        var records = world.Records();
        var id = world.TryGet<AIState>(entity, out var state) && !state.Profile.IsEmpty ? state.Profile : world.Conventions().AiProfile.Id;
        return !id.IsEmpty && records.TryGet(id, out AIProfileRecord profile) ? profile : NoProfile;
    }

    private static bool OnDay(RoutineEntry entry, CalendarRecord calendar, int day)
    {
        int weekday = calendar.WeekdayOf(day);
        foreach (var name in entry.Days)
            if (calendar.WeekdayIndex(name) == weekday) return true;
        return false;
    }

    private static double Wrap(double hours)
    {
        double wrapped = hours - Math.Floor(hours / WorldClock.HoursPerDay) * WorldClock.HoursPerDay;
        return wrapped >= WorldClock.HoursPerDay ? 0.0 : wrapped;
    }
}

// Load checks for a routine (AIModule adds them): every entry needs a schedule and a place, its hours are
// hours, and its days are days some calendar has.
internal static class RoutineChecks
{
    public static void Check(RecordStore records, RoutineRecord routine, RecordCheck check)
    {
        for (int i = 0; i < routine.Entries.Count; i++)
        {
            var entry = routine.Entries[i];
            string at = $"Entries[{i}]";
            if (entry.Schedule.IsEmpty) check.Error(at, "an entry needs the \"schedule\" it runs");
            if (entry.At.Length == 0) check.Error(at, "an entry needs the name of the entity it is \"at\"");
            if (entry.From is < 0 or > 24 || double.IsNaN(entry.From)) check.Error($"{at}.From", $"{entry.From} is not an hour (0 to 24)");
            if (entry.To is < 0 or > 24 || double.IsNaN(entry.To)) check.Error($"{at}.To", $"{entry.To} is not an hour (0 to 24)");
            for (int d = 0; d < entry.Days.Count; d++)
                if (!IsAWeekday(records, entry.Days[d]))
                    check.Error($"{at}.Days[{d}]", $"no calendar has a weekday '{entry.Days[d]}'");
        }
    }

    private static bool IsAWeekday(RecordStore records, string name)
    {
        if (CalendarRecord.Default.WeekdayIndex(name) >= 0) return true;
        foreach (var id in records.Ids("calendar"))
            if (records.TryGet(id, out CalendarRecord calendar) && calendar.WeekdayIndex(name) >= 0) return true;
        return false;
    }
}

// ---- the tasks ------------------------------------------------------------------------------------------

// Walk to the routine's anchor until within `distance` metres (default 0.75), round what is in the way.
internal sealed class MoveToAnchorTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;

    public AITaskStatus Start(ref AITaskContext c)
    {
        c.State.Path.Clear();
        return AITaskStatus.Running;
    }

    public AITaskStatus Run(ref AITaskContext c)
    {
        if (!Routines.AnchorFor(ref c, out var anchor, out var status)) return status;
        Routines.PositionOf(c.World, anchor, out var at, out _);
        Vector3 self = c.Transform.LocalPosition;
        if (SageMath.DistanceXZ(self, at) <= (c.Param > 0 ? c.Param : 0.75f))
        {
            c.Intent.Move = Vector2.Zero;
            return AITaskStatus.Succeeded;
        }
        MoveToTargetTask.WalkToward(ref c, self, at, anchor);
        return AITaskStatus.Running;
    }
}

// Turn to face the way the anchor faces; from further than a step away, toward the anchor instead (a
// smith told to stand *by* the anvil rather than on it).
internal sealed class FaceAnchorTask : IAITask
{
    private const float OnIt = 1.25f;

    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        if (!Routines.AnchorFor(ref c, out var anchor, out var status)) return status;
        Routines.PositionOf(c.World, anchor, out var at, out float facing);
        Vector3 self = c.Transform.LocalPosition;
        float wanted = SageMath.DistanceXZ(self, at) > OnIt ? AIMath.YawTo(self, at) : facing;
        c.Intent.Yaw = AIMath.TurnToward(c.Intent.Yaw, wanted, c.Profile.TurnSpeedDegrees * MathF.PI / 180f * c.Dt);
        return MathF.Abs(AIMath.WrapPi(wanted - c.Intent.Yaw)) < 0.15f ? AITaskStatus.Succeeded : AITaskStatus.Running;
    }
}

// Stay at the anchor until the routine moves on (the think starts the next entry's schedule). Pushed more
// than `distance` metres away (default 2) it fails, and the schedule starts again: back it walks.
internal sealed class StayAtTask : IAITask
{
    public const string ArgumentName = "distance";
    public string? Argument => ArgumentName;

    public AITaskStatus Run(ref AITaskContext c)
    {
        c.Intent.Move = Vector2.Zero;
        if (!Routines.AnchorFor(ref c, out var anchor, out var status)) return status;
        Routines.PositionOf(c.World, anchor, out var at, out _);
        return SageMath.DistanceXZ(c.Transform.LocalPosition, at) > (c.Param > 0 ? c.Param : 2f)
            ? AITaskStatus.Failed : AITaskStatus.Running;
    }
}
