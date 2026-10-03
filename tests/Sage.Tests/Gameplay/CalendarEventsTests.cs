#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Leap years, seasons, the moon and calendar events (issue 4m-15).
public class CalendarEventsTests
{
    public CalendarEventsTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private const string Data = """
    [
      { "type": "calendar", "id": "greg", "startYear": 1900, "leapEvery": 4, "leapSkipEvery": 100, "leapRestoreEvery": 400, "leapMonth": 2 },
      { "type": "calendar", "id": "reach", "startYear": 1, "startWeekday": 0,
        "weekdays": ["A", "B"],
        "months": [ { "name": "First", "days": 10 }, { "name": "Second", "days": 10 }, { "name": "Third", "days": 10 } ],
        "seasons": [ { "name": "Dry", "month": 1, "day": 1 }, { "name": "Wet", "month": 2, "day": 6 } ],
        "leapEvery": 3 },
      { "type": "calendar_event", "id": "harvest", "name": "Harvest Festival", "month": 9, "day": 21 },
      { "type": "calendar_event", "id": "leapday", "name": "Leap Day", "month": 2, "day": 29 },
      { "type": "calendar_event", "id": "fullmoon", "name": "Full Moon", "month": 0, "moon": "full" },
      { "type": "prefab", "id": "listener", "name": "listener", "parts": { "calendar_event": { "event": "sage:harvest" } } },
      { "type": "prefab", "id": "moonwatch", "name": "moonwatch", "parts": { "calendar_event": { "event": "sage:fullmoon" } } },
      { "type": "prefab", "id": "thing", "name": "thing" },
      { "type": "scene", "id": "fest",
        "place": [
          { "prefab": "listener", "at": [0, 0, 0], "name": "festival",
            "outputs": [ { "output": "OnCalendarEvent", "target": "gate", "input": "Record", "parameter": "harvest" } ] },
          { "prefab": "moonwatch", "at": [0, 0, 0], "name": "moon",
            "outputs": [ { "output": "OnCalendarEvent", "target": "gate", "input": "Record", "parameter": "moon" } ] },
          { "prefab": "thing", "at": [3, 0, 0], "name": "gate" }
        ] }
    ]
    """;

