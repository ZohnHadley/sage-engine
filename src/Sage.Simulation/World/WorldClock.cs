#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// The world's clock (issue 4h-2, REDESIGN §4.7): what time of day it is, and how fast it goes.
//
// **Minimal on purpose.** A day count, an hour, and a speed: enough for a sky to be dusk and for a lamp to
// know it is night. The calendar (`Calendar`, `Date`; Calendar.cs) and passing time (`Time.Pass`; Time.cs)
// are phase 4g's, issue 4g-2; NPC schedules and the rest are 4g's later issues. They read this clock; the
// `sky` record's curve is rendering data, not gameplay.
//
// Saved, as the `clock` resource: `"clock": { "data": { "Day": 2, "Hour": 18.5, "Scale": 60, "Sky": "sage:day" } }`
// (test: TheClockSurvivesASave). A save from before the clock has no such resource, and loads with the
// world's own (noon of day 0) (test: AnOldSaveWithoutAClockStillLoads).
//
// `Hour` is in [0, 24): 18.5 is half past six in the evening. `Scale` is game seconds per real second
// (60 = a game minute a second, a day in 24 minutes; 0 stops it). It runs in fixed ticks, so two runs of a
// world agree (test: TheClockRunsInFixedTicksAndWrapsIntoTheNextDay).
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4h rendering: may change before 1.0
[SavedResource("clock", Plugin = RegistrationOwners.Core)]
public sealed class WorldClock
{
    public const double HoursPerDay = 24.0;

    private double _hour = 12.0;

    public int Day { get; set; }

    // The time of day, in hours, in [0, 24). Setting it wraps (25 is 1) and does not change the day.
    public double Hour
    {
        get => _hour;
        set => _hour = Wrap(value);
    }

    // Game seconds per real second; 0 stops the clock.
    public double Scale { get; set; } = 60.0;

    // The `sky` record the world is lit by; empty: no sky, and the light is what the game set (the
    // weather rules alone).
    public RecordId Sky { get; set; }

    // The `calendar` record the day count is read by (issue 4g-2); empty: `CalendarRecord.Default`. Saved
    // with the clock; a save from before it has none, and the world keeps what it has
    // (test: AClockSavedBeforeTheCalendarStillLoads).
    public RecordId Calendar { get; set; }

    // Today's date by `calendar` (the default one when none is given), from the day count alone: day, month,
    // year and weekday (test: MonthsAndYearsWrapInACustomCalendar). `Calendars.Today(world)` finds the
    // world's own calendar.
    public GameDate Date(CalendarRecord? calendar = null) => (calendar ?? CalendarRecord.Default).DateOf(Day);

    // Total game hours since day 0 began: a number that only goes up, for a schedule to compare.
    [System.Text.Json.Serialization.JsonIgnore]
    public double Elapsed => Day * HoursPerDay + _hour;

    public void Advance(double realSeconds)
    {
        if (Scale == 0.0 || realSeconds <= 0.0) return;
        double hours = _hour + realSeconds * Scale / 3600.0;
        double days = Math.Floor(hours / HoursPerDay);
        Day += (int)days;
        _hour = Wrap(hours);
    }

    // Moves the clock forward by game hours at once, day and all (issue 4g-2). Only `Time.Pass` calls it:
    // the simulation does not tick through the hours, a `TimePassed` event tells what cares to catch up.
    internal void Skip(double hours)
    {
        double total = _hour + hours;
        double days = Math.Floor(total / HoursPerDay);
        Day += (int)days;
        _hour = Wrap(total);
    }

    // Is it from `from` (inclusive) to `to` (exclusive)? A window may wrap past midnight: 20 to 6 is the
    // night. From equal to To is an empty window, not the whole day
    // (test: TimeBetweenWrapsPastMidnight).
    public static bool Between(double hour, double from, double to)
    {
        hour = Wrap(hour); from = Wrap(from); to = Wrap(to);
        if (from == to) return false;
        return from < to ? hour >= from && hour < to : hour >= from || hour < to;
    }

    // Hours and minutes for a HUD or the `time` command: "18:30".
    public static string Format(double hour)
    {
        hour = Wrap(hour);
        int h = (int)hour;
        int m = (int)Math.Round((hour - h) * 60.0);
        if (m == 60) { h = (h + 1) % 24; m = 0; }
        return $"{h:00}:{m:00}";
    }

    // Reads "18", "18.5" or "18:30" as an hour; false when it is none of those.
    public static bool TryParseHour(string text, out double hour)
    {
        hour = 0;
        int colon = text.IndexOf(':');
        if (colon < 0)
            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out hour);
        if (!int.TryParse(text.AsSpan(0, colon), out int h) || !int.TryParse(text.AsSpan(colon + 1), out int m)) return false;
        if (h < 0 || h > 24 || m < 0 || m >= 60) return false;
        hour = h + m / 60.0;
        return true;
    }

    private static double Wrap(double hours)
    {
        double wrapped = hours - Math.Floor(hours / HoursPerDay) * HoursPerDay;
        return wrapped >= HoursPerDay ? 0.0 : wrapped;
    }

    // The world's clock; made when a world has none (a bare one).
    public static WorldClock Of(World world) => world.Resources.GetOrAdd(static () => new WorldClock());
}

// Fixed Commands phase, ahead of everything that asks what time it is (entity I/O's conditions, the sky).
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[System(Id, Phase.Commands)]
internal sealed class WorldClockSystem : ISystem
{
    public const string Id = "sage.world.clock";

    private readonly World _world;

    public WorldClockSystem(World world) => _world = world;

    public void Run(in SystemContext ctx) => WorldClock.Of(_world).Advance(ctx.Tick.Dt);
}

// `{ "time_between": { "from": 20, "to": 6 } }`: the world's clock is in that window, which may wrap past
// midnight. Hours are numbers (18.5 is half past six); a lamp that comes on at night asks
// `{ "time_between": { "from": 19, "to": 6 } }`. A world with no clock reads as noon.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("time_between", Plugin = RegistrationOwners.Core)]
internal sealed class TimeBetweenCondition : ICondition
{
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour the window opens (inclusive); 18.5 is 18:30")]
    public double From;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour it closes (exclusive); less than From wraps past midnight")]
    public double To;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not at this hour";
        double hour = context.World.Resources.TryGet<WorldClock>(out var clock) && clock != null ? clock.Hour : 12.0;
        return WorldClock.Between(hour, From, To);
    }
}
