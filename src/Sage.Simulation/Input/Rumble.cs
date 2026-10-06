#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Haptics as data (docs/design/08, issue #331). A `rumble` record is a shape of vibration; a cue or a
// damage type names one (`rumble` beside `sound`), and the simulation raises it for the player whose entity
// the thing happened to. Nothing here knows a pad exists: the mixer says how hard each player's two motors
// should turn right now, and the client passes that to `GamePad.SetVibration`. A headless server has the
// mixer and nobody reading it.
[Record("rumble", Plugin = RegistrationOwners.Core)]
public sealed class RumbleRecord
{
    [Property(Min = 0, Max = 1, Tooltip = "The heavy, low-frequency motor at full strength (a thump, an explosion)")]
    public float Low = 0.5f;
    [Property(Min = 0, Max = 1, Tooltip = "The light, high-frequency motor at full strength (a buzz, a tick)")]
    public float High = 0.5f;
    [Property(Min = 0, Unit = "s", Tooltip = "How long it lasts, attack and release included")]
    public float Duration = 0.2f;
    [Property(Min = 0, Unit = "s", Tooltip = "How long it takes to rise to full strength")]
    public float Attack = 0f;
    [Property(Min = 0, Unit = "s", Tooltip = "How long it takes to fall to nothing at the end of the duration")]
    public float Release = 0.1f;
}

// What each local player's motors are doing: the sum of every effect still playing, per player, over time.
// A world resource (a game with a player adds it; `RumbleSystem` ages it). Two explosions in one tick are
// one strong rumble, not the last one: effects add, and the motor is clamped at 1.
public sealed class RumbleMixer
{
    public const int MaxPlayers = 4;
    public const int MaxEffects = 32;   // a flood of cues is bounded: the oldest effect makes room

    private struct Effect
    {
        public int Player;
        public float Low, High, Duration, Attack, Release, Age;
    }

    private readonly List<Effect> _effects = new();
    private readonly float[] _low = new float[MaxPlayers];
    private readonly float[] _high = new float[MaxPlayers];

    // `joy_rumble`: a multiplier on everything (0 = off), set by the host.
    public float Gain { get; set; } = 1f;

    public int Count => _effects.Count;

    public float Low(int player) => (uint)player < MaxPlayers ? _low[player] : 0f;
    public float High(int player) => (uint)player < MaxPlayers ? _high[player] : 0f;

    public void Start(int player, RumbleRecord record, float scale = 1f) =>
        Start(player, record.Low * scale, record.High * scale, record.Duration, record.Attack, record.Release);

    public void Start(int player, float low, float high, float duration, float attack = 0f, float release = 0f)
    {
        if ((uint)player >= MaxPlayers || duration <= 0f) return;
        if (_effects.Count >= MaxEffects) _effects.RemoveAt(0);
        _effects.Add(new Effect
        {
            Player = player, Low = Math.Clamp(low, 0f, 1f), High = Math.Clamp(high, 0f, 1f),
            Duration = duration, Attack = Math.Max(0f, attack), Release = Math.Max(0f, release),
        });
        Sum();   // felt at once, not a tick late
    }

    public void Stop(int player)
    {
        _effects.RemoveAll(e => e.Player == player);
        Sum();
    }

    public void Clear() { _effects.Clear(); Sum(); }

    // Ages every effect and re-sums the motors.
    public void Update(float dt)
    {
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            var e = _effects[i];
            e.Age += dt;
            if (e.Age >= e.Duration) _effects.RemoveAt(i);
            else _effects[i] = e;
        }
        Sum();
    }

    // Attack ramps up from the start, release ramps down to the end; where they overlap the lower wins.
    internal static float Envelope(float age, float duration, float attack, float release)
    {
        float level = 1f;
        if (attack > 0f) level = Math.Min(level, age / attack);
        float left = duration - age;
        if (release > 0f) level = Math.Min(level, left / release);
        return Math.Clamp(level, 0f, 1f);
    }

    private void Sum()
    {
        Array.Clear(_low);
        Array.Clear(_high);
        foreach (var e in _effects)
        {
            float level = Envelope(e.Age, e.Duration, e.Attack, e.Release);
            _low[e.Player] += e.Low * level;
            _high[e.Player] += e.High * level;
        }
        for (int p = 0; p < MaxPlayers; p++)
        {
            _low[p] = Math.Min(1f, _low[p] * Gain);
            _high[p] = Math.Min(1f, _high[p] * Gain);
        }
    }
}
