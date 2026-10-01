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

    // Days in month 1..MonthCount; a month written with fewer than one day counts as one, so a bad record
    // cannot make the walk loop or divide by zero.
    public int DaysIn(int month) =>
        Months.Count == 0 ? DefaultMonthDays[month - 1] : Math.Max(1, Months[month - 1].Days);

    public int DaysInYear
    {
        get
        {
            int total = 0;
            for (int m = 1; m <= MonthCount; m++) total += DaysIn(m);
            return total;
        }
    }

    // The weekday (0-based) of a day count; floor arithmetic, so a day before day 0 still has one.
    public int WeekdayOf(int day) => FloorMod(StartWeekday + (long)day, WeekdayCount);

    // The date of a day count (the clock's `Day`), however far it is from day 0.
    public GameDate DateOf(int day)
    {
        int yearLength = DaysInYear;
        int year = StartYear + (int)FloorDiv(day, yearLength);
        int rest = FloorMod(day, yearLength);
        int month = 1;
        while (month < MonthCount && rest >= DaysIn(month)) { rest -= DaysIn(month); month++; }
        int weekday = WeekdayOf(day);
        return new GameDate(year, month, rest + 1, weekday, MonthName(month), WeekdayName(weekday));
    }

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
}
