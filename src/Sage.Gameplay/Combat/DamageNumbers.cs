#nullable enable
using System.Numerics;

namespace sage_engine;

// What a hit looks like when it pops up (16 §3.2, F39). A rule rather than a constant, because a game
// wants its own: `damage_type` records carry the colour, and what the engine ships is a sensible default
// for anything that does not say.
public static class DamageNumbers
{
    public const uint Default = 0xFFEEEEEE;      // white-ish: a hit
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
