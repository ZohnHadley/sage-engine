#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Point lights (docs/design/06 §3.9, 07 §3.5; TODO F2's last leftover).
//
// **Why now.** The engine got interiors (F16) and a room with a roof is lit by a sun it cannot see: the
// inside of the Sandbox's hut was a uniform dark grey box, and no amount of ambient fixes that without
// flattening the outdoors too. A light you can put in a room is the smallest thing that makes an
// interior look like a place.
//
// Forward lighting, four lights at a time, chosen per object. That ceiling is not a limitation of the
// idea but of the shader model DesktopGL gives us (07 §2) — and four is enough for a room, which is what
// this is for. Lightmaps (HL1's answer, and the right one for a big level) are still later.
[Component("sage:point_light")]
public struct PointLight : IComponent
{
    [Property(Min = 0, Tooltip = "Linear RGB; 1 1 1 is white")]
    public Vector3 Colour;      // linear rgb; 1,1,1 is white
    [Property(Min = 0, Unit = "m", Tooltip = "Where the light fades to nothing")]
    public float Range;         // metres to where it fades to nothing
    [Property(Min = 0, Tooltip = "Multiplies the colour; 1 is a lamp")]
    public float Intensity;     // multiplies the colour; 1 is "a lamp"
}

// One light as the renderer wants it: where it is, what it contributes, how far it reaches.
public readonly record struct LightSample(Vector3 Position, Vector3 Colour, float Range)
{
    // How much this light matters at a point. Distance alone is the wrong measure — a bright lamp four
    // metres away beats a candle at two — so it is the falloff, which is what the shader will compute.
    public float InfluenceAt(Vector3 at)
    {
        if (Range <= 0f) return 0f;
        float distance = Vector3.Distance(Position, at);
        if (distance >= Range) return 0f;

        float falloff = 1f - distance / Range;
        return falloff * falloff * MathF.Max(MathF.Max(Colour.X, Colour.Y), Colour.Z);
    }
}

public static class LightRules
{
    // How many lights one draw can carry. The shader declares arrays of this size (common.fxh).
    public const int PerObject = 4;

    // The lights that matter most at a point, strongest first, into `result`; returns how many.
    //
    // This is a *decision* — which of a hundred lamps a wall is lit by — so it lives here rather than in
    // the renderer, and is tested headlessly. A selection sort, because picking four out of a handful is
    // what this is for and an allocation-free pass beats a clever data structure at that size (02 §4.6).
    public static int Nearest(ReadOnlySpan<LightSample> lights, Vector3 at, Span<LightSample> result)
    {
        int wanted = Math.Min(result.Length, PerObject);
        if (wanted == 0) return 0;

        Span<float> influence = stackalloc float[PerObject];
        int found = 0;

        foreach (var light in lights)
        {
            float score = light.InfluenceAt(at);
            if (score <= 0f) continue;                    // out of range: not dim, absent

            // Where it belongs among the ones kept so far.
            int place = found;
            while (place > 0 && influence[place - 1] < score) place--;
            if (place >= wanted) continue;

            for (int i = Math.Min(found, wanted - 1); i > place; i--)
            {
                influence[i] = influence[i - 1];
                result[i] = result[i - 1];
            }
            influence[place] = score;
            result[place] = light;
            if (found < wanted) found++;
        }

        return found;
    }
}
