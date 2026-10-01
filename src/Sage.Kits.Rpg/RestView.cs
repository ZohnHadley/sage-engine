#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using Sage.UI;

namespace Sage.Kits.Rpg;

// The rest and wait screen (issue 4g-7): how many hours, then sleep or wait them out — Daggerfall's
// "rest for how long?", Morrowind's slider. The rule is `Rest` (Rest.cs): this shows it and calls it.
//   screen rpg:rest — layout rpg:rest
//
// The hours are a slider made of what a layout has: a `bar` bound to `hours` out of `maxHours`, and `less`
// and `more` buttons either side of it (an hour each). `sleep` is enabled only when the rule allows it, and
// `refused` says why it does not (`@rpg.rest.enemies`) — read from `Rest.Can` every frame, so walking
// up to the screen with a wolf on your heels and the wolf dying while it is open both show. After a rest
// `message` says how long it was and the clock (`now`) moves on.
//
// Refresh allocates nothing once warm: the clock's words are made again only when the minute changes.
[Experimental(RpgKitModule.OpenWorld, UrlFormat = RpgKitModule.ExperimentalUrl)]
[ViewModel("rpg_rest")]
public sealed class RestView : IViewModel
{
    public const string LessButton = "less", MoreButton = "more", SleepButton = "sleep", WaitButton = "wait";

    // What the slider starts on: a night's sleep.
    public const int DefaultHours = 8;

    private long _minute = long.MinValue;
    private World? _world;
    private Query<Transform> _players;

    public int Hours { get; set; } = DefaultHours;

    public int MaxHours { get; private set; } = 24;

    // Whether sleeping is allowed now, and when not, why (a string-table key).
    public bool CanSleep { get; private set; }
    public string Refused { get; private set; } = "";

    public bool CanWait { get; private set; }

    // What the last rest did, or why it was refused.
    public string Message { get; private set; } = "";

    // The clock, as words: "day 3, 18:30".
    public string Now { get; private set; } = "";

    // Who rests: the screen's subject, else the first player.
    public Entity Subject { get; private set; }

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        Subject = Who(world, context.Subject);

        var conventions = RpgConventions.Of(world);
        MaxHours = Math.Max(1, conventions.RestMaxHours);
        Hours = Math.Clamp(Hours, 1, MaxHours);

        CanSleep = Rest.Can(world, Subject, RestKind.Sleep, Hours, out string why);
        Refused = CanSleep ? "" : why;
        CanWait = Rest.Can(world, Subject, RestKind.Wait, Hours, out _);

        if (world.Resources.TryGet<WorldClock>(out var clock) && clock != null)
        {
            long minute = (long)clock.Day * 1440 + (long)Math.Floor(clock.Hour * 60);
            if (minute != _minute)
            {
                _minute = minute;
                Now = RpgText.Of(world).Format("@rpg.rest.now", ("day", clock.Day), ("time", WorldClock.Format(clock.Hour)));
            }
        }
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { } world) return false;
        switch (widget.Name)
        {
            case LessButton: Hours = Math.Max(1, Hours - 1); return true;
            case MoreButton: Hours = Math.Min(MaxHours, Hours + 1); return true;
            case SleepButton: return Begin(world, Who(world, context.Subject), RestKind.Sleep);
            case WaitButton: return Begin(world, Who(world, context.Subject), RestKind.Wait);
            default: return false;
        }
    }

    // Rests the chosen hours, as the buttons do; Message says what happened.
    public bool Begin(World world, Entity who, RestKind kind)
    {
        var text = RpgText.Of(world);
        if (!Rest.Begin(world, who, kind, Hours, out string reason))
        {
            Message = text.Text(reason);
            return true;   // used: the screen shows why
        }
        Message = text.Format(kind == RestKind.Sleep ? "@rpg.rest.slept" : "@rpg.rest.waited", ("count", Hours), ("hours", Hours));
        return true;
    }

    private Entity Who(World world, Entity subject)
    {
        if (!subject.IsNull && world.IsAlive(subject)) return subject;
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
        }
        foreach (var entity in _players.Entities) return entity;
        return default;
    }
}
