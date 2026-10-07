#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Sage.UI;

namespace Sage.Gameplay;

// Barks (issue #392): a line said without a window — a guard's "Halt!" when it sees you, "Where did they
// go?" when it loses you, a grunt when hurt, a mutter while it stands about. A `barks` record is who says
// it (a label) and lines by occasion; the `barks` prefab part puts it on a creature, and the dialogue
// plugin's BarkSystem says them on the engine's four occasions:
//
//   alert  — an AI took a target when it had none (it saw, heard or was hit by somebody)
//   lost   — it had one and has none
//   hurt   — it was damaged (the attacker is the one barked at)
//   idle   — every `idleSeconds` while it has no target (0 = never)
//
// Any other word is a game's, said by the `bark` action (`{ "bark": "surrender" }`, the action's other
// barking at its subject — a state machine's machine at its activator, a dialogue's speaker at the
// listener) or by DialogueBarks.Bark. A line may require a condition (asked about the one barked at,
// with the barker as the other, as a topic's info is) and may play a sound; of the lines for an occasion
// whose requires hold, they take turns. A barker waits `cooldown` seconds between lines, so a creature
// hit five times in a second says one thing.
//
//   { "type": "barks", "id": "guard", "speaker": "Guard", "cooldown": 4, "idleSeconds": 20,
//     "lines": [ { "on": "alert", "text": "Halt!" },
//                { "on": "alert", "requires": { "standing": "watch", "max": -20 }, "text": "You again!" },
//                { "on": "lost",  "text": "Must have been the wind." } ] }
//
// What a bark does is send `Barked` and, when the player is within `range`, say it in the world's
// subtitles (Sage.UI's Subtitles, the `subtitles` cvar). Nothing is saved but which record a barker has.

[Record("barks", Plugin = "sage.gameplay.dialogue")]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // barks (#392): may change before 1.0
public sealed class BarkRecord
{
    [Property(Tooltip = "Who the subtitle says is speaking; empty = the entity's name. May be a raw @key")]
    public string Speaker = "";
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds between two lines from the same barker")]
    public float Cooldown = 4f;
    [Property(Min = 0, Unit = "m", Tooltip = "How near the player must be to see the line as a subtitle; 0 = anywhere")]
    public float Range = 25f;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds between idle lines while it has no target; 0 = no idle lines")]
    public float IdleSeconds;
    [Property(Tooltip = "The lines, by occasion: alert, lost, hurt, idle, or a game's own word")]
    public List<BarkLine> Lines = new();
}

// One line of a bark.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // barks (#392): may change before 1.0
public sealed class BarkLine
{
    [Property(Tooltip = "When it is said: alert, lost, hurt, idle, or a game's own word (the bark action)")]
    public string On = "";
    [Property(Tooltip = "When this line may be said: one condition, asked about the one barked at with the barker as the other; none = always")]
    public ICondition? Requires;
    [Property(Tooltip = "What is said. May be a raw @key, resolved where it is shown")]
    public string Text = "";
    [Property(Tooltip = "A voice to play at the barker; give it no caption, the line is the subtitle")]
    public RecordRef<SoundRecord> Sound;
}

// Says lines from this `barks` record.
[Component("sage:barks")]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // barks (#392): may change before 1.0
public struct Barks : IComponent
{
    [Property(Tooltip = "The barks record it says lines from")]
    public RecordId Record;

    // Not saved: when it may bark again, when it next mutters, whom it last had in its sights, and whose
    // turn it is among a record's lines.
    internal double NextAt;
    internal double IdleAt;
    internal Entity Had;
    internal bool Engaged;   // had a target: a destroyed entity's handle reads as null, so this is kept apart
    internal bool Seen;
    internal int Turn;
}

// Something barked: who, at whom (maybe nobody), on what occasion and the line as written (maybe `@key`).
[GameEvent]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // barks (#392): may change before 1.0
public readonly record struct Barked(Entity Speaker, Entity Target, string Occasion, string Text);

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // barks (#392): may change before 1.0
public static class DialogueBarks
{
    // The engine's occasions (BarkSystem says them).
    public const string Alert = "alert", Lost = "lost", Hurt = "hurt", Idle = "idle";

    // Says a line for `occasion` from `speaker`'s barks at `target`: the next of that occasion's lines
    // whose requires hold. False, with nothing said, when it has no barks, is still cooling down, is dead,
    // or has no line for it.
    public static bool Bark(World world, Entity speaker, string occasion, Entity target = default)
    {
        if (!world.IsAlive(speaker) || !world.Has<Barks>(speaker) || string.IsNullOrEmpty(occasion)) return false;
        if (Store(world) is not { } records) return false;
        ref var barks = ref world.Get<Barks>(speaker);
        if (barks.Record.IsEmpty || world.SimTime < barks.NextAt) return false;
        if (!records.TryGet(barks.Record, out BarkRecord record)) return false;
        var dead = world.Conventions().Dead;
        if (!dead.IsEmpty && world.Has<GameplayTags>(speaker) && world.HasTag(speaker, dead.Id)) return false;

        var line = Pick(record, occasion, new ConditionContext(world, target, speaker), ref barks.Turn);
        if (line == null) return false;
        barks.NextAt = world.SimTime + record.Cooldown;
        Say(world, speaker, target, occasion, record, line);
        return true;
    }

