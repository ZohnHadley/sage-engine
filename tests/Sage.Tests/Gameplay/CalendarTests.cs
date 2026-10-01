#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The calendar and passing time (issue 4g-2, REDESIGN §5 phase 4g).
//
// A calendar is a record that says what the clock's day count means; a skip is a request that runs at a
// tick boundary and raises one event. Both are arithmetic on a headless world.
public class CalendarTests
{
    public CalendarTests() { _ = TestEnv.UserRoot; }

    // Three months of 10, 5 and 7 days (a 22-day year) and a five-day week, day 0 on its third day.
    private const string Records = """
        [{ "type": "calendar", "id": "reach", "startYear": 412, "startWeekday": 2,
           "weekdays": ["Sundas", "Morndas", "Tirdas", "Middas", "Turdas"],
           "months": [ { "name": "Morning Star", "days": 10 }, { "name": "Sun's Dawn", "days": 5 }, { "name": "Last Seed", "days": 7 } ] }]
        """;

    private static HeadlessApp Boot()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay().File("data/calendar.json", Records).Boot("calendar");
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static ICondition Condition(HeadlessApp app, string json) =>
        JsonSerializer.Deserialize<ICondition>(json, app.Records.Json)!;

    private static bool Holds(HeadlessApp app, string json) => Conditions.Evaluate(app.World, default, Condition(app, json));

    // ---- the date ----

    [Fact]
    public void TheDefaultCalendarHasMonday()
    {
        using var app = Boot();
        var clock = WorldClock.Of(app.World);

        var date = clock.Date();
        Assert.Equal((1, 1, 1), (date.Year, date.Month, date.Day));
        Assert.Equal("Monday", date.WeekdayName);
        Assert.Equal("January", date.MonthName);

        clock.Day = 31;                                     // the first of February
        Assert.Equal((1, 2, 1), (clock.Date().Year, clock.Date().Month, clock.Date().Day));
        Assert.Equal("Thursday", clock.Date().WeekdayName);
        clock.Day = 365;                                    // the year is 365 days: the first of the next
        Assert.Equal((2, 1, 1), (clock.Date().Year, clock.Date().Month, clock.Date().Day));
        Assert.Equal("Tuesday", clock.Date().WeekdayName);
    }

    [Fact]
    public void WeekdayReadsMonday()
    {
        using var app = Boot();
        var clock = WorldClock.Of(app.World);

        clock.Day = 0;
        Assert.True(Holds(app, """{ "weekday": { "is": "Monday" } }"""));
        Assert.True(Holds(app, """{ "weekday": { "is": "monday" } }"""));          // case does not matter
        Assert.False(Holds(app, """{ "weekday": { "is": "Tuesday" } }"""));
        Assert.False(Holds(app, """{ "weekday": { "is": "Sundas" } }"""));         // not this calendar's name

        clock.Day = 5;                                                              // Saturday
        Assert.True(Holds(app, """{ "weekday": { "anyOf": ["Saturday", "Sunday"] } }"""));
        Assert.False(Holds(app, """{ "weekday": { "anyOf": ["Monday", "Friday"] } }"""));
        clock.Day = 7;                                                              // a week on: Monday again
        Assert.True(Holds(app, """{ "weekday": { "is": "Monday" } }"""));
    }

    [Fact]
    public void MonthsAndYearsWrapInACustomCalendar()
    {
        using var app = Boot();
        var calendar = app.Records.Get<CalendarRecord>(new RecordId("sage", "reach"));
        Assert.Equal(22, calendar.DaysInYear);

        void Is(int day, int year, int month, int dayOfMonth, string weekday)
        {
            var d = calendar.DateOf(day);
            Assert.Equal((year, month, dayOfMonth, weekday), (d.Year, d.Month, d.Day, d.WeekdayName));
        }
        Is(0, 412, 1, 1, "Tirdas");
        Is(9, 412, 1, 10, "Morndas");        // the last day of the first month
        Is(10, 412, 2, 1, "Tirdas");         // and the first of the next
        Is(14, 412, 2, 5, "Morndas");
        Is(15, 412, 3, 1, "Tirdas");
        Is(21, 412, 3, 7, "Middas");         // the last day of the year
        Is(22, 413, 1, 1, "Turdas");        // wraps into the next year
        Is(22 * 5 + 11, 417, 2, 2, "Middas");
        Is(-1, 411, 3, 7, "Morndas");        // before day 0 still has a date
        Is(-22, 411, 1, 1, "Sundas");

        Assert.Equal("Tirdas, 1 Morning Star 412", calendar.DateOf(0).ToString());

        // The world's own calendar, chosen on the clock.
        var clock = WorldClock.Of(app.World);
        clock.Calendar = new RecordId("sage", "reach");
        clock.Day = 15;
        Assert.Equal((412, 3, 1), (Calendars.Today(app.World).Year, Calendars.Today(app.World).Month, Calendars.Today(app.World).Day));
        Assert.True(Holds(app, """{ "weekday": { "is": "Tirdas" } }"""));
        clock.Calendar = new RecordId("sage", "gone");           // a record that is not there: the default
        Assert.Equal(1, Calendars.Today(app.World).Year);
    }

