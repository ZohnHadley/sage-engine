#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Passing time (issue 4g-2, REDESIGN §5 phase 4g): resting, waiting for the shop to open, a journey.
//
//   Time.Pass(world, hours: 8, reason: "rest");
//
// **A skip is an event, not ticks.** The clock moves forward at once and the world raises one `TimePassed`;
// the simulation does not run hour by hour. What keeps a schedule (an NPC's routine, an off-screen fight,
// a crop) catches up from the event, by arithmetic, in the later 4g issues. The clock's `Scale` stays the
// rate it runs at while the game is played; a skip never goes through it
// (test: PassingTimeNeverTicksTheSimulation).
//
// **The request runs at a tick boundary**, like a save (SaveRequests): asked for during a tick, it runs
// when the world's fixed phases of that tick have all finished, so no system sees the hour change under it
// (test: TimePassesAtTheTickBoundary). Asked for with no tick running, it runs at once. Several requests in
// one tick are one skip: the hours add up and the one event carries the first reason
// (test: SeveralRequestsInATickAreOneSkip).
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class Time
{
    // The most one request may pass: a thousand years, far inside the day count's range.
    public const double MaxHours = 24.0 * 365.0 * 1000.0;

    // Asks for `hours` of game time to pass at the next tick boundary. False (and a warning) for a number
    // that is not a positive, finite amount, or a world with no clock.
    public static bool Pass(World world, double hours, string reason = "")
    {
        if (double.IsNaN(hours) || double.IsInfinity(hours) || hours <= 0 || hours > MaxHours)
        {
            Log.Warn(LogCat.World, $"Time.Pass: {hours} hours is not a time that can pass (0 to {MaxHours:0})");
            return false;
        }
        if (!world.Resources.TryGet<WorldClock>(out var clock) || clock == null)
        {
            Log.Warn(LogCat.World, $"Time.Pass: '{world.Name}' has no clock");
            return false;
        }

        if (world.PendingTimePass is { } pending)
        {
            pending.Hours = Math.Min(MaxHours, pending.Hours + hours);   // one skip for the tick
            return true;
        }
        world.PendingTimePass = new TimePassRequest { Hours = hours, Reason = reason };
        if (!world.InFixedTick) Run(world);
        return true;
    }

    // The tick boundary (World.RunFixed): does what was asked for.
    internal static void Run(World world)
    {
        var request = world.PendingTimePass;
        world.PendingTimePass = null;
        if (request == null || !world.Resources.TryGet<WorldClock>(out var clock) || clock == null) return;

        double from = clock.Elapsed;
        clock.Skip(request.Hours);
        world.Events.Send(new TimePassed(request.Hours, request.Reason, from, clock.Elapsed));
        Log.Info(LogCat.World, $"'{world.Name}': {request.Hours:0.##} h passed" +
                               (request.Reason.Length > 0 ? $" ({request.Reason})" : "") +
                               $", now day {clock.Day} {WorldClock.Format(clock.Hour)}");
    }
}

internal sealed class TimePassRequest
{
    public double Hours;
    public string Reason = "";
}

// Time was skipped (`Time.Pass`): `Hours` of game time went by in one step, from `FromElapsed` to
// `ToElapsed` (the clock's `Elapsed`, in game hours since day 0). Raised once per skip, at the tick
// boundary, so a reader sees it at the start of the next tick, and the clock it reads already says the new
// time. `Reason` is the caller's word for it ("rest", "travel", "wait"), for a rule to tell a sleep from a
// journey.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[GameEvent]
public readonly record struct TimePassed(double Hours, string Reason, double FromElapsed, double ToElapsed);
