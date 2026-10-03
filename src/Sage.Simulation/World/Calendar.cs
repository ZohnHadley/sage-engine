#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// The calendar (issue 4g-2, REDESIGN §5 phase 4g): what the clock's day count means as a date.
//
//   { "type": "calendar", "id": "reach", "startYear": 412, "startWeekday": 2,
//     "weekdays": ["Sundas", "Morndas", "Tirdas", "Middas", "Turdas", "Fredas", "Loredas"],
//     "months": [ { "name": "Morning Star", "days": 31 }, { "name": "Sun's Dawn", "days": 28 } ] }
//
// Day 0 of the clock is the first day of the first month of `startYear`, and falls on weekday number
// `startWeekday` of the list (0 is the first name). `WorldClock.Date` turns the day count into a
// day, month, year and weekday by walking the months, so months and years wrap by arithmetic and a
// clock saved before the calendar existed has the same `Day` it always had (test:
// MonthsAndYearsWrapInACustomCalendar, AClockSavedBeforeTheCalendarStillLoads).
//
// A world chooses its calendar with `WorldClock.Calendar`; with none it has `CalendarRecord.Default`,
// twelve months of the usual lengths (no leap years), Monday to Sunday, year 1, day 0 a Monday
// (test: TheDefaultCalendarHasMonday).
//
// Leap years, seasons and the moon (issue 4m-15) are optional fields of the same record:
//
//   "leapEvery": 4, "leapSkipEvery": 100, "leapRestoreEvery": 400, "leapMonth": 2     (the Gregorian rule)
//   "seasons": [ { "name": "Sun's Height", "month": 6, "day": 1 }, ... ]                (each runs to the next start)
//   "moonCycle": 29.53, "moonStart": 0                                                  (days; day 0 is moonStart days in)
//
// A leap year is one divisible by `leapEvery`, unless divisible by `leapSkipEvery`, unless divisible by
// `leapRestoreEvery`; the skip must be a multiple of the leap period and the restore of the skip, or it is
// ignored. A leap year has one more day in `leapMonth` (0, the default: the last month); the year is found
// by arithmetic, not by walking years, so a day count of a billion is as cheap as day 3
// (test: LeapYearsFollowTheRule). With no `leapEvery` there are none, as before. The default calendar has no leap
// years but has the four seasons from 1 March, and the moon's usual 29.53-day cycle; a custom calendar has
// seasons only when it lists them (test: SeasonsRunToTheNextStart, TheMoonCyclesThroughEightPhases).
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Record("calendar", Plugin = RegistrationOwners.Core)]
public sealed class CalendarRecord
{
    [Property(Tooltip = "The months of a year, in order, with their lengths. Empty: twelve months of the usual lengths")]
    public List<CalendarMonth> Months = new();

    [Property(Tooltip = "The names of the days of the week, in order. Empty: Monday to Sunday")]
    public List<string> Weekdays = new();

    [Property(Tooltip = "The year the clock's day 0 is in")]
    public int StartYear = 1;

    [Property(Min = 0, Tooltip = "Which weekday (0 is the first of Weekdays) the clock's day 0 is")]
    public int StartWeekday;

    [Property(Min = 0, Unit = "years", Tooltip = "A year divisible by this is a leap year; 0: no leap years")]
    public int LeapEvery;

    [Property(Min = 0, Unit = "years", Tooltip = "Unless divisible by this too (a multiple of leapEvery; 100 in the Gregorian rule); 0: no exception")]
    public int LeapSkipEvery;

    [Property(Min = 0, Unit = "years", Tooltip = "Unless divisible by this as well (a multiple of leapSkipEvery; 400 in the Gregorian rule); 0: none")]
    public int LeapRestoreEvery;

    [Property(Min = 0, Tooltip = "The month that has the extra day in a leap year (1 is the first); 0: the last month")]
    public int LeapMonth;

    [Property(Tooltip = "The seasons, each starting on a month and day and running to the next one's start. Empty: none (the default calendar has the four usual ones)")]
    public List<CalendarSeason> Seasons = new();

    [Property(Min = 0, Unit = "days", Tooltip = "Days from one new moon to the next; 0: 29.53")]
    public double MoonCycle;

    [Property(Unit = "days", Tooltip = "How many days into the moon's cycle the clock's day 0 is (0 is a new moon)")]
    public double MoonStart;

