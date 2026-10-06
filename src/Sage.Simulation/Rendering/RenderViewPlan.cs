#nullable enable
using System;

namespace Sage.Simulation;

// The planning half of drawing several views in a frame (docs/design/06 §3.2, issue #77): which views
// draw in which order, and which of the frame's items and sprites belong to each. Pure and allocation
// free, so it lives here, where a headless test can reach it (tests cannot reference MonoGame); the
// client's `Renderer` feeds it copies of the snapshot's keys and draws what it says.
//
// A view is identified by its index in the snapshot. Its target is an index into the renderer's pool of
// named render targets, or `Screen` for the back buffer.
internal static class RenderViewPlan
{
    public const int Screen = -1;

    // The order to draw `targets.Length` views in, written to `drawOrder`:
    //   1. every view into an off-screen target first, so anything on screen that samples a target
    //      (`rt:<name>`, a minimap in the HUD) sees this frame's picture; grouped by target, lowest
    //      target id first, so a target is bound once;
    //   2. then the screen's views.
    // Within one target, lower `orders` draw first (a higher priority draws later, on top: a
    // picture-in-picture over the main view), and ties keep their snapshot order.
    public static void OrderViews(ReadOnlySpan<int> targets, ReadOnlySpan<int> orders, Span<int> drawOrder)
    {
        int n = targets.Length;
        if (orders.Length < n || drawOrder.Length < n)
            throw new ArgumentException("orders and drawOrder need one entry per view");
        for (int i = 0; i < n; i++) drawOrder[i] = i;

        // Insertion sort: a frame has a handful of views, and it is stable, which Array.Sort is not.
        for (int i = 1; i < n; i++)
        {
            int view = drawOrder[i];
            int j = i - 1;
            while (j >= 0 && Before(view, drawOrder[j], targets, orders))
            {
                drawOrder[j + 1] = drawOrder[j];
                j--;
            }
            drawOrder[j + 1] = view;
        }
    }

    private static bool Before(int a, int b, ReadOnlySpan<int> targets, ReadOnlySpan<int> orders)
    {
        int ta = targets[a] == Screen ? int.MaxValue : targets[a];
        int tb = targets[b] == Screen ? int.MaxValue : targets[b];
        if (ta != tb) return ta < tb;
        return orders[a] < orders[b];
    }

    // Groups `viewOf.Length` entries (items, or sprites) by view and sorts each view's run by key.
    // `viewOf[i]` is entry i's view and `keys[i]` its sort key (06 §3.5). Afterwards view v's entries
    // are `order[starts[v] .. starts[v] + counts[v])`, in key order, with their keys beside them in
    // `sortedKeys`. `starts` and `counts` need one slot per view; `sortedKeys` and `order` one per entry.
    //
    // A counting sort by view, then a stable radix sort within each view (`RadixSort`, ties keep entry order): extract writes entries interleaved (a
    // system adds every view's copy of an entity before moving on, and particles come after sprites),
    // so the view cannot simply be the top bits of a key that already uses all 64.
    public static void Bucket(ReadOnlySpan<int> viewOf, ReadOnlySpan<ulong> keys, Span<int> starts, Span<int> counts,
                              Span<ulong> sortedKeys, Span<int> order)
    {
        int n = viewOf.Length;
        int views = counts.Length;
        if (keys.Length < n || sortedKeys.Length < n || order.Length < n || starts.Length < views)
            throw new ArgumentException("keys, sortedKeys and order need one entry per entry; starts one per view");

        counts.Clear();
        for (int i = 0; i < n; i++) counts[viewOf[i]]++;

        int at = 0;
        for (int v = 0; v < views; v++)
        {
            starts[v] = at;
            at += counts[v];
        }

        // Scatter, with `starts` as each view's cursor; put back afterwards.
        for (int i = 0; i < n; i++)
        {
            int slot = starts[viewOf[i]]++;
            order[slot] = i;
            sortedKeys[slot] = keys[i];
        }
        for (int v = 0; v < views; v++)
        {
            starts[v] -= counts[v];
            if (counts[v] > 1) RadixSort.Sort(sortedKeys.Slice(starts[v], counts[v]), order.Slice(starts[v], counts[v]));
        }
    }
}
