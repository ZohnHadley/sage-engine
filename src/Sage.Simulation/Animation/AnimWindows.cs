#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Windows between two clip events (issue #359): the part of a swing where the blade is dangerous, the
// part of a guard that parries, read from the clip's events (anim_events, a sheet's frame events) the
// way a blow lands on its `hit` — so the timing lives with the art, per clip, and each direction of a
// directional set keeps its own. A window is open from its `open` event until its `close` event, in the
// clip the layer shows now (its state's clip, or its blend's heaviest: Animators.TryGetClip's), and
// closes when the state changes. A clip with no `open` event has no window.
public static partial class Animators
{
    // Whether `open` has happened in the clip the layer shows now and `close` has not happened since. An
    // empty `close`: open until the state ends. False without an animator, a clip or the event.
    public static bool InWindow(World world, Entity entity, string open, string? close = null, string? layer = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (string.IsNullOrEmpty(open) || entity.IsNull || !world.IsAlive(entity) || !world.Has<Animator>(entity)) return false;
        ref var a = ref world.Get<Animator>(entity);
        if (a.Layers == null || Compiled(world, a.Graph) is not { } g) return false;
        AnimatorStepper.Resolve(entity, ref a, g);
        int l = g.LayerIndex(layer);
        if (l < 0) return false;
        ref var s = ref a.Layers![l];
        if (s.Index < 0) return false;
        int c = AnimatorStepper.HeaviestClip(g.Layers[l].States[s.Index], a.Params!);
        if (c < 0 || !world.Resources.TryGet<AnimatorPoses>(out var poses) || poses == null
            || poses.Find(a.Slot, entity) is not { } instance || c >= instance.Clips.Length || instance.Clips[c] is not { } clip)
            return false;

        // The latest of the two at or before now decides (events are sorted by time).
        float now = s.Phase * clip.Duration;
        bool isOpen = false;
        var events = clip.Events;
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Time > now) break;
            if (string.Equals(e.Name, open, StringComparison.OrdinalIgnoreCase)) isOpen = true;
            else if (!string.IsNullOrEmpty(close) && string.Equals(e.Name, close, StringComparison.OrdinalIgnoreCase)) isOpen = false;
        }
        return isOpen;
    }
}

// `{ "anim_window": "hit_start", "close": "hit_end" }`: the animator on the entity doing the asking (the
// context's other; its subject when there is none) is between those two events of the clip its layer
// (`layer`; empty: the base) shows now (Animators.InWindow).
[Condition("anim_window", Plugin = RegistrationOwners.Core)]
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
internal sealed class AnimWindowCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The clip event that opens the window")]
    public string Open = "";
    [Property(Tooltip = "The clip event that closes it; empty: open until the state ends")]
    public string Close = "";
    [Property(Tooltip = "The layer whose clip it reads; empty or \"base\": the base layer")]
    public string Layer = "";

    public bool Test(in ConditionContext context, out string why)
    {
        why = "not inside its window";
        var entity = context.Other.IsNull ? context.Subject : context.Other;
        return Animators.InWindow(context.World, entity, Open, Close, Layer);
    }
}