    [Fact]
    public void DateBetweenWrapsPastTheNewYear()
    {
        using var app = Boot();
        var clock = WorldClock.Of(app.World);
        const string winter = """{ "date_between": { "fromMonth": 12, "fromDay": 1, "toMonth": 2, "toDay": 28 } }""";
        const string spring = """{ "date_between": { "fromMonth": 3, "fromDay": 21, "toMonth": 6, "toDay": 20 } }""";

        clock.Day = 0;                                              // 1 January
        Assert.True(Holds(app, winter));
        Assert.False(Holds(app, spring));
        clock.Day = 31 + 27;                                        // 28 February: the last day, included
        Assert.True(Holds(app, winter));
        clock.Day = 31 + 28;                                        // 1 March
        Assert.False(Holds(app, winter));
        clock.Day = 334;                                            // 1 December: the first day, included
        Assert.True(Holds(app, winter));
        clock.Day = 333;
        Assert.False(Holds(app, winter));
        clock.Day = 31 + 28 + 20;                                   // 21 March
        Assert.True(Holds(app, spring));
        clock.Day = 365 + 31 + 28 + 20;                             // and a year on
        Assert.True(Holds(app, spring));

        clock.Day = 0;
        Assert.True(Holds(app, """{ "date_between": { "fromMonth": 1, "fromDay": 1, "toMonth": 1, "toDay": 1 } }"""));   // one day
        Assert.False(Holds(app, """{ "date_between": { "fromMonth": 1, "fromDay": 2, "toMonth": 1, "toDay": 2 } }"""));
    }

    // ---- passing time ----

    private sealed class CountEvents : ISystem
    {
        public int Ticks;
        public readonly List<TimePassed> Seen = new();
        private readonly EventReader<TimePassed> _reader;

        public CountEvents(World world)
        {
            _reader = world.Events.Reader<TimePassed>(this, Schedule.Fixed);
            world.AddSystem(this, Phase.Gameplay);
        }

        public void Run(in SystemContext ctx)
        {
            Ticks++;
            foreach (ref readonly var e in _reader.Read()) Seen.Add(e);
        }
    }

    [Fact]
    public void TimePass30MovesToTheNextDayAndFiresOnce()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        var watch = new CountEvents(world);
        clock.Scale = 0;                                            // only the skip moves it
        clock.Day = 3; clock.Hour = 12;

        app.CVars.Execute("time_pass 30");
        Assert.Equal(12, clock.Hour);                               // a cheat: refused until cheats are on
        app.CVars.Execute("sv_cheats 1");
        app.CVars.Execute("time_pass 30");
        Assert.Equal(4, clock.Day);                                 // day 3 at noon plus 30 h: day 4, 18:00
        Assert.Equal(18, clock.Hour, 6);

        for (int i = 0; i < 5; i++) world.RunFixed(1f / 60f);
        var seen = Assert.Single(watch.Seen);
        Assert.Equal(30, seen.Hours);
        Assert.Equal("console", seen.Reason);
        Assert.Equal(3 * 24 + 12, seen.FromElapsed, 6);
        Assert.Equal(4 * 24 + 18, seen.ToElapsed, 6);
        Assert.Equal(4, clock.Day);                                 // and the ticks that followed did not move it

