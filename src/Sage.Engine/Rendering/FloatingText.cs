#nullable enable
using System;
using System.Numerics;

namespace sage_engine;

// Numbers that pop up (docs/design/13 §3, 06 §3.12, TODO F39).
//
// The same shape as particles, and for the same reason: where a number is and how faded it is after
// half a second is arithmetic, and drawing it needs a font. So the rising and fading are here and the
// drawing is the client's, which also means a headless server never makes one.
//
// **It allocates nothing per hit.** A damage number is a small integer, and small integers have their
// strings cached: a fight of a hundred blows a second costs the same as standing still.
public sealed class FloatingTexts
{
    public struct Entry
    {
        public Vector3 Position;      // origin space, where it started
        public Vector3 Velocity;      // metres a second, usually up and a little sideways
        public float Age;
        public float Life;
        public uint Colour;           // RGBA; the alpha is what fades
        public float Scale;
        public string Text;
    }

    private const int MaxEntries = 64;      // more than this on screen is noise, not information
    private static readonly string[] SmallNumbers = BuildNumbers(1000);

    private Entry[] _entries = new Entry[MaxEntries];
    private readonly Random _random = new();

    public int Count { get; private set; }

    public bool Enabled { get; set; } = true;

    public Entry this[int index] => _entries[index];

    // How far through its life, for the drawing: 0 just thrown, 1 gone.
    public float Fraction(int index) => _entries[index].Life <= 0f ? 1f
        : Math.Clamp(_entries[index].Age / _entries[index].Life, 0f, 1f);

    // What a number looks like *now*: it rises, drifts a little, and fades out over the last third.
    public uint ColourOf(int index)
    {
        float fade = Fraction(index);
        float alpha = fade < 0.65f ? 1f : 1f - (fade - 0.65f) / 0.35f;
        uint colour = _entries[index].Colour;
        return (colour & 0x00FFFFFF) | ((uint)(byte)(((colour >> 24) & 0xFF) * alpha) << 24);
    }

    public void Add(string text, Vector3 at, uint colour, float scale = 1f, float life = 1.1f)
    {
        if (!Enabled || text.Length == 0) return;

        // Full up: the oldest goes, because it is the one already fading and the newest is the one the
        // player is waiting for.
        if (Count == _entries.Length) { Array.Copy(_entries, 1, _entries, 0, _entries.Length - 1); Count--; }

        // A little sideways scatter, so two hits in the same instant do not draw on top of each other.
        float drift = ((float)_random.NextDouble() * 2f - 1f) * 0.6f;
        _entries[Count++] = new Entry
        {
            Position = at,
            Velocity = new Vector3(drift, 1.6f, 0f),
            Age = 0f,
            Life = MathF.Max(life, 0.1f),
            Colour = colour,
            Scale = scale,
            Text = text,
        };
    }

    // The number for a hit, without allocating: 0..999 have their strings already, and anything bigger
    // is rare enough that the allocation does not matter.
    public static string Number(int value) =>
        value >= 0 && value < SmallNumbers.Length ? SmallNumbers[value] : value.ToString();

    public void Update(float dt)
    {
        for (int i = Count - 1; i >= 0; i--)
        {
            ref var entry = ref _entries[i];
            entry.Age += dt;
            if (entry.Age >= entry.Life)
            {
                _entries[i] = _entries[--Count];
                continue;
            }
            entry.Position += entry.Velocity * dt;
            entry.Velocity.Y -= 1.2f * dt;      // it slows as it rises, like a thrown thing
        }
    }

    public void Rebase(Vector3 offset)
    {
        for (int i = 0; i < Count; i++) _entries[i].Position += offset;
    }

    public void Clear() => Count = 0;

    private static string[] BuildNumbers(int upTo)
    {
        var numbers = new string[upTo];
        for (int i = 0; i < upTo; i++) numbers[i] = i.ToString();
        return numbers;
    }
}

// What a hit looks like when it pops up (16 §3.2, F39). A rule rather than a constant, because a game
// wants its own: `damage_type` records carry the colour, and what the engine ships is a sensible default
// for anything that does not say.
public static class DamageNumbers
{
    public const uint Default = 0xFFEEEEEE;      // white-ish: a hit
    public const uint Healing = 0xFF88EE88;      // green: a gain
    public const uint Mine = 0xFFFF7766;         // what *you* took, so it reads differently from what you dealt

    // Where the number starts: above the hit, not on it, so it is not swallowed by the body it belongs
    // to. Half a metre up is about a head.
    public static Vector3 Above(Vector3 point) => point + new Vector3(0, 0.5f, 0);

    public static uint ColourFor(RecordStore records, RecordId damageType, bool onThePlayer)
    {
        if (onThePlayer) return Mine;
        if (!damageType.IsEmpty && records.TryGet(damageType, out DamageTypeRecord type) && type.Colour != 0)
            return type.Colour;
        return Default;
    }
}
