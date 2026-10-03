#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Calendar events (issue 4m-15): a festival, a holy day, a full moon, written once as a record and heard by a
// wire on the day it falls.
//
//   record   { "type": "calendar_event", "id": "sage:harvest_fest", "name": "Harvest Festival", "month": 9, "day": 21 }
//   entity   prefab part "calendar_event": { "event": "sage:harvest_fest" }     (component sage:calendar_event)
//   wire     OnCalendarEvent  ->  the entity's own outputs: "OnCalendarEvent" -> "market_gate" "Open"
//
// An event falls on a day when its `month` and `day` match the date (`year` 0 matches every year; a leap
// day, 29 February, falls only in leap years) and, if it names a `moon` phase, the moon is in it; with `month` 0 and
// a `moon` it is the first day of each such phase ("every full moon"). The date is read by the world's calendar.
//
// **Once a day.** The listener remembers the last day it looked at (`LastDay`, saved), so a loaded game
// does not fire the day twice. A skip (`Time.Pass`, resting) is looked through day by day: an event that fell
// in the days skipped fires once, now, whatever the number of days it fell on
// (test: ASkippedFestivalStillFires). A listener new to the world fires if today is the day
// (test: AFestivalFiresItsWireOnItsDay).
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Record("calendar_event", Plugin = RegistrationOwners.Core)]
public sealed class CalendarEventRecord
{
    [Property(Tooltip = "What the event is called, for a message or a HUD")]
    public string Name = "";

    [Property(Min = 0, Tooltip = "The month it falls in (1 is the first); 0: any month, which needs a moon phase")]
    public int Month;

    [Property(Min = 1, Tooltip = "The day of that month")]
    public int Day = 1;

    [Property(Tooltip = "The one year it falls in; 0: every year")]
    public int Year;

    [Property(Tooltip = "Only when the moon is in this phase (new, waxing_crescent, first_quarter, waxing_gibbous, full, waning_gibbous, last_quarter, waning_crescent); empty: any")]
    public string Moon = "";

    // Does this event fall on `day` of the clock, by `calendar`?
    public bool FallsOn(CalendarRecord calendar, int day)
    {
        if (Moon.Length > 0 && CalendarRecord.MoonPhaseIndex(Moon) != calendar.MoonPhaseOf(day)) return false;
        // A moon event with no date is the first day of the phase, not each of its three or four days.
        if (Month == 0)
            return Moon.Length > 0 && calendar.MoonPhaseOf(day - 1) != calendar.MoonPhaseOf(day)
                   && (Year == 0 || calendar.DateOf(day).Year == Year);
        var date = calendar.DateOf(day);
        return date.Month == Month && date.Day == Day && (Year == 0 || date.Year == Year);
    }
}

// An entity that fires OnCalendarEvent on the day its event falls. Saved with the day it last looked at.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Component("sage:calendar_event")]
public struct CalendarEventListener : IComponent
{
    [RecordRef("calendar_event"), Property(Tooltip = "The calendar_event record it listens for")]
    public RecordId Event;
    [Property(Tooltip = "The last day (the clock's day count) it looked at; the event fires when a day after it is the event's")]
    public int LastDay;
    [Property(Tooltip = "Whether LastDay means anything yet; off until its first look")]
    public bool Seen;
}

// "calendar_event": { "event": "sage:harvest_fest" }
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[PrefabPart("calendar_event", Plugin = RegistrationOwners.Core)]
public sealed class CalendarEventPart : IPrefabPart
{
    [RecordRef("calendar_event"), Property(Tooltip = "The calendar_event record to fire OnCalendarEvent for")]
    public RecordId Event;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Event.IsEmpty) { ctx.Warn("calendar_event: no event is named; it will never fire"); return; }
        ctx.World.Add(ctx.Entity, new CalendarEventListener { Event = Event });
    }
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class CalendarEvents
{
    public const string OnCalendarEvent = "OnCalendarEvent";

    // The most days a skip is looked through; further back than this, the last of them are.
    internal const int MaxLookback = 4000;

    internal static void Register(Engine engine) =>
        engine.Outputs.Declare(OnCalendarEvent, "The calendar event this entity listens for (sage:calendar_event) has fallen: today is its day.");
}

// EntityIO phase, ahead of the dispatch like the timers: wires with no delay arrive the same tick. Costs one
// query and nothing at all on a tick that is the same day as the last.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[System(Id, Phase.EntityIO, Before = new[] { "?sage.io.dispatch" })]
internal sealed class CalendarEventSystem : ISystem
{
    public const string Id = "sage.world.calendar_events";

    private readonly World _world;
    private readonly Query<CalendarEventListener> _listeners;

    public CalendarEventSystem(World world)
    {
        _world = world;
        _listeners = world.Query<CalendarEventListener>();
    }

    public void Run(in SystemContext ctx)
    {
        if (!_world.Resources.TryGet<RecordStore>(out var records) || records == null) return;
        int today = Calendars.DayOf(_world);
        CalendarRecord? calendar = null;
        foreach (var (listeners, entities) in _listeners.Chunks)
        {
            var l = listeners.Span;
            for (int i = 0; i < l.Length; i++)
            {
                ref var listener = ref l[i];
                if (listener.Seen && listener.LastDay == today) continue;
                int from = listener.Seen ? listener.LastDay + 1 : today;
                if (from > today) from = today;                       // the clock was set back: look at today only
                from = Math.Max(from, today - CalendarEvents.MaxLookback);
                listener.Seen = true;
                listener.LastDay = today;
                if (!records.TryGet<CalendarEventRecord>(listener.Event, out var record) || record == null) continue;
                calendar ??= Calendars.Of(_world);
                for (int day = from; day <= today; day++)
                {
                    if (!record.FallsOn(calendar, day)) continue;
                    _world.FireOutput(entities.EntityAt(i), CalendarEvents.OnCalendarEvent);
                    break;
                }
            }
        }
    }
}
