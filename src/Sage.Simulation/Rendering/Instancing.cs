#nullable enable
using System;

namespace Sage.Simulation;

// Mesh and sprite instancing (issue 4n-5, docs/design/06 §3.7). Grass, rocks, trees and a town's clutter
// are hundreds of copies of one mesh with one material; drawn one at a time each is its own draw call,
// and the per-call cost, not the triangles, is what an old-style open world runs out of.
//
// With `r_instancing 1` the client's renderer hands each stage's sorted items to `Plan`: a run of at
// least `r_instancing_min` neighbours that share a mesh part, a material and the per-draw state (tint
// and the lamps lighting them, `State`) becomes one `DrawInstancedPrimitives` call, the world matrices
// going in a second vertex stream. A material is instanced through its effect's instanced technique
// (`TechniqueFor`: lit.fx's `Instanced`, `AlphaTestInstanced`, `UnlitInstanced`, sprite.fx's
// `LitInstanced`...), and drawn the old way when its effect has none, when the item is skinned, or when
// the device cannot instance (a GL below 3.2: the first failed call turns it off, said once). The sun's
// caster view is planned the same way with lit.fx's `ShadowCasterInstanced`.
//
// **The decision is here and the drawing is not**, like `MeshLod` and `LightRules`: which neighbours make
// a run, how long a run must be and how it is split are headless and tested; `r_stats` counts the items
// drawn instanced and the draws they took (`instanced N in M draws`).
internal static class Instancing
{
    // r_instancing_min's default: below this many copies, setting up the second stream costs about what
    // it saves.
    public const int DefaultMin = 8;

    // The most instances in one draw: the client's instance buffer holds this many world matrices.
    public const int MaxPerDraw = 1024;

    // The instanced technique of a material's technique: `Default`'s is `Instanced`, any other's is its
    // name with `Instanced` after it. A material whose effect has no such technique is not instanced.
    public static string TechniqueFor(string technique) =>
        technique == "Default" ? "Instanced" : technique + "Instanced";

    // Splits `items` (one stage's, in draw order) into runs: consecutive eligible items with the same
    // mesh, part, material and state, `min` or more of them, are instanced, at most `maxPerDraw` a draw
    // (a remainder shorter than `min` is drawn one by one); everything else is a run of single draws.
    // Neighbouring single draws share one run. Returns the number of runs written; `runs` needs at most
    // `items.Length` entries.
    public static int Plan(ReadOnlySpan<InstanceCandidate> items, int min, int maxPerDraw, Span<InstanceRun> runs)
    {
        min = Math.Max(2, min);
        maxPerDraw = Math.Max(min, maxPerDraw);
        int count = 0, singles = -1;   // singles: where the open run of single draws starts
        int i = 0;
        while (i < items.Length)
        {
            int end = i + 1;
            if (items[i].Eligible)
                while (end < items.Length && items[end].Eligible && items[end].SameDraw(items[i])) end++;

            int at = i;
            while (end - at >= min)
            {
                int take = Math.Min(maxPerDraw, end - at);
                if (take < min) break;
                if (singles >= 0) { runs[count++] = new InstanceRun(singles, at - singles, false); singles = -1; }
                runs[count++] = new InstanceRun(at, take, true);
                at += take;
            }
            if (at < end && singles < 0) singles = at;
            i = end;
        }
        if (singles >= 0) runs[count++] = new InstanceRun(singles, items.Length - singles, false);
        return count;
    }

    // Draw calls saved by `runs`: an instanced run of n items is one draw instead of n.
    public static int Saved(ReadOnlySpan<InstanceRun> runs)
    {
        int saved = 0;
        foreach (var run in runs)
            if (run.Instanced) saved += run.Count - 1;
        return saved;
    }
}

// One item as `Instancing.Plan` sees it. `State` is whatever else a draw sets per item that the instanced
// draw sets once (the tint, the lamps): the caller numbers it, and neighbours with the same number share
// it. `Eligible` is false for what cannot be instanced at all (skinned, a material without an instanced
// technique, instancing off).
internal readonly record struct InstanceCandidate(int Material, int Mesh, int Part, int State, bool Eligible)
{
    public bool SameDraw(in InstanceCandidate other) =>
        Material == other.Material && Mesh == other.Mesh && Part == other.Part && State == other.State;
}

// A run of `Instancing.Plan`'s: items [Start, Start + Count) of its input, one instanced draw, or each
// drawn on its own.
internal readonly record struct InstanceRun(int Start, int Count, bool Instanced);
