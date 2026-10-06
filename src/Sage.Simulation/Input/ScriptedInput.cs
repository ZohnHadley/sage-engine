#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Scripted input (docs/design/08 §9), the headless half: actions driven from the console instead of a
// device, **for a chosen local player** (issue #331). The client's `in_*` commands are thin wrappers; what
// a script is, how it ages and how it lands in the action state are here so a test can drive a second
// player without a window.
//
// Injected after the bindings are evaluated, so a script goes through the same PlayerCommand path a device
// does. Ages in the time the caller gives it (the client uses real time, so a paused game still runs its
// script).
public sealed class ScriptedInput
{
    private struct Entry
    {
        public int Player;
        public ActionId Action;
        public Vector2 Value;      // buttons: held while it lasts; axes: the value to report
        public float Remaining;    // seconds; infinity until released
        public bool OneFrame;
        public bool Rate;          // Value is per *second*, scaled by dt (turning, not a stick position)
    }

    private readonly List<Entry> _entries = new();

    public int Count => _entries.Count;

    // Driving an action twice is a script contradicting itself; the last wins.
    public void Inject(int player, ActionId action, Vector2 value, float seconds, bool oneFrame = false, bool rate = false)
    {
        Release(player, action);
        _entries.Add(new Entry { Player = player, Action = action, Value = value, Remaining = seconds, OneFrame = oneFrame, Rate = rate });
    }

    public void Release(int player, ActionId action)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
            if (_entries[i].Player == player && _entries[i].Action == action) _entries.RemoveAt(i);
    }

    public void Clear() => _entries.Clear();

    public void Clear(int player) => _entries.RemoveAll(e => e.Player == player);

    // Writes this player's scripts into the action state the bindings left, and ages them. Both held
    // arrays for a button (a script *is* the device as far as edges go, and consumption does not apply to
    // it); the axis for an axis.
    public void Apply(int player, float dt, ActionRegistry registry, bool[] held, bool[] rawHeld, Vector2[] axis)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var s = _entries[i];
            if (s.Player != player) continue;
            int index = s.Action.Index;
            if (index >= held.Length) { _entries.RemoveAt(i); continue; }

            if (registry.All[index].Kind == ActionKind.Button) held[index] = rawHeld[index] = true;
            else axis[index] = s.Rate ? s.Value * dt : s.Value;

            if (s.OneFrame) { _entries.RemoveAt(i); continue; }
            if (float.IsPositiveInfinity(s.Remaining)) continue;

            s.Remaining -= dt;
            if (s.Remaining <= 0f) _entries.RemoveAt(i);
            else _entries[i] = s;
        }
    }

    // For `in_scripted`: "[P2] Attack = [1.00, 1.00] (held)".
    public IEnumerable<string> Describe(ActionRegistry registry)
    {
        foreach (var s in _entries)
        {
            string left = float.IsPositiveInfinity(s.Remaining) ? "held" : $"{s.Remaining:F2}s left";
            yield return $"P{s.Player + 1} {registry.All[s.Action.Index].Name} = [{s.Value.X:F2}, {s.Value.Y:F2}] ({left})";
        }
    }

    // The player an `in_` command is aimed at: a trailing `@N` argument (1-based, as a person counts:
    // `in_tap Attack @2` is the second player). No such argument means the first player. False, with
    // `args` untouched, for a malformed one (`@0`, `@9`).
    public static bool TakePlayer(IReadOnlyList<string> args, out int player, out int count)
    {
        player = 0;
        count = args.Count;
        if (count == 0 || !args[count - 1].StartsWith('@')) return true;
        if (!int.TryParse(args[count - 1].AsSpan(1), out int number) || number < 1 || number > PadAssignment.MaxPads) return false;
        player = number - 1;
        count--;
        return true;
    }
}
