#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sage.Simulation;

// `wait` (issue #275): a `then` that happens over time, written in data.
//
//   "then": [ { "message": "The floor shakes." }, { "wait": 2 }, { "fire": "gate", "input": "Open" },
//             { "wait": 0.5 }, { "play_sound": "gate_slam", "at": "gate" } ]
//
// **What it means.** A `wait` stops the list where it is, and the rest of the list runs `seconds` later,
// with the same subject and other, through the same runner (Conditions.Run), so a later `wait` waits again.
// Whatever ran the list (a relay's Trigger, a state's `enter`, a dialogue option, a topic) has finished
// with it by then: a relay has fired OnTrigger, a state machine may be in another state. A list that ends
// in a `wait` waits for nothing. A `wait` outside a list (a single action) does nothing.
//
// **When.** Like a timer (Timers.cs): a wait of N seconds begun on tick D runs the rest on the tick an
// input sent on tick D with a delay of N arrives — counted in the EntityIO phase before the dispatch, so
// a `fire` with no delay in the rest arrives the tick after, as a wire's would. A paused world's waits
// stand still. A `wait` of 0 runs the rest on the next tick.
//
// **Saved.** What is still to run is the `sequences` resource: for each, the seconds left, the subject and
// the other (by persistent id; an entity without one comes back as nobody), and the rest of the list as
// content writes it (the record store's JSON dialect). A load reads the list back with the content loaded
// then, so a sequence saved half-way finishes after a load (test: AWaitSavedHalfWayFinishesAfterALoad); a
// word the content no longer has drops that sequence with a warning.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#275)
[Action("wait", Plugin = RegistrationOwners.Core)]
internal sealed class WaitAction : IAction
{
    [EntryValue, Property(Min = 0, Unit = "s", Tooltip = "Seconds before the rest of the list runs")]
    public float Seconds;

    // Conditions.Run reads a wait itself: one run on its own has nothing after it to put off.
    public void Run(in ActionContext context) { }
}

// The rest of every list a `wait` put off, by world. Made the first time a world waits (and by a load).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#275)
[SavedResource("sequences", Plugin = RegistrationOwners.Core)]
internal sealed class LogicSequences : ISavedResource
{
    private struct Waiting
    {
        public float Remaining;
        public long Queued;                       // the tick it began: not counted down in that tick
        public IReadOnlyList<IAction> Actions;
        public int Next;                          // where in Actions the rest starts
        public Entity Subject;
        public Entity Other;
    }

    private readonly List<Waiting> _waiting = new();
    private readonly List<Waiting> _due = new();
    private World? _world;

    [Transient] public int Count => _waiting.Count;

    public static LogicSequences Of(World world)
    {
        var sequences = world.Resources.GetOrAdd(static () => new LogicSequences());
        sequences._world ??= world;
        return sequences;
    }

    internal void Defer(in ActionContext context, IReadOnlyList<IAction> actions, int next, float seconds)
    {
        _world ??= context.World;
        _waiting.Add(new Waiting
        {
            Remaining = seconds >= 0f && float.IsFinite(seconds) ? seconds : 0f,
            Queued = context.World.Tick,
            Actions = actions,
            Next = next,
            Subject = context.Subject,
            Other = context.Other,
        });
    }

    // Once a tick (LogicSequenceSystem): counts every wait down and runs the rest of those that are over,
    // in the order they began. What those put off begins this tick, so it is not counted until the next.
    internal void Run(World world, float dt)
    {
        _world ??= world;
        if (_waiting.Count == 0) return;
        long tick = world.Tick;
        _due.Clear();
        int keep = 0;
        for (int i = 0; i < _waiting.Count; i++)
        {
            var waiting = _waiting[i];
            if (waiting.Queued != tick)
            {
                waiting.Remaining -= dt;
                if (waiting.Remaining <= Timers.Epsilon)
                {
                    _due.Add(waiting);
                    continue;
                }
            }
            _waiting[keep++] = waiting;
        }
        _waiting.RemoveRange(keep, _waiting.Count - keep);

        for (int i = 0; i < _due.Count; i++)
        {
            var due = _due[i];
            var subject = world.IsAlive(due.Subject) ? due.Subject : default;
            var other = world.IsAlive(due.Other) ? due.Other : default;
            Conditions.RunFrom(due.Actions, due.Next, new ActionContext(world, subject, other));
        }
        _due.Clear();
    }

    // ---- Saving ---------------------------------------------------------------------------------------

    // One sequence still waiting, as a save writes it.
    internal sealed class SavedSequence
    {
        public double Remaining;
        public Entity Subject;
        public Entity Other;
        public JsonArray? Then;
    }

    private List<SavedSequence>? _loaded;

    [JsonInclude]
    internal List<SavedSequence> Pending
    {
        get
        {
            var list = new List<SavedSequence>(_waiting.Count);
            if (_waiting.Count == 0 || _world is not { } world) return list;
            if (!world.Resources.TryGet<RecordStore>(out var records) || records == null) return list;
            foreach (var waiting in _waiting)
            {
                var rest = new List<IAction>(waiting.Actions.Count - waiting.Next);
                for (int i = waiting.Next; i < waiting.Actions.Count; i++)
                    if (waiting.Actions[i] is { } action) rest.Add(action);
                list.Add(new SavedSequence
                {
                    Remaining = Math.Max(0f, waiting.Remaining),
                    Subject = world.IsAlive(waiting.Subject) ? waiting.Subject : default,
                    Other = world.IsAlive(waiting.Other) ? waiting.Other : default,
                    Then = JsonSerializer.SerializeToNode(rest, records.Json) as JsonArray,
                });
            }
            return list;
        }
        set => _loaded = value;
    }

    public void AfterLoad(World world)
    {
        _world = world;
        _waiting.Clear();
        if (_loaded == null) return;
        world.Resources.TryGet<RecordStore>(out var records);
        foreach (var saved in _loaded)
        {
            if (saved?.Then == null || saved.Then.Count == 0) continue;
            List<IAction>? actions = null;
            if (records != null)
            {
                try { actions = saved.Then.Deserialize<List<IAction>>(records.Json); }
                catch (JsonException ex)
                {
                    Log.Warn(LogCat.Save, $"sequences: a `then` waiting {saved.Remaining:0.##} s no longer reads ({ex.Message}); it is dropped");
                }
            }
            if (actions == null) continue;
            _waiting.Add(new Waiting
            {
                Remaining = (float)Math.Max(0.0, saved.Remaining),
                Queued = -1,
                Actions = actions,
                Next = 0,
                Subject = saved.Subject,
                Other = saved.Other,
            });
        }
        _loaded = null;
    }
}

// EntityIO phase, after state machines and before the dispatch (see LogicSequences for why).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#275)
[System(Id, Phase.EntityIO, After = new[] { StateMachineSystem.Id }, Before = new[] { "?sage.io.dispatch" })]
internal sealed class LogicSequenceSystem : ISystem
{
    public const string Id = "sage.logic.sequences";

    private readonly World _world;

    public LogicSequenceSystem(World world) => _world = world;

    public void Run(in SystemContext ctx)
    {
        if (_world.Resources.TryGet<LogicSequences>(out var sequences) && sequences != null)
            sequences.Run(_world, ctx.Tick.Dt);
    }
}
