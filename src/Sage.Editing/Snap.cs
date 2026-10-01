#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Editing;

// Grid and angle snapping for gizmo drags (docs/design/15 §6). A step of 0 or less means "no snapping",
// so a tool passes its setting straight through.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class Snap
{
    // `value` to the nearest multiple of `step` metres.
    public static float ToGrid(float value, float step) =>
        step > 0f ? MathF.Round(value / step, MidpointRounding.AwayFromZero) * step : value;

    // `radians` to the nearest multiple of `stepDegrees`.
    public static float Angle(float radians, float stepDegrees)
    {
        if (stepDegrees <= 0f) return radians;
        float step = stepDegrees * (MathF.PI / 180f);
        return MathF.Round(radians / step, MidpointRounding.AwayFromZero) * step;
    }
}
