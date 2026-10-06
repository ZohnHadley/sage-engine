#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Sun shadows in every view (issue 4n-19): which of a frame's views get a shadow map of their own, and
// where it sits. Until 4n-19 only the screen's main view had one, fitted to its camera, and every other
// view read that map: a security monitor across the level, a split-screen partner 300 m away or a world
// drawing only into a render target had no shadows. Now each view that wants them — a **receiver** — gets
// its own cascades fitted to its own camera (ShadowMath.FitCascades, the same stable fit), drawn into its
// own render target, and its lit draws read that map.
//
// **The decision is here, headless; the client's `sage:shadow` only follows it.** A view receives when it
// is a real view (not a caster view, not the depth-only viewmodel view), its camera does not say
// `noShadows`, and it has a slice to cover (its near plane short of the shadow distance). Of those, the
// screen's main view comes first, then the others in their order, up to `r_shadow_views`
// (`MaxViews` at most): a view past the cap reads no map rather than one fitted to another camera.
// Views other than the main one draw their cascades at `r_shadow_view_size` texels a side (at most the
// main's), since a split-screen half or a monitor has fewer pixels than the screen.
internal static class ShadowViews
{
    // `r_shadow_views`: how many views a frame fits maps for, the main one included.
    public const int DefaultViews = 4;
    public const int MaxViews = 8;

    // `r_shadow_view_size`: texels a side of each cascade in a view other than the screen's main one.
    public const int DefaultViewSize = 1024;

    // Whether a view gets a map of its own (see above). `distance` is `r_shadow_distance`.
    public static bool Receives(bool shadowCaster, bool depthOnly, bool noShadows, float near, float far, float distance) =>
        !shadowCaster && !depthOnly && !noShadows && MathF.Min(far, distance) > near;

    // The receivers among `receives.Length` views, in the order their maps are fitted: `main` (the screen's
    // main view, or -1) first, then the rest in list order, at most `max` (clamped to 1..MaxViews) of them.
    // Writes their indices into `chosen` (at least that long) and returns how many.
    public static int Choose(ReadOnlySpan<bool> receives, int main, int max, Span<int> chosen)
    {
        max = Math.Clamp(max, 1, MaxViews);
        int count = 0;
        if (main >= 0 && main < receives.Length && receives[main]) chosen[count++] = main;
        for (int v = 0; v < receives.Length && count < max; v++)
            if (v != main && receives[v]) chosen[count++] = v;
        return count;
    }

    // Texels a side of each of a receiver's cascades: the main view's `size`, the others' `viewSize`, never
    // more than the main's.
    public static int CascadeSize(bool main, int size, int viewSize) => main ? size : Math.Min(size, Math.Max(4, viewSize));

    // A receiver's cascades: `fits.Length` maps over its slice from `near` to the shadow `distance` (or its
    // far plane), fitted to its own camera — `camera` in origin space, looking along `forward`, through
    // `projection` (an orthographic one has M44 = 1; the lens is read from it as the renderer has it).
    // `splits` is as long as `fits`. False when there is no slice to cover.
    public static bool Fit(Vector3 camera, Vector3 forward, in Matrix4x4 projection, float near, float far, float distance,
                           Vector3 sunDirection, int size, Vector3 origin, Span<ShadowFit> fits, Span<float> splits)
    {
        float reach = MathF.Min(far, distance);
        if (!(reach > near)) return false;
        bool orthographic = projection.M44 == 1f;
        ShadowMath.FitCascades(camera, forward, orthographic, 1f / projection.M11, 1f / projection.M22, near, reach,
                               sunDirection, size, origin, fits, splits);
        return true;
    }
}