    private static HeadlessApp Boot(List<string>? arrivals = null)
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).File("data/cal.json", Data)
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) => arrivals?.Add(io.Parameter)))
            .StartScene("sage:fest").Boot("cal");
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        WorldClock.Of(app.World).Scale = 0;
        return app;
    }

    private static bool Holds(HeadlessApp app, string json) =>
        Conditions.Evaluate(app.World, default, JsonSerializer.Deserialize<ICondition>(json, app.Records.Json)!);

    private static void Tick(World world, int n = 1) { for (int i = 0; i < n; i++) world.RunFixed(Dt); }

    private static CalendarRecord Get(HeadlessApp app, string id) => app.Records.Get<CalendarRecord>(new RecordId("sage", id));

    [Fact]
    public void LeapYearsFollowTheRule()
    {
        using var app = Boot();
        var greg = Get(app, "greg");
        Assert.True(greg.IsLeapYear(2000));
        Assert.False(greg.IsLeapYear(1900));
        Assert.False(greg.IsLeapYear(2100));
        Assert.True(greg.IsLeapYear(2024));
        Assert.False(greg.IsLeapYear(2023));
        Assert.False(CalendarRecord.Default.IsLeapYear(2024));      // no leap rule, no leap years

        // Agrees with the real calendar over several centuries, before day 0 as well as after.
        var origin = new DateTime(1900, 1, 1);
        for (int day = -20000; day <= 120000; day += 13)
        {
            var d = greg.DateOf(day);
            var expected = origin.AddDays(day);
            Assert.Equal((expected.Year, expected.Month, expected.Day), (d.Year, d.Month, d.Day));
        }
        var leap = greg.DateOf((int)(new DateTime(2024, 2, 29) - origin).TotalDays);
        Assert.Equal((2024, 2, 29), (leap.Year, leap.Month, leap.Day));
        Assert.Equal(366, greg.DaysInYearOf(2024));
        Assert.Equal(365, greg.DaysInYearOf(2023));

        // A billion days is as cheap as three.
        var far = greg.DateOf(1_000_000_000);
        var expectedFar = far.Year;
        Assert.InRange(expectedFar, 2_737_000, 2_740_000);
    }

    [Fact]
    public void ACustomCalendarCanLeapInItsLastMonth()
    {
        using var app = Boot();
        var reach = Get(app, "reach");                              // 30-day years; every third year has 31 days
        Assert.True(reach.IsLeapYear(3));
        Assert.Equal(31, reach.DaysInYearOf(3));
        Assert.Equal(11, reach.DaysIn(3, 3));
        var d = reach.DateOf(60);                                   // years 1 and 2 are 30 days; year 3 starts on day 60
        Assert.Equal((3, 1, 1), (d.Year, d.Month, d.Day));
        d = reach.DateOf(60 + 30);                                  // the 31st day of year 3
        Assert.Equal((3, 3, 11), (d.Year, d.Month, d.Day));
        Assert.Equal((4, 1, 1), (reach.DateOf(91).Year, reach.DateOf(91).Month, reach.DateOf(91).Day));
    }

    [Fact]
    public void SeasonsRunToTheNextStart()
    {
        using var app = Boot();
        var clock = WorldClock.Of(app.World);
        // The default calendar: the four usual seasons.
        clock.Day = 0;                                              // 1 January
        Assert.True(Holds(app, """{ "season": { "is": "winter" } }"""));
        clock.Day = 31 + 28;                                        // 1 March
        Assert.True(Holds(app, """{ "season": { "is": "Spring" } }"""));
        clock.Day = 31 + 28 + 31 + 30 + 31 + 30;                    // 1 July
        Assert.True(Holds(app, """{ "season": { "anyOf": ["autumn", "summer"] } }"""));
        Assert.False(Holds(app, """{ "season": { "is": "winter" } }"""));

        // A custom calendar has the seasons it lists, and the last one wraps the new year.
        clock.Calendar = new RecordId("sage", "reach");
        clock.Day = 12;                                             // 3 of the second month
        Assert.True(Holds(app, """{ "season": { "is": "dry" } }"""));
        clock.Day = 15;                                             // 6 of the second month
        Assert.True(Holds(app, """{ "season": { "is": "wet" } }"""));
        clock.Day = 0;
        Assert.True(Holds(app, """{ "season": { "is": "dry" } }"""));

        // One with none is never in a season.
        clock.Calendar = new RecordId("sage", "greg");
        Assert.True(Holds(app, """{ "season": { "is": "winter" } }"""));   // 1 Jan 1900: the default months, so the default seasons
    }

    [Fact]
    public void TheMoonCyclesThroughEightPhases()
    {
        using var app = Boot();
        var cal = CalendarRecord.Default;
        Assert.Equal("new", CalendarRecord.MoonPhaseNames[cal.MoonPhaseOf(0)]);
        Assert.Equal("first_quarter", CalendarRecord.MoonPhaseNames[cal.MoonPhaseOf(7)]);
        Assert.Equal("full", CalendarRecord.MoonPhaseNames[cal.MoonPhaseOf(15)]);
        Assert.Equal("last_quarter", CalendarRecord.MoonPhaseNames[cal.MoonPhaseOf(22)]);
        Assert.Equal("new", CalendarRecord.MoonPhaseNames[cal.MoonPhaseOf(29)]);
        Assert.InRange(cal.MoonLight(15), 0.95, 1.0);
        Assert.InRange(cal.MoonLight(0), 0.0, 0.01);
        Assert.InRange(cal.MoonPhaseOf(-3), 0, 7);                  // before day 0 still has one

        var clock = WorldClock.Of(app.World);
        clock.Day = 15;
        Assert.True(Holds(app, """{ "moon_phase": { "is": "full" } }"""));
        Assert.True(Holds(app, """{ "moon_phase": { "anyOf": ["new", "full"] } }"""));
        Assert.False(Holds(app, """{ "moon_phase": { "is": "new" } }"""));
        clock.Day = 0;
        Assert.True(Holds(app, """{ "moon_phase": { "is": "NEW" } }"""));
    }

    [Fact]
    public void OnDateMatchesADayOfTheYear_AndALeapDayOnlyInLeapYears()
    {
        using var app = Boot();
        var clock = WorldClock.Of(app.World);
        clock.Calendar = new RecordId("sage", "greg");
        clock.Day = (int)(new DateTime(2024, 2, 29) - new DateTime(1900, 1, 1)).TotalDays;
        Assert.True(Holds(app, """{ "on_date": { "month": 2, "day": 29 } }"""));
        Assert.True(Holds(app, """{ "on_date": { "month": 2, "day": 29, "year": 2024 } }"""));
        Assert.False(Holds(app, """{ "on_date": { "month": 2, "day": 29, "year": 2020 } }"""));
        clock.Day = (int)(new DateTime(2023, 3, 1) - new DateTime(1900, 1, 1)).TotalDays - 1;   // the last day of February, 2023
        Assert.False(Holds(app, """{ "on_date": { "month": 2, "day": 29 } }"""));
        Assert.True(Holds(app, """{ "on_date": { "month": 2, "day": 28 } }"""));
    }

    // The Done criterion: a record defines a festival, a wire fires on its day.
    [Fact]
    public void AFestivalFiresItsWireOnItsDay()
    {
        var arrivals = new List<string>();
        using var app = Boot(arrivals);
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Calendar = new RecordId("sage", "greg");
        int harvest = (int)(new DateTime(1901, 9, 21) - new DateTime(1900, 1, 1)).TotalDays;

        clock.Day = harvest - 1;
        Tick(world, 3);
        Assert.DoesNotContain("harvest", arrivals);

        clock.Day = harvest;
        Tick(world, 3);
        Assert.Equal(1, arrivals.Count(a => a == "harvest"));       // once, however many ticks the day lasts
        Tick(world, 30);
        Assert.Equal(1, arrivals.Count(a => a == "harvest"));

        clock.Day = harvest + 1;
        Tick(world, 3);
        Assert.Equal(1, arrivals.Count(a => a == "harvest"));

        clock.Day = harvest + 365;                                  // next year
        Tick(world, 3);
        Assert.Equal(2, arrivals.Count(a => a == "harvest"));
    }

    [Fact]
    public void ASkippedFestivalStillFires()
    {
        var arrivals = new List<string>();
        using var app = Boot(arrivals);
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Calendar = new RecordId("sage", "greg");
        int harvest = (int)(new DateTime(1901, 9, 21) - new DateTime(1900, 1, 1)).TotalDays;
        clock.Day = harvest - 5;
        Tick(world, 2);

        Assert.True(Time.Pass(world, 24 * 10));                     // ten days of rest, over the festival
        Tick(world, 3);
        Assert.Equal(1, arrivals.Count(a => a == "harvest"));
        Assert.Equal(harvest + 5, clock.Day);
    }

    [Fact]
    public void AMoonEventFiresOnEveryFullMoon()
    {
        var arrivals = new List<string>();
        using var app = Boot(arrivals);
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Day = 0; Tick(world, 2);
        for (int day = 1; day <= 60; day++) { clock.Day = day; Tick(world, 2); }
                // The first day of each run of full-moon days: two cycles, two firings, not one a day.
        Assert.Equal(2, arrivals.Count(a => a == "moon"));
    }

    [Fact]
    public void TheListenersLastDayIsSaved_SoALoadDoesNotFireTheDayTwice()
    {
        var arrivals = new List<string>();
        using var app = Boot(arrivals);
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Calendar = new RecordId("sage", "greg");
        int harvest = (int)(new DateTime(1901, 9, 21) - new DateTime(1900, 1, 1)).TotalDays;
        clock.Day = harvest;
        Tick(world, 3);
        Assert.Equal(1, arrivals.Count(a => a == "harvest"));
        Assert.True(app.Engine.Saves.Save("fest"));

        Assert.True(app.Engine.Saves.Load("fest"));
        Tick(world, 5);
        Assert.Equal(1, arrivals.Count(a => a == "harvest"));
        Assert.Equal(harvest, WorldClock.Of(world).Day);
    }
}