    private static BarkLine? Pick(BarkRecord record, string occasion, in ConditionContext context, ref int turn)
    {
        var lines = record.Lines;
        int count = lines.Count;
        if (count == 0) return null;
        // Take turns: start after the last line said, so "Halt!" and "Stop there!" alternate.
        for (int k = 0; k < count; k++)
        {
            int i = (turn + k) % count;
            var line = lines[i];
            if (line == null || !string.Equals(line.On, occasion, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Conditions.Test(line.Requires, in context, out _)) continue;
            turn = i + 1;
            return line;
        }
        return null;
    }

    private static void Say(World world, Entity speaker, Entity target, string occasion, BarkRecord record, BarkLine line)
    {
        bool placed = world.TryGet<GlobalTransform>(speaker, out var where);
        var at = placed ? where.Current.Position : Vector3.Zero;
        if (!line.Sound.IsEmpty) world.Events.Send(new SoundRequested(line.Sound.Id, speaker, at, placed, 1f));
        world.Events.Send(new Barked(speaker, target, occasion, line.Text));

        if (line.Text.Length == 0 || !world.Resources.TryGet<Subtitles>(out var subtitles) || subtitles == null) return;
        if (record.Range > 0f && placed)
        {
            var player = Scenes.Player(world);
            if (!world.TryGet<GlobalTransform>(player, out var heard)) return;
            if (Vector3.DistanceSquared(heard.Current.Position, at) > record.Range * record.Range) return;
        }
        subtitles.Say(record.Speaker.Length > 0 ? record.Speaker : speaker.Name ?? "", line.Text);
    }

    private static RecordStore? Store(World world) =>
        world.Resources.TryGet<RecordStore>(out var records) && records != null && records.TypeNameOf(typeof(BarkRecord)) != null
            ? records : null;
}

// The engine's occasions: an AI that takes a target or loses it, a barker that is hurt, and one that
// stands about. After the AI has thought this tick, so `alert` is the tick it noticed.
[System("sage.dialogue.barks", Phase.Gameplay)]
internal sealed class BarkSystem : ISystem
{
    private readonly Query<Barks> _barkers;
    private readonly EventReader<Damaged> _damaged;

    public BarkSystem(World world)
    {
        _barkers = world.Query<Barks>();
        _damaged = world.Events.Reader<Damaged>(this);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        if (_damaged.HasPending)
            foreach (ref readonly var hit in _damaged.Read())
                if (world.IsAlive(hit.Hit.Target) && world.Has<Barks>(hit.Hit.Target))
                    DialogueBarks.Bark(world, hit.Hit.Target, DialogueBarks.Hurt, hit.Hit.Attacker);

        double now = world.SimTime;
        foreach (var (barks, entities) in _barkers.Chunks)
        {
            var b = barks.Span;
            for (int n = 0; n < b.Length; n++)
            {
                var entity = entities.EntityAt(n);
                var target = world.TryGet<AIState>(entity, out var state) && world.IsAlive(state.Target) ? state.Target : default;
                ref var barker = ref b[n];
                bool engaged = !target.IsNull;
                var had = barker.Had;
                bool was = barker.Engaged;
                barker.Had = target;
                barker.Engaged = engaged;
                if (!barker.Seen)
                {
                    // First seen: the idle clock starts; one that already has a target is alert now.
                    barker.Seen = true;
                    barker.IdleAt = now + IdleSeconds(world, barker.Record);
                }

                if (engaged != was)
                {
                    // Bark writes this barker's component through the world: the same storage as the
                    // span, which stays valid, as nothing here adds or removes a component.
                    DialogueBarks.Bark(world, entity, engaged ? DialogueBarks.Alert : DialogueBarks.Lost, engaged ? target : had);
                    barker.IdleAt = now + IdleSeconds(world, barker.Record);
                    continue;
                }
                if (engaged || now < barker.IdleAt) continue;
                barker.IdleAt = now + IdleSeconds(world, barker.Record);
                DialogueBarks.Bark(world, entity, DialogueBarks.Idle, Scenes.Player(world));
            }
        }
    }

    private static double IdleSeconds(World world, RecordId record) =>
        !record.IsEmpty && world.Resources.TryGet<RecordStore>(out var records) && records != null
        && records.TypeNameOf(typeof(BarkRecord)) != null && records.TryGet(record, out BarkRecord found) && found.IdleSeconds > 0f
            ? found.IdleSeconds : double.PositiveInfinity;   // no idle lines: never
}

// `"barks": "guard"` — says lines from that `barks` record (BarkSystem, the `bark` action).
[PrefabPart("barks", Plugin = "sage.gameplay.dialogue", Shorthand = nameof(Id))]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // barks (#392): may change before 1.0
public sealed class BarksPart : IPrefabPart
{
    [Property(Tooltip = "The barks record it says lines from")]
    public RecordRef<BarkRecord> Id;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Id.IsEmpty) { ctx.Error("needs a record id"); return; }
        ctx.World.Add(ctx.Entity, new Barks { Record = Id });
    }
}

// `{ "bark": "surrender" }`: the action's other barks a line for that occasion at its subject — a state
// machine at its activator, a conversation's speaker at the listener.
[Action("bark", Plugin = "sage.gameplay.dialogue")]
internal sealed class BarkAction : IAction
{
    [EntryValue, Property(Tooltip = "The occasion: alert, lost, hurt, idle, or a game's own word")]
    public string On = "";

    public void Run(in ActionContext context) => DialogueBarks.Bark(context.World, context.Other, On, context.Subject);
}