        app.CVars.Execute("time_pass nonsense");
        app.CVars.Execute("time_pass -2");
        for (int i = 0; i < 3; i++) world.RunFixed(1f / 60f);
        Assert.Single(watch.Seen);                                  // refused, not applied
        Assert.Equal(18, clock.Hour, 6);
    }

    // A system that asks while the tick is running sees the clock as it was; the skip lands when the tick's
    // phases have all run.
    private sealed class AskDuringTick : ISystem
    {
        public double HourAfterAsking = -1;
        public double HourInLaterPhase = -1;
        public bool Asked;

        public void Run(in SystemContext ctx)
        {
            if (Asked) return;
            Asked = true;
            Time.Pass(ctx.World, 5, "test");
            HourAfterAsking = WorldClock.Of(ctx.World).Hour;
        }
    }

    private sealed class LaterPhase : ISystem
    {
        public double Hour = -1;
        public void Run(in SystemContext ctx) => Hour = WorldClock.Of(ctx.World).Hour;
    }

    [Fact]
    public void TimePassesAtTheTickBoundary()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Scale = 0; clock.Hour = 20;
        var watch = new CountEvents(world);
        var asker = new AskDuringTick();
        var later = new LaterPhase();
        world.AddSystem(asker, Phase.Commands);
        world.AddSystem(later, Phase.Late);

        world.RunFixed(1f / 60f);
        Assert.Equal(20, asker.HourAfterAsking);                    // asking changed nothing
        Assert.Equal(20, later.Hour);                               // not even for a system later in the same tick
        Assert.Equal(1, clock.Day);                                 // 20:00 plus 5 h: past midnight
        Assert.Equal(1, clock.Hour, 6);
        Assert.Empty(watch.Seen);                                   // readers see the event next tick

        world.RunFixed(1f / 60f);
        Assert.Single(watch.Seen);
        Assert.Equal(1, later.Hour, 6);
    }

    [Fact]
    public void SeveralRequestsInATickAreOneSkip()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Scale = 0; clock.Hour = 6;
        var watch = new CountEvents(world);
        world.AddSystem(new AskTwice(), Phase.Commands);

        world.RunFixed(1f / 60f);
        world.RunFixed(1f / 60f);
        var seen = Assert.Single(watch.Seen);
        Assert.Equal(5, seen.Hours);                                // 2 + 3
        Assert.Equal("rest", seen.Reason);                          // the first reason
        Assert.Equal(11, clock.Hour, 6);

        Assert.False(Time.Pass(world, 0));                          // not a time that can pass
        Assert.False(Time.Pass(world, double.NaN));
        Assert.False(Time.Pass(world, -1));
        world.RunFixed(1f / 60f);
        Assert.Single(watch.Seen);
    }

    private sealed class AskTwice : ISystem
    {
        private bool _done;
        public void Run(in SystemContext ctx)
        {
            if (_done) return;
            _done = true;
            Time.Pass(ctx.World, 2, "rest");
            Time.Pass(ctx.World, 3, "wait");
        }
    }

    // Asked with no tick running, it happens at once. And a skip is not ticks: a day passes and the
    // simulation has run no more ticks than the ones the test ran.
    [Fact]
    public void PassingTimeNeverTicksTheSimulation()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Scale = 60;                                           // the render rate, which a skip does not go through
        clock.Hour = 9;
        var watch = new CountEvents(world);

        Assert.True(Time.Pass(world, 24 * 10, "journey"));
        Assert.Equal(10, clock.Day);                                // already: no tick was needed
        Assert.Equal(9, clock.Hour, 6);
        Assert.Equal(0, watch.Ticks);                               // and nothing ran

        world.RunFixed(1f / 60f);
        Assert.Equal(1, watch.Ticks);
        Assert.Single(watch.Seen);
        Assert.Equal(10, clock.Day);
    }

    // ---- saving ----

    [Fact]
    public void TheCalendarSurvivesASave()
    {
        using var app = Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var clock = WorldClock.Of(app.World);
        clock.Calendar = new RecordId("sage", "reach"); clock.Day = 15;

        Assert.True(app.Engine.Saves.Save("cal"));
        clock.Calendar = default; clock.Day = 0;
        Assert.True(app.Engine.Saves.Load("cal"));

        clock = WorldClock.Of(app.World);
        Assert.Equal(new RecordId("sage", "reach"), clock.Calendar);
        Assert.Equal(15, clock.Day);
        Assert.Equal("Tirdas", Calendars.Today(app.World).WeekdayName);
    }

    // A save from before the calendar has a clock with no `Calendar` in it. It loads, with the default
    // calendar, and the day count it had.
    [Fact]
    public void AClockSavedBeforeTheCalendarStillLoads()
    {
        using var app = Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var clock = WorldClock.Of(app.World);
        clock.Day = 2; clock.Hour = 18.5; clock.Calendar = new RecordId("sage", "reach");
        Assert.True(app.Engine.Saves.Save("old"));

        string file = Path.Combine(app.Engine.Saves.Root, "old", "world_calendar.json");
        var doc = JsonNode.Parse(File.ReadAllText(file))!;
        var data = doc["resources"]!["clock"]!["data"]!.AsObject();
        Assert.True(data.ContainsKey("Calendar"));
        data.Remove("Calendar");
        File.WriteAllText(file, doc.ToJsonString());

        clock.Calendar = default; clock.Day = 9; clock.Hour = 3;
        using var log = new CaptureSink();
        Assert.True(app.Engine.Saves.Load("old"));
        // the sink hears the whole process (other tests log a bad calendar too), so look only at what this
        // load, on this thread, could have warned about
        int thread = Environment.CurrentManagedThreadId;
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warn && e.ThreadId == thread
            && (e.Message.Contains("clock", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("calendar", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("world_calendar")));

        var loaded = WorldClock.Of(app.World);
        Assert.Equal(2, loaded.Day);
        Assert.Equal(18.5, loaded.Hour, 6);
        Assert.True(loaded.Calendar.IsEmpty);
        Assert.Equal("Wednesday", loaded.Date().WeekdayName);        // by the default calendar
    }
}