    private static readonly string[] DefaultMonthNames =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };
    private static readonly int[] DefaultMonthDays = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
    private static readonly string[] DefaultWeekdays =
        { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    // What a world has when it names no calendar.
    public static CalendarRecord Default { get; } = new();

    public int MonthCount => Months.Count == 0 ? DefaultMonthNames.Length : Months.Count;
    public int WeekdayCount => Weekdays.Count == 0 ? DefaultWeekdays.Length : Weekdays.Count;

    public string MonthName(int month) =>
        Months.Count == 0 ? DefaultMonthNames[month - 1] : Months[month - 1].Name;
    public string WeekdayName(int weekday) =>
        Weekdays.Count == 0 ? DefaultWeekdays[weekday] : Weekdays[weekday];

    // Days in month 1..MonthCount in a year that is not a leap year; a month written with fewer than one day
    // counts as one, so a bad record cannot make the walk loop or divide by zero.
    public int DaysIn(int month) =>
        Months.Count == 0 ? DefaultMonthDays[month - 1] : Math.Max(1, Months[month - 1].Days);

    // The same in a given year: the leap month has one more day in a leap year.
    public int DaysIn(int month, int year) => DaysIn(month) + (month == LeapMonthIndex && IsLeapYear(year) ? 1 : 0);

    // Days in a year that is not a leap year.
    public int DaysInYear
    {
        get
        {
            int total = 0;
            for (int m = 1; m <= MonthCount; m++) total += DaysIn(m);
            return total;
        }
    }

    public int DaysInYearOf(int year) => DaysInYear + (IsLeapYear(year) ? 1 : 0);

    private int LeapMonthIndex => LeapMonth >= 1 && LeapMonth <= MonthCount ? LeapMonth : MonthCount;

    // The rule's three periods as they apply: a skip that is not a multiple of the leap period, or a restore
    // that is not a multiple of the skip, is ignored.
    private (int Every, int Skip, int Restore) Rule()
    {
        int every = Math.Max(0, LeapEvery);
        int skip = every > 0 && LeapSkipEvery > 0 && LeapSkipEvery % every == 0 ? LeapSkipEvery : 0;
        int restore = skip > 0 && LeapRestoreEvery > 0 && LeapRestoreEvery % skip == 0 ? LeapRestoreEvery : 0;
        return (every, skip, restore);
    }

    public bool IsLeapYear(int year)
    {
        var (every, skip, restore) = Rule();
        if (every == 0 || FloorMod(year, every) != 0) return false;
        if (skip == 0 || FloorMod(year, skip) != 0) return true;
        return restore > 0 && FloorMod(year, restore) == 0;
    }

    // How many leap years there are up to and including `year`, counted from year 0 (differences are what matter).
    private long LeapsUpTo(long year, (int Every, int Skip, int Restore) rule)
    {
        if (rule.Every == 0) return 0;
        long n = FloorDiv(year, rule.Every);
        if (rule.Skip > 0) n -= FloorDiv(year, rule.Skip);
        if (rule.Restore > 0) n += FloorDiv(year, rule.Restore);
        return n;
    }

    // Days from day 0 to the start of the year `offset` years after StartYear (negative: before it).
    private long DaysBeforeYear(long offset, int baseYear, (int Every, int Skip, int Restore) rule) =>
        offset * baseYear + LeapsUpTo(StartYear + offset - 1, rule) - LeapsUpTo(StartYear - 1L, rule);

    // The weekday (0-based) of a day count; floor arithmetic, so a day before day 0 still has one.
    public int WeekdayOf(int day) => FloorMod(StartWeekday + (long)day, WeekdayCount);

    // The date of a day count (the clock's `Day`), however far it is from day 0.
    public GameDate DateOf(int day)
    {
        int baseYear = DaysInYear;
        var rule = Rule();
        double average = baseYear;
        if (rule.Every > 0)
        {
            average += 1.0 / rule.Every;
            if (rule.Skip > 0) average -= 1.0 / rule.Skip;
            if (rule.Restore > 0) average += 1.0 / rule.Restore;
        }
        long offset = (long)Math.Floor(day / average);          // close; the loops below settle it exactly
        while (DaysBeforeYear(offset, baseYear, rule) > day) offset--;
        while (DaysBeforeYear(offset + 1, baseYear, rule) <= day) offset++;
        int year = (int)(StartYear + offset);
        long rest = day - DaysBeforeYear(offset, baseYear, rule);
        int month = 1;
        while (month < MonthCount && rest >= DaysIn(month, year)) { rest -= DaysIn(month, year); month++; }
        int weekday = WeekdayOf(day);
        return new GameDate(year, month, (int)rest + 1, weekday, MonthName(month), WeekdayName(weekday));
    }

    // The name of the season a date is in ("" when the calendar has none).
    public string SeasonOf(GameDate date)
    {
        var seasons = Seasons.Count > 0 ? Seasons : Months.Count == 0 ? DefaultSeasons : null;
        if (seasons == null) return "";
        long now = date.Month * 1000L + date.Day;
        string best = "", last = "";
        long bestAt = long.MinValue, lastAt = long.MinValue;
        foreach (var s in seasons)
        {
            long at = s.Month * 1000L + s.Day;
            if (at <= now && at >= bestAt) { bestAt = at; best = s.Name; }
            if (at >= lastAt) { lastAt = at; last = s.Name; }
        }
        return bestAt == long.MinValue ? last : best;           // before the first start: the last season, wrapped
    }

    public const int MoonPhaseCount = 8;
    public static readonly IReadOnlyList<string> MoonPhaseNames = new[]
    {
        "new", "waxing_crescent", "first_quarter", "waxing_gibbous", "full", "waning_gibbous", "last_quarter", "waning_crescent",
    };

    // How far through its cycle the moon is on a day, in [0, 1): 0 new, 0.5 full.
    public double MoonAge(int day)
    {
        double cycle = MoonCycle > 0 && double.IsFinite(MoonCycle) ? MoonCycle : 29.53;
        double pos = (day + (double.IsFinite(MoonStart) ? MoonStart : 0)) / cycle;
        pos -= Math.Floor(pos);
        return pos >= 1.0 ? 0.0 : pos;
    }

    // The moon's phase on a day, as an index into MoonPhaseNames: eight, each a eighth of the cycle centred on
    // its own moment (the new moon spans the cycle's end and start).
    public int MoonPhaseOf(int day) => (int)Math.Floor(MoonAge(day) * MoonPhaseCount + 0.5) % MoonPhaseCount;

    // How much of the moon is lit on a day, 0 (new) to 1 (full).
    public double MoonLight(int day) => (1.0 - Math.Cos(MoonAge(day) * 2.0 * Math.PI)) / 2.0;

    public static int MoonPhaseIndex(string name)
    {
        for (int i = 0; i < MoonPhaseCount; i++)
            if (string.Equals(MoonPhaseNames[i], name.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static readonly List<CalendarSeason> DefaultSeasons = new()
    {
        new() { Name = "spring", Month = 3, Day = 1 }, new() { Name = "summer", Month = 6, Day = 1 },
        new() { Name = "autumn", Month = 9, Day = 1 }, new() { Name = "winter", Month = 12, Day = 1 },
    };

    // Is this date in the window that opens on (fromMonth, fromDay) and closes on (toMonth, toDay), both ends
    // included? Months and days count from 1. A window may wrap past the end of the year: 1 Dec to 28 Feb is
    // the winter (test: DateBetweenWrapsPastTheNewYear).
    public static bool InWindow(GameDate date, int fromMonth, int fromDay, int toMonth, int toDay)
    {
        long now = date.Month * 1000L + date.Day;
        long from = fromMonth * 1000L + fromDay;
        long to = toMonth * 1000L + toDay;
        return from <= to ? now >= from && now <= to : now >= from || now <= to;
    }

    // The 0-based weekday a name stands for (case does not matter); -1 when the calendar has no such day.
    public int WeekdayIndex(string name)
    {
        for (int i = 0; i < WeekdayCount; i++)
            if (string.Equals(WeekdayName(i), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static long FloorDiv(long a, long b) => (long)Math.Floor((double)a / b);
    private static int FloorMod(long a, long b) { long m = a % b; return (int)(m < 0 ? m + b : m); }
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class CalendarSeason
{
    [Property(Tooltip = "The season's name, as the `season` condition names it")]
    public string Name = "";
    [Property(Min = 1, Tooltip = "The month it starts in (1 is the first)")]
    public int Month = 1;
    [Property(Min = 1, Tooltip = "The day of that month it starts on")]
    public int Day = 1;
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class CalendarMonth
{
    [Property(Tooltip = "The month's name")]
    public string Name = "";
    [Property(Min = 1, Unit = "days", Tooltip = "How many days it has")]
    public int Days = 30;
}

// A day on the calendar. Month and Day count from 1; Weekday is an index into the calendar's names.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly record struct GameDate(int Year, int Month, int Day, int Weekday, string MonthName, string WeekdayName)
{
    public override string ToString() => $"{WeekdayName}, {Day} {MonthName} {Year}";
}

// `{ "weekday": { "is": "Monday" } }` or `{ "weekday": { "anyOf": ["Saturday", "Sunday"] } }`: today is one of
// those days of the week, named as the world's calendar names them (case does not matter). A name the
// calendar lacks is never today.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("weekday", Plugin = RegistrationOwners.Core)]
internal sealed class WeekdayCondition : ICondition
{
    [Property(Tooltip = "The day of the week it must be, as the calendar names it")]
    public string Is = "";
    [Property(Tooltip = "Or any of these days of the week")]
    public List<string> AnyOf = new();

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not this day of the week";
        var date = Calendars.Today(context.World, out var calendar);
        if (Is.Length > 0 && calendar.WeekdayIndex(Is) == date.Weekday) return true;
        foreach (var name in AnyOf)
            if (calendar.WeekdayIndex(name) == date.Weekday) return true;
        return false;
    }
}

// `{ "date_between": { "fromMonth": 12, "fromDay": 1, "toMonth": 2, "toDay": 28 } }`: today is in that
// window of the year, both ends included; it may wrap past the new year. Months and days count from 1.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("date_between", Plugin = RegistrationOwners.Core)]
internal sealed class DateBetweenCondition : ICondition
{
    [Property(Min = 1, Tooltip = "The month the window opens in (1 is the first month)")]
    public int FromMonth = 1;
    [Property(Min = 1, Tooltip = "The day of that month it opens on (inclusive)")]
    public int FromDay = 1;
    [Property(Min = 1, Tooltip = "The month it closes in")]
    public int ToMonth = 1;
    [Property(Min = 1, Tooltip = "The day of that month it closes on (inclusive); earlier than the opening wraps past the new year")]
    public int ToDay = 1;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not at this time of year";
        var date = Calendars.Today(context.World, out _);
        return CalendarRecord.InWindow(date, FromMonth, FromDay, ToMonth, ToDay);
    }
}

// `{ "season": { "is": "winter" } }` or `{ "season": { "anyOf": ["autumn", "winter"] } }`: it is that season by
// the world's calendar (names as the calendar gives them; case does not matter). A calendar with no seasons
// is never in one.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("season", Plugin = RegistrationOwners.Core)]
internal sealed class SeasonCondition : ICondition
{
    [Property(Tooltip = "The season it must be, as the calendar names it")]
    public string Is = "";
    [Property(Tooltip = "Or any of these seasons")]
    public List<string> AnyOf = new();

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not this season";
        var date = Calendars.Today(context.World, out var calendar);
        string season = calendar.SeasonOf(date);
        if (season.Length == 0) return false;
        if (Is.Length > 0 && string.Equals(Is, season, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var name in AnyOf)
            if (string.Equals(name, season, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

// `{ "moon_phase": { "is": "full" } }` or `{ "moon_phase": { "anyOf": ["new", "waning_crescent"] } }`: the moon is in
// that phase today. The phases are new, waxing_crescent, first_quarter, waxing_gibbous, full, waning_gibbous,
// last_quarter and waning_crescent.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("moon_phase", Plugin = RegistrationOwners.Core)]
internal sealed class MoonPhaseCondition : ICondition
{
    [Property(Tooltip = "The phase it must be: new, waxing_crescent, first_quarter, waxing_gibbous, full, waning_gibbous, last_quarter, waning_crescent")]
    public string Is = "";
    [Property(Tooltip = "Or any of these phases")]
    public List<string> AnyOf = new();

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not this phase of the moon";
        var calendar = Calendars.Of(context.World);
        int day = Calendars.DayOf(context.World);
        int phase = calendar.MoonPhaseOf(day);
        if (Is.Length > 0 && CalendarRecord.MoonPhaseIndex(Is) == phase) return true;
        foreach (var name in AnyOf)
            if (CalendarRecord.MoonPhaseIndex(name) == phase) return true;
        return false;
    }
}

// `{ "on_date": { "month": 12, "day": 25 } }`: today is that day of the year; with a `year` it is that one day
// only. Months and days count from 1.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Condition("on_date", Plugin = RegistrationOwners.Core)]
internal sealed class OnDateCondition : ICondition
{
    [Property(Min = 1, Tooltip = "The month (1 is the first)")]
    public int Month = 1;
    [Property(Min = 1, Tooltip = "The day of the month")]
    public int Day = 1;
    [Property(Tooltip = "The year; 0: every year")]
    public int Year;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not this date";
        var date = Calendars.Today(context.World);
        return date.Month == Month && date.Day == Day && (Year == 0 || date.Year == Year);
    }
}

// The world's calendar and today's date.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class Calendars
{
    // The calendar the world's clock names, or the default one when it names none (or a record that is gone).
    public static CalendarRecord Of(World world)
    {
        if (world.Resources.TryGet<WorldClock>(out var clock) && clock != null && !clock.Calendar.IsEmpty
            && world.Resources.TryGet<RecordStore>(out var records) && records != null
            && records.TryGet<CalendarRecord>(clock.Calendar, out var calendar) && calendar != null)
            return calendar;
        return CalendarRecord.Default;
    }

    // Today, by the world's clock and calendar. A world with no clock is on day 0.
    public static GameDate Today(World world, out CalendarRecord calendar)
    {
        calendar = Of(world);
        int day = world.Resources.TryGet<WorldClock>(out var clock) && clock != null ? clock.Day : 0;
        return calendar.DateOf(day);
    }

    public static GameDate Today(World world) => Today(world, out _);

    // The clock's day count; a world with no clock is on day 0.
    public static int DayOf(World world) =>
        world.Resources.TryGet<WorldClock>(out var clock) && clock != null ? clock.Day : 0;
}
