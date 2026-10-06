#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Each animator's runtime half, per world (issue #118): its pooled pose, the clips and masks of its graph
// resolved against its model, and what the tick needs to remember (where the body was, when it was last
// sampled). Indexed by Animator.Slot, which is [Transient]: after a load, or when the entity is gone, the
// slot is found again or handed back (Sweep), and its pose's arrays go back to the ArrayPool.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
internal sealed class AnimatorPoses
{
    private readonly List<Instance?> _slots = new() { null };   // slot 0: none
    private readonly Stack<int> _free = new();

    internal sealed class Instance
    {
        public Entity Owner;
        public long Seen = -1;
        public long SampledTick = -1;
        public int Interval = 1;                                 // animation LOD: sampled every Interval ticks
        public AnimationSet? Set;
        public SpriteSheetRecord? Sheet;                         // a sprite's animator (no model): the sheet it plays
        public object? SheetClips;                               // and the sheet's clips it was resolved against
        public SkeletonPose? Pose;                               // what readers get: rewritten from Sampled every tick
        public SkeletonPose? Sampled;                            // the graph's output, at the LOD's rate
        public int ResolvedVersion;                              // the compiled graph Clips and Masks are for
        public AnimationClip?[] Clips = Array.Empty<AnimationClip?>();
        public JointMask?[] Masks = Array.Empty<JointMask?>();
        public Vector3 LastPosition;
        public bool HasLastPosition;
        public bool LoadTried;
        // Per layer, the frozen pose a PlayFrom fade starts from (issue #244); rented from the snapshot
        // pool while the fade runs and handed back when it ends.
        public SkeletonPose?[] Snapshots = Array.Empty<SkeletonPose?>();
    }

    // Snapshot poses no fade holds, per skeleton: PlayFrom rents one, and it comes back when the fade
    // ends, so getting up again (a ragdoll's, issue #244) allocates nothing once the pool has met it.
    private readonly Dictionary<Skeleton, Stack<SkeletonPose>> _snapshotPool = new(ReferenceEqualityComparer.Instance);

    public SkeletonPose RentSnapshot(Skeleton skeleton)
    {
        if (_snapshotPool.TryGetValue(skeleton, out var free) && free.Count > 0) return free.Pop();
        return new SkeletonPose(skeleton);
    }

    // Hands back the instance's snapshot for `layer` (all of them when layer < 0), if it holds one.
    public void ReturnSnapshots(Instance instance, int layer = -1)
    {
        var held = instance.Snapshots;
        for (int l = layer < 0 ? 0 : layer; l < held.Length && (layer < 0 || l == layer); l++)
        {
            if (held[l] is not { } pose) continue;
            held[l] = null;
            if (!_snapshotPool.TryGetValue(pose.Skeleton, out var free)) _snapshotPool.Add(pose.Skeleton, free = new Stack<SkeletonPose>());
            free.Push(pose);
        }
    }

    public int Count => _slots.Count - 1 - _free.Count;

    public Instance? Find(int slot, Entity owner) =>
        slot > 0 && slot < _slots.Count && _slots[slot] is { } i && i.Owner == owner ? i : null;

    // The animator's instance, made the first time (a spawn, a load: never a steady tick).
    public Instance Acquire(ref Animator animator, Entity owner)
    {
        if (Find(animator.Slot, owner) is { } found) return found;
        var instance = new Instance { Owner = owner };
        int slot;
        if (_free.Count > 0) _slots[slot = _free.Pop()] = instance;
        else { slot = _slots.Count; _slots.Add(instance); }
        animator.Slot = slot;
        return instance;
    }

    // Hands back every slot whose animator was not seen on `tick` (destroyed, or its Animator removed),
    // taking its pose out of SkeletonPoses (#120; and through it #117's SkinPoses) first.
    public void Sweep(long tick, SkeletonPoses registered)
    {
        for (int i = 1; i < _slots.Count; i++)
        {
            if (_slots[i] is not { } instance || instance.Seen == tick) continue;
            Release(instance, registered);
            _slots[i] = null;
            _free.Push(i);
        }
    }

    // Takes the instance's pose out of SkeletonPoses (when it is the one registered for its entity) and
    // returns its arrays (and its snapshots to the pool).
    public void Release(Instance instance, SkeletonPoses registered)
    {
        ReturnSnapshots(instance);
        if (instance.Pose is not { } pose) return;
        // A destroyed entity reads as null, so TryGet no longer finds it; Remove still matches its handle
        // (never an entity that reused the id). A live one is left alone if another source registered it.
        if (!registered.TryGet(instance.Owner, out var shown) || ReferenceEquals(shown, pose)) registered.Remove(instance.Owner);
        pose.Dispose();
        instance.Pose = null;
        instance.Sampled?.Dispose();
        instance.Sampled = null;
    }
}

// Scratch poses for one skeleton: a layer's pose, the state it fades from, and one clip of a blend.
internal sealed class AnimatorScratch
{
    public readonly SkeletonPose Layer, From, Clip;

    public AnimatorScratch(Skeleton skeleton)
    {
        Layer = new SkeletonPose(skeleton);
        From = new SkeletonPose(skeleton);
        Clip = new SkeletonPose(skeleton);
    }
}

// Stepping and sampling one animator: pure over the component, the compiled graph and the instance, so
// the system, the Animators API and tests share it. Allocation-free once the animator has been resolved
// against its graph and model (which allocates once per animator, and again after a reload or a load).
internal static class AnimatorStepper
{
    private const float Epsilon = 1e-4f;
    private const float RadToDeg = 180f / MathF.PI;

    // Shapes the animator's layers and params to the graph (by name) the first time, after a load and
    // after a reload; a layer whose state is gone goes to its initial state, with a warning.
    public static void Resolve(Entity entity, ref Animator a, AnimGraphRecord.Compiled g)
    {
        if (a.Version == g.Version && a.Layers != null && a.Params != null
            && a.Layers.Length == g.Layers.Length && a.Params.Length == g.ParamNames.Length) return;

        var oldParams = a.Params;
        var values = new AnimatorParam[g.ParamNames.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i].Name = g.ParamNames[i];
            values[i].Value = g.ParamDefaults[i];
            if (oldParams == null) continue;
            foreach (var old in oldParams)
            {
                if (!string.Equals(old.Name, g.ParamNames[i], StringComparison.OrdinalIgnoreCase)) continue;
                float v = float.IsFinite(old.Value) ? old.Value : 0f;
                values[i].Value = g.ParamKinds[i] == AnimParamKind.Float ? v : v != 0f ? 1f : 0f;
                break;
            }
        }

        var oldLayers = a.Layers;
        var layers = new AnimatorLayer[g.Layers.Length];
        for (int l = 0; l < layers.Length; l++)
        {
            var layer = g.Layers[l];
            ref var s = ref layers[l];
            if (oldLayers != null)
            {
                foreach (var old in oldLayers)
                {
                    if (!string.Equals(old.Name ?? Animators.BaseLayer, layer.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    s = old;
                    break;
                }
            }
            s.Name = layer.Name;
            if (!float.IsFinite(s.Time) || s.Time < 0f) s.Time = 0f;
            if (!float.IsFinite(s.Phase)) s.Phase = 0f;
            s.Index = layer.IndexOf(s.State);
            if (s.Index < 0)
            {
                if (!string.IsNullOrEmpty(s.State))
                    Log.Warn(LogCat.Animation, $"Animator {a.Graph} at {World.Describe(entity)}: layer '{layer.Name}' has no state '{s.State}' any more "
                                             + $"(a save or a reload from before it changed); going to '{layer.InitialName}'");
                s.Index = layer.Initial;
                s.State = layer.Initial >= 0 ? layer.Names[layer.Initial] : null;
                s.Time = 0f;
                s.Phase = 0f;
                s.From = null;
                s.FromSnapshot = false;
                s.Entered = true;
            }
            else s.State = layer.Names[s.Index];
            // A fade from a pose snapshot (PlayFrom) lives through a hot reload; after a load it has no
            // snapshot (FromSnapshot is not saved) and is dropped below: the layer shows its state.
            if (s.FromSnapshot && s.FadeDuration > 0f && float.IsFinite(s.Fade))
            {
                s.From = null;
                s.FromIndex = -1;
                continue;
            }
            s.FromSnapshot = false;
            s.FromIndex = s.From == null ? -1 : layer.IndexOf(s.From);
            if (s.FromIndex < 0 || !(s.FadeDuration > 0f) || !float.IsFinite(s.Fade))
            {
                s.From = null;
                s.FromIndex = -1;
                s.FromPhase = 0f;
                s.Fade = 0f;
                s.FadeDuration = 0f;
            }
            else s.From = layer.Names[s.FromIndex];
        }
        a.Params = values;
        a.Layers = layers;
        a.Version = g.Version;
    }

    // Resolves the graph's clips and layer masks against the instance's model, when either changed.
    public static void ResolveClips(World world, Entity entity, in Animator a, AnimGraphRecord.Compiled g, AnimatorPoses.Instance instance)
    {
        if (instance.ResolvedVersion == g.Version && (instance.Set == null || instance.Clips.Length == g.ClipNames.Length)) return;
        instance.ResolvedVersion = g.Version;
        var set = instance.Set;
        instance.Clips = new AnimationClip?[g.ClipNames.Length];
        instance.Masks = new JointMask?[g.Layers.Length];
        if (set == null)
        {
            // A sprite's graph (issue #119): its leaves are the sheet's clips, which have no joints.
            if (instance.Sheet is not { } sheet) return;
            for (int c = 0; c < g.ClipNames.Length; c++)
            {
                instance.Clips[c] = SpriteClips.Find(sheet, g.ClipNames[c]);
                if (instance.Clips[c] == null)
                    Log.Once(LogCat.Animation, LogLevel.Warn, $"anim-sprite-clip:{a.Graph}:{g.ClipNames[c]}",
                        $"Animator {a.Graph} at {World.Describe(entity)}: its sprite sheet has no clip '{g.ClipNames[c]}'; states that play it hold still");
            }
            return;
        }
        for (int c = 0; c < g.ClipNames.Length; c++)
        {
            // A clip the model has not got may come from a clip library's rig through a skeleton_map (#360).
            instance.Clips[c] = set.FindClip(g.ClipNames[c]) ?? SkeletonMaps.Find(world, a.Model, set, g.ClipNames[c]);
            if (instance.Clips[c] == null)
                Log.Once(LogCat.Animation, LogLevel.Warn, $"anim-clip:{a.Graph}:{a.Model}:{g.ClipNames[c]}",
                    $"Animator {a.Graph} at {World.Describe(entity)}: {set.Source} has no clip '{g.ClipNames[c]}'; states that play it stand at rest");
        }
        for (int l = 1; l < g.Layers.Length; l++)
        {
            var names = g.Layers[l].Mask;
            if (names == null || names.Count == 0) continue;            // the whole body
            var mask = new JointMask(set.Skeleton);
            foreach (var joint in names)
                if (!string.IsNullOrEmpty(joint) && !mask.SetBranch(joint, 1f))
                    Log.Once(LogCat.Animation, LogLevel.Warn, $"anim-mask:{a.Graph}:{a.Model}:{joint}",
                        $"Animator {a.Graph}: layer '{g.Layers[l].Name}' masks joint '{joint}', which {set.Source}'s skeleton has not got");
            instance.Masks[l] = mask;
        }
    }

    // ---- logic: every tick, whatever the LOD ---------------------------------------------------------

    // Params from the body, then every layer's clock (raising the clip events it crosses), then every
    // layer's transitions. A transition's `then` goes on `then` (run by the caller once no component is
    // held by ref: actions may change the world).
    //
    // A state entered by a transition plays the tick it is entered on (issue #119): its clip advances by
    // this tick's dt at once and raises the events that crosses, as a clip started by Play before the
    // step does — so a trigger set in Gameplay and a clip played there start on the same tick. `Time`
    // (what `after` reads) and the cross-fade still start at 0, as #118 has them.
    public static void Step(World world, Entity entity, ref Animator a, AnimGraphRecord.Compiled g, AnimatorPoses.Instance instance, float dt,
                            List<(Entity, StateTransition)>? then, IAnimationEventSink? sink = null, bool drive = true)
    {
        Resolve(entity, ref a, g);
        var values = a.Params!;
        if (drive && g.HasSources) Drive(world, entity, g, values, instance, dt);
        var events = new EventContext(world, entity, g, values, instance, sink, dt);

        for (int l = 0; l < g.Layers.Length; l++)
        {
            var layer = g.Layers[l];
            ref var s = ref a.Layers![l];
            if (s.Index < 0) continue;
            bool raise = l == 0 || layer.Weight > 0f;

            s.Time += dt;
            var state = layer.States[s.Index];
            float before = s.Phase;
            bool entered = s.Entered;
            s.Entered = false;
            s.Phase = Advance(state, before, dt, values, instance);
            if (raise) Raise(in events, state, before, s.Phase, entered, pending: false);
            if (s.FromSnapshot)
            {
                // A frozen pose (PlayFrom): only the fade's clock moves, and it raises nothing.
                s.Fade += dt;
                if (s.Fade + Epsilon >= s.FadeDuration) EndFade(ref s);
            }
            else if (s.From != null)
            {
                var from = layer.States[s.FromIndex];
                float fromBefore = s.FromPhase;
                s.FromPhase = Advance(from, fromBefore, dt, values, instance);
                s.Fade += dt;
                if (s.Fade + Epsilon >= s.FadeDuration) EndFade(ref s);
                // The state being left raises its events while it still shows more than the one entered.
                else if (raise && Easing.Apply(s.FadeEase, s.Fade / s.FadeDuration) < 0.5f)
                    Raise(in events, from, fromBefore, s.FromPhase, entered: false, pending: false);
            }
        }

        var context = new ConditionContext(world, entity, entity);
        for (int l = 0; l < g.Layers.Length; l++)
        {
            var layer = g.Layers[l];
            ref var s = ref a.Layers![l];
            if (s.Index < 0) continue;

            StateTransition? taken = null;
            for (int p = 0; p < values.Length && taken == null; p++)
                if (g.ParamKinds[p] == AnimParamKind.Trigger && values[p].Value == TriggerSet)
                    taken = Pick(layer, s.Index, g.ParamNames[p], s.Time, in context);
            taken ??= Pick(layer, s.Index, null, s.Time, in context);
            if (taken == null) continue;
            Change(g, layer, ref s, layer.Target(taken));
            if (s.Index >= 0)
            {
                var entered = layer.States[s.Index];
                s.Entered = false;
                s.Phase = Advance(entered, 0f, dt, values, instance);
                if (l == 0 || layer.Weight > 0f) Raise(in events, entered, 0f, s.Phase, entered: true, pending: true);
            }
            if (then != null && taken.Then is { Count: > 0 }) then.Add((entity, taken));
        }

        // Triggers are used up by the tick after they were set, taken or not; one an event set while a
        // state was being entered, after the transitions had been tried, is there for the next tick.
        for (int p = 0; p < values.Length; p++)
            if (g.ParamKinds[p] == AnimParamKind.Trigger) values[p].Value = values[p].Value == TriggerPending ? TriggerSet : 0f;
    }

    // A trigger's value: set (a transition may take it this tick), or set by an event after this tick's
    // transitions were tried (it is set from the next one).
    private const float TriggerSet = 1f;
    private const float TriggerPending = 2f;

    // What raising a clip event needs, gathered once a step.
    private readonly struct EventContext
    {
        public readonly World World;
        public readonly Entity Entity;
        public readonly AnimGraphRecord.Compiled Graph;
        public readonly AnimatorParam[] Values;
        public readonly AnimatorPoses.Instance Instance;
        public readonly IAnimationEventSink? Sink;
        public readonly float Dt;

        public EventContext(World world, Entity entity, AnimGraphRecord.Compiled graph, AnimatorParam[] values,
                            AnimatorPoses.Instance instance, IAnimationEventSink? sink, float dt)
        {
            Dt = dt;
            World = world;
            Entity = entity;
            Graph = graph;
            Values = values;
            Instance = instance;
            Sink = sink;
        }
    }

    // Raises the events of the clip `state` shows (a blend: its heaviest clip) that the phase crossed
    // going from `before` to `after`, in the order it crossed them: before < t <= after, and from the very
    // start (0 <= t) when the state was just entered. A loop that went round (the phase came down)
    // crossed before..1, then 0..after: a pass's end and the next one's start, once each. A one-shot
    // parked at its end crosses nothing, so each event fires once a pass. Allocation-free.
    //
    // Both ends are moved on by a twentieth of a tick (`slack`, in this clip's phase), so an event that
    // falls exactly on a tick — 0.25 s at 60 Hz — is raised on that tick whichever way float rounding
    // leaves the accumulated phase, rather than a tick later now and then. Every tick is moved alike, so
    // the ranges still meet end to end and no event is raised twice or missed.
    private static void Raise(in EventContext e, AnimGraphRecord.State state, float before, float after, bool entered, bool pending)
    {
        int c = HeaviestClip(state, e.Values);
        var clips = e.Instance.Clips;
        if (c < 0 || c >= clips.Length || clips[c] is not { } clip) return;
        var list = clip.Events;
        if (list.Count == 0) return;
        float duration = clip.Duration;
        float slack = duration > 0f ? SlackTicks * e.Dt * MathF.Abs(state.Speed) / duration : 0f;
        if (state.Speed >= 0f)
        {
            if (state.Loop && after < before)
            {
                Forward(in e, list, duration, before + slack, false, 2f, pending);
                Forward(in e, list, duration, 0f, true, after + slack, pending);
            }
            else Forward(in e, list, duration, entered ? 0f : before + slack, entered, after + slack, pending);
        }
        else
        {
            if (state.Loop && after > before)
            {
                Backward(in e, list, duration, before - slack, false, -1f, pending);
                Backward(in e, list, duration, 1f, true, after - slack, pending);
            }
            else Backward(in e, list, duration, entered ? 1f : before - slack, entered, after - slack, pending);
        }
    }

    // The fraction of a tick an event may be early by and still count as reached (see Raise, Advance).
    private const float SlackTicks = 0.05f;

    // Events with from < t <= to (from <= t when `inclusive`), in time order.
    private static void Forward(in EventContext e, IReadOnlyList<ClipEvent> list, float duration, float from, bool inclusive, float to, bool pending)
    {
        for (int i = 0; i < list.Count; i++)
        {
            float t = Phase(list[i], duration);
            if (t < 0f) continue;
            if ((inclusive ? t >= from : t > from) && t <= to) Fire(in e, list[i].Name, pending);
        }
    }

    // Playing backwards: events with to <= t < from (t <= from when `inclusive`), latest first.
    private static void Backward(in EventContext e, IReadOnlyList<ClipEvent> list, float duration, float from, bool inclusive, float to, bool pending)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            float t = Phase(list[i], duration);
            if (t < 0f) continue;
            if ((inclusive ? t <= from : t < from) && t >= to) Fire(in e, list[i].Name, pending);
        }
    }

    // An event's place in its clip's phase; -1 for one past the clip's end (never reached).
    private static float Phase(in ClipEvent ev, float duration)
    {
        if (!(duration > 0f)) return 0f;
        if (ev.Time > duration) return -1f;
        return MathF.Max(0f, ev.Time / duration);
    }

    private static void Fire(in EventContext e, string name, bool pending)
    {
        e.Sink?.Raise(e.World, e.Entity, name);
        e.World.FireOutput(e.Entity, Animators.AnimEventOutput, e.Entity, name);
        // Events drive transitions: one sets the trigger param of its name, if the graph has one.
        int p = e.Graph.ParamIndex(name);
        if (p >= 0 && e.Graph.ParamKinds[p] == AnimParamKind.Trigger && e.Values[p].Value != TriggerSet)
            e.Values[p].Value = pending ? TriggerPending : TriggerSet;
    }

    // The clip a state shows most: its clip, or the heaviest point of its blend at the params now (the
    // first of equals). -1 for a state that plays nothing.
    internal static int HeaviestClip(AnimGraphRecord.State state, AnimatorParam[] values)
    {
        if (state.Clip >= 0) return state.Clip;
        if (!state.IsBlend) return -1;
        Span<float> weights = stackalloc float[Animators.MaxBlendPoints];
        int n = Weights(state, values, weights);
        int best = -1;
        for (int i = 0; i < n; i++)
            if (best < 0 || weights[i] > weights[best]) best = i;
        return best < 0 ? -1 : state.PointClips[best];
    }

    private static StateTransition? Pick(AnimGraphRecord.Layer layer, int current, string? input, float time, in ConditionContext context)
    {
        var own = StateMachines.FirstTransition(layer.States[current].Source.Transitions, input, time, in context);
        if (own != null && layer.Target(own) >= 0) return own;
        var any = layer.Any;
        for (int i = 0; i < any.Count; i++)
        {
            if (any[i] is not { } t) continue;
            int to = layer.Target(t);
            if (to < 0 || to == current) continue;
            // FirstTransition over one: the same rules (on, after, when) as the state's own.
            if (StateMachines.FirstTransition(Single(t), input, time, in context) != null) return t;
        }
        return null;
    }

    [ThreadStatic] private static StateTransition[]? _one;

    private static StateTransition[] Single(StateTransition t)
    {
        var one = _one ??= new StateTransition[1];
        one[0] = t;
        return one;
    }

    // Goes to state `to`, cross-fading from the one it is in over the new state's fade. A change while
    // already fading fades from the state it was going to (the older one drops out).
    public static void Change(AnimGraphRecord.Compiled g, AnimGraphRecord.Layer layer, ref AnimatorLayer s, int to)
    {
        if (to < 0) return;
        var target = layer.States[to];
        if (target.Fade > 0f && s.Index >= 0)
        {
            s.FromSnapshot = false;                 // a fade from a snapshot drops out like an older state
            s.From = s.State;
            s.FromIndex = s.Index;
            s.FromPhase = s.Phase;
            s.Fade = 0f;
            s.FadeDuration = target.Fade;
            s.FadeEase = target.Ease;
        }
        else EndFade(ref s);
        s.State = layer.Names[to];
        s.Index = to;
        s.Time = 0f;
        s.Phase = 0f;
        s.Entered = true;
    }

    internal static void EndFade(ref AnimatorLayer s)
    {
        s.From = null;
        s.FromSnapshot = false;
        s.FromIndex = -1;
        s.FromPhase = 0f;
        s.Fade = 0f;
        s.FadeDuration = 0f;
    }

    // The params that read the body: velocity (the character controller's, else how far the transform
    // moved since last tick) and the pawn's intent.
    private static void Drive(World world, Entity entity, AnimGraphRecord.Compiled g, AnimatorParam[] values, AnimatorPoses.Instance instance, float dt)
    {
        Vector3 velocity = default;
        bool grounded = false, crouching = false;
        Vector3 position = world.TryGet<GlobalTransform>(entity, out var global) ? global.Current.Position
                         : world.TryGet<Transform>(entity, out var local) ? local.LocalPosition : default;
        if (world.TryGet<CharacterController>(entity, out var character))
        {
            velocity = character.Velocity;
            grounded = character.Grounded;
            crouching = character.Crouching;
        }
        else if (instance.HasLastPosition && dt > 0f) velocity = (position - instance.LastPosition) / dt;
        instance.LastPosition = position;
        instance.HasLastPosition = true;
        world.TryGet<PawnIntent>(entity, out var intent);

        for (int p = 0; p < values.Length; p++)
        {
            float v;
            switch (g.ParamSources[p])
            {
                case AnimParamSource.Speed: v = MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z); break;
                case AnimParamSource.VerticalSpeed: v = velocity.Y; break;
                case AnimParamSource.MoveX: v = intent.Move.X; break;
                case AnimParamSource.MoveY: v = intent.Move.Y; break;
                case AnimParamSource.AimPitch: v = intent.Pitch * RadToDeg; break;
                case AnimParamSource.Grounded: v = grounded ? 1f : 0f; break;
                case AnimParamSource.Crouching: v = crouching ? 1f : 0f; break;
                default: continue;
            }
            if (!float.IsFinite(v)) v = 0f;
            values[p].Value = g.ParamKinds[p] == AnimParamKind.Float ? v : v != 0f ? 1f : 0f;
        }
    }

    // A state's phase after dt: its rate is its speed over its (weighted) clip duration. A loop coming
    // forward to within a twentieth of a tick of its end wraps on this tick (issue #119: float rounding
    // must not put a pass's end, and its events, a tick late).
    private static float Advance(AnimGraphRecord.State state, float phase, float dt, AnimatorParam[] values, AnimatorPoses.Instance instance)
    {
        float duration = Duration(state, values, instance);
        if (!(duration > 0f)) return phase;
        float step = dt * state.Speed / duration;
        float p = phase + step;
        if (state.Loop)
        {
            float slack = step > 0f ? SlackTicks * step : 0f;
            return MathF.Max(0f, p - MathF.Floor(p + slack));
        }
        return Math.Clamp(p, 0f, 1f);
    }

    private static float Duration(AnimGraphRecord.State state, AnimatorParam[] values, AnimatorPoses.Instance instance)
    {
        var clips = instance.Clips;
        if (state.Clip >= 0) return state.Clip < clips.Length && clips[state.Clip] is { } clip ? clip.Duration : 0f;
        if (!state.IsBlend) return 0f;
        Span<float> weights = stackalloc float[Animators.MaxBlendPoints];
        int n = Weights(state, values, weights);
        float duration = 0f, total = 0f;
        for (int i = 0; i < n; i++)
        {
            int c = state.PointClips[i];
            if (weights[i] <= 0f || c >= clips.Length || clips[c] is not { } clip) continue;
            duration += weights[i] * clip.Duration;
            total += weights[i];
        }
        return total > 0f ? duration / total : 0f;
    }

    // What the state is made of at the params now: one weight per point of a blend (1D: the two points
    // around x, linearly; 2D: inverse-distance-squared over every point, exact at a point), or 1 for a
    // clip. The count written; 0 for a state that plays nothing.
    public static int Weights(AnimGraphRecord.State state, AnimatorParam[] values, Span<float> weights)
    {
        if (state.Clip >= 0)
        {
            weights[0] = 1f;
            return 1;
        }
        int n = state.PointClips.Length;
        if (n == 0) return 0;
        weights[..n].Clear();
        float x = state.ParamX >= 0 && state.ParamX < values.Length ? values[state.ParamX].Value : 0f;
        if (!float.IsFinite(x)) x = 0f;
        var px = state.PointX;
        if (!state.TwoD)
        {
            if (n == 1 || x <= px[0]) weights[0] = 1f;
            else if (x >= px[n - 1]) weights[n - 1] = 1f;
            else
            {
                int k = 0;
                while (k < n - 2 && px[k + 1] <= x) k++;
                float span = px[k + 1] - px[k];
                float t = span > 0f ? (x - px[k]) / span : 0f;
                weights[k] = 1f - t;
                weights[k + 1] = t;
            }
            return n;
        }

        float y = state.ParamY >= 0 && state.ParamY < values.Length ? values[state.ParamY].Value : 0f;
        if (!float.IsFinite(y)) y = 0f;
        var py = state.PointY;
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float dx = x - px[i], dy = y - py[i];
            float d2 = dx * dx + dy * dy;
            if (d2 < 1e-8f)
            {
                weights[..n].Clear();
                weights[i] = 1f;
                return n;
            }
            weights[i] = 1f / d2;
            sum += weights[i];
        }
        for (int i = 0; i < n; i++) weights[i] /= sum;
        return n;
    }

    // ---- sampling: at the LOD's rate ------------------------------------------------------------------

    // Every layer into the instance's Sampled pose (the system copies it into the readers' pose every tick).
    public static void Sample(in Animator a, AnimGraphRecord.Compiled g, AnimatorPoses.Instance instance, AnimatorScratch scratch)
    {
        var pose = instance.Sampled!;
        var values = a.Params!;
        for (int l = 0; l < g.Layers.Length; l++)
        {
            var layer = g.Layers[l];
            ref readonly var s = ref a.Layers![l];
            if (l == 0)
            {
                float w = Layer(layer, l, in s, values, instance, pose, scratch);
                if (w <= 0f) pose.ResetToRest();
                else if (w < 1f)
                {
                    scratch.From.ResetToRest();
                    PoseSampler.Blend(scratch.From, pose, w, null, pose);
                }
                continue;
            }
            float weight = layer.Weight * Layer(layer, l, in s, values, instance, scratch.Layer, scratch);
            if (weight > 0f) PoseSampler.Blend(pose, scratch.Layer, weight, instance.Masks[l], pose);
        }
    }

    // One layer into `into`: its state, cross-faded from the one it is leaving. Returns how much of the
    // layer shows (1, or less while fading to or from a state that plays nothing). A fade from a pose
    // snapshot (PlayFrom) blends from that frozen pose as from a state.
    private static float Layer(AnimGraphRecord.Layer layer, int index, in AnimatorLayer s, AnimatorParam[] values, AnimatorPoses.Instance instance,
                               SkeletonPose into, AnimatorScratch scratch)
    {
        bool current = s.Index >= 0 && State(layer.States[s.Index], s.Phase, values, instance, into, scratch.Clip);
        if (s.FromSnapshot)
        {
            if (index >= instance.Snapshots.Length || instance.Snapshots[index] is not { } snapshot || snapshot.JointCount != into.JointCount)
                return current ? 1f : 0f;
            float g = Easing.Apply(s.FadeEase, s.FadeDuration > 0f ? s.Fade / s.FadeDuration : 1f);
            if (current) PoseSampler.Blend(snapshot, into, g, null, into);
            else snapshot.Local.CopyTo(into.Local);
            return current ? 1f : 1f - g;
        }
        if (s.From == null || s.FromIndex < 0) return current ? 1f : 0f;
        float f = Easing.Apply(s.FadeEase, s.FadeDuration > 0f ? s.Fade / s.FadeDuration : 1f);
        var from = layer.States[s.FromIndex];
        if (current)
        {
            if (!State(from, s.FromPhase, values, instance, scratch.From, scratch.Clip)) return f;
            PoseSampler.Blend(scratch.From, into, f, null, into);
            return 1f;
        }
        return State(from, s.FromPhase, values, instance, into, scratch.Clip) ? 1f - f : 0f;
    }

    // A state at a phase into `into` (a blend's other clips through `clip`). False when it plays nothing.
    private static bool State(AnimGraphRecord.State state, float phase, AnimatorParam[] values, AnimatorPoses.Instance instance,
                              SkeletonPose into, SkeletonPose clip)
    {
        var clips = instance.Clips;
        if (state.Clip >= 0)
        {
            if (state.Clip >= clips.Length || clips[state.Clip] is not { } c) return false;
            PoseSampler.Sample(c, phase * c.Duration, state.Loop, into);
            return true;
        }
        if (!state.IsBlend) return false;
        Span<float> weights = stackalloc float[Animators.MaxBlendPoints];
        int n = Weights(state, values, weights);
        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            int index = state.PointClips[i];
            if (weights[i] <= 0f || index >= clips.Length || clips[index] is not { } c) continue;
            float time = phase * c.Duration;
            if (total <= 0f)
            {
                PoseSampler.Sample(c, time, state.Loop, into);
                total = weights[i];
                continue;
            }
            PoseSampler.Sample(c, time, state.Loop, clip);
            total += weights[i];
            PoseSampler.Blend(into, clip, weights[i] / total, null, into);
        }
        return total > 0f;
    }
}

// Phase.Animation (issue #118): every animator steps its params, clocks and transitions each tick, then
// is sampled into its pose — every tick near the main camera, less often far from it (animation LOD,
// anim_lod_distance). Every tick the readers' pose is rewritten from the last sample and registered in
// #120's SkeletonPoses (which passes it to #117's SkinPoses), and Removed before it goes back to the
// pool; `aim_pitch`/`aim_yaw` params (degrees) go to the entity's AimIk (radians). Runs after sprite
// animation; #120's foot IK, aim IK and attachments adjust the pose in Phase.Late. A suspended animator
// (Animators.Suspend, #244) is skipped: whoever suspended it writes the pose.
// Animators are gathered in the query and stepped after it, because a transition's `then` may change
// the world.
[Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
[System(Animators.SystemId, Phase.Animation, After = new[] { "?sage.animation.sprites" })]
internal sealed class AnimatorSystem : ISystem
{
    private const float DegToRad = MathF.PI / 180f;

    private readonly World _world;
    private readonly Query<Animator> _animators;
    private readonly AnimatorPoses _poses;
    private readonly SkeletonPoses _registered;
    private readonly GltfAnimationReader? _reader;
    private readonly RecordStore? _records;
    private readonly CVar<float>? _lodDistance;
    private readonly bool _drive;   // params read from the body (false: an editor's preview sets them all, #362)
    private readonly List<Entity> _step = new();
    private readonly List<(Entity, StateTransition)> _then = new();
    private readonly Dictionary<Skeleton, AnimatorScratch> _scratch = new(ReferenceEqualityComparer.Instance);

    public AnimatorSystem(World world, bool driveParams = true)
    {
        _drive = driveParams;
        _world = world;
        _animators = world.Query<Animator>();
        _poses = world.Resources.GetOrAdd(static () => new AnimatorPoses());
        // #120's seam (the engine installs it over #117's SkinPoses): IK, attachments and skinning read it.
        _registered = world.Resources.GetOrAdd(() => new SkeletonPoses(world.Resources.GetOrAdd(static () => new SkinPoses())));
        world.Resources.TryGet(out _reader);
        world.Resources.TryGet(out _records);
        _lodDistance = world.Engine?.CVars.Find(Animators.LodDistanceCVar) as CVar<float>;
    }

    public void Run(in SystemContext ctx)
    {
        _step.Clear();
        foreach (var (_, entities) in _animators.Chunks)
            for (int i = 0; i < entities.Length; i++)
                _step.Add(entities.EntityAt(i));

        float dt = ctx.Tick.Dt;
        long tick = _world.Tick;
        _world.Resources.TryGet<IAnimationEventSink>(out var sink);   // Sage.Gameplay's: sends AnimationEvent
        bool haveCamera = false;
        Vector3 camera = default;
        float lod = _lodDistance?.Value ?? 0f;
        if (lod > 0f && _world.Resources.TryGet<CameraViews>(out var views) && views != null && views.TryGetMain(out var main))
        {
            haveCamera = true;
            camera = main.Position;
        }

        for (int i = 0; i < _step.Count; i++)
        {
            var entity = _step[i];
            if (!_world.IsAlive(entity) || !_world.Has<Animator>(entity)) continue;
            ref var a = ref _world.Get<Animator>(entity);
            var instance = _poses.Acquire(ref a, entity);
            instance.Seen = tick;
            if (Animators.Compiled(_world, a.Graph) is not { } g)
            {
                Log.Once(LogCat.Animation, LogLevel.Warn, $"anim-graph:{a.Graph}",
                    $"No anim_graph record {a.Graph} (removed by a reload, or a typo); animators that run it stand still");
                continue;
            }

            Model(ref a, entity, instance);
            AnimatorStepper.ResolveClips(_world, entity, in a, g, instance);
            if (a.Suspended)
            {
                // Frozen (Animators.Suspend, issue #244): whoever suspended it rewrites the pose. Only a pose
                // it has never had (a load, a model that just arrived) is sampled once from where it stands,
                // so it is not shown at rest, and registered.
                if (instance.Pose != null && instance.SampledTick < 0)
                {
                    AnimatorStepper.Resolve(entity, ref a, g);
                    instance.SampledTick = tick;
                    AnimatorStepper.Sample(in a, g, instance, Scratch(instance.Pose.Skeleton));
                    instance.Sampled!.Local.CopyTo(instance.Pose.Local);
                    PoseSampler.ToModelSpace(instance.Pose.Skeleton, instance.Pose);
                    _registered.Set(entity, instance.Pose, a.Model);
                }
                continue;
            }
            AnimatorStepper.Step(_world, entity, ref a, g, instance, dt, _then, sink, _drive);
            // A snapshot whose fade ended (or was replaced by a state's) goes back to the pool.
            for (int l = 0; l < instance.Snapshots.Length; l++)
                if (instance.Snapshots[l] != null && (l >= a.Layers!.Length || !a.Layers[l].FromSnapshot)) _poses.ReturnSnapshots(instance, l);
            if ((g.AimPitchParam >= 0 || g.AimYawParam >= 0) && _world.Has<AimIk>(entity))
            {
                ref var aim = ref _world.Get<AimIk>(entity);
                if (g.AimPitchParam >= 0) aim.Pitch = a.Params![g.AimPitchParam].Value * DegToRad;
                if (g.AimYawParam >= 0) aim.Yaw = a.Params![g.AimYawParam].Value * DegToRad;
            }

            if (instance.Pose == null) continue;
            // First-person arms (#121) are in the camera's own space, so their distance means nothing: never LOD.
            instance.Interval = haveCamera && !entity.Tags.Has<ViewmodelLayer>() && _world.TryGet<GlobalTransform>(entity, out var at)
                ? Interval(Vector3.Distance(camera, at.Current.Position), lod) : 1;
            if (instance.SampledTick < 0 || tick - instance.SampledTick >= instance.Interval)
            {
                instance.SampledTick = tick;
                AnimatorStepper.Sample(in a, g, instance, Scratch(instance.Pose.Skeleton));
            }
            // Every tick, sampled or not: the readers' pose is rewritten from the graph's output, so what a
            // post-process (#120's IK) did to it last tick never compounds.
            instance.Sampled!.Local.CopyTo(instance.Pose.Local);
            PoseSampler.ToModelSpace(instance.Pose.Skeleton, instance.Pose);
            _registered.Set(entity, instance.Pose, a.Model);    // an overwrite: no allocation
        }
        _step.Clear();
        _poses.Sweep(tick, _registered);

        for (int i = 0; i < _then.Count; i++)
        {
            var (entity, transition) = _then[i];
            if (_world.IsAlive(entity)) Conditions.Run(transition.Then, new ActionContext(_world, entity, entity));
        }
        _then.Clear();
    }

    // The sample interval at `distance` from the camera: every tick within `lod` metres, every 2nd
    // within twice it, every 4th past that.
    internal static int Interval(float distance, float lod)
    {
        if (!(lod > 0f) || !(distance > lod)) return 1;
        return distance > 2f * lod ? 4 : 2;
    }

    // The instance's model: from the reader's cache (a tick never reads a file), except the first time
    // an animator with no set asks — a load in a new session, which spawned nothing through the part.
    private void Model(ref Animator a, Entity entity, AnimatorPoses.Instance instance)
    {
        if (a.Model.IsEmpty)
        {
            Sheet(entity, instance);
            return;
        }
        if (_reader == null) return;
        if (!_reader.TryGet(a.Model, out var set))
        {
            if (instance.Set != null || instance.LoadTried) return;
            instance.LoadTried = true;                                // a failure warns once, and is not asked again
            set = _reader.Load(a.Model);
            if (set == null) return;
        }
        if (ReferenceEquals(set, instance.Set)) return;
        _poses.Release(instance, _registered);
        instance.Set = set;
        instance.Pose = new SkeletonPose(set.Skeleton);
        instance.Sampled = new SkeletonPose(set.Skeleton);
        instance.ResolvedVersion = 0;
        instance.SampledTick = -1;
    }

    // An animator with no model on a sprite (issue #119) plays its sheet's clips: resolved again when
    // the sheet changes or a records reload gives it new clips. Allocation-free when neither did.
    private void Sheet(Entity entity, AnimatorPoses.Instance instance)
    {
        SpriteSheetRecord? sheet = null;
        // (Sprite sheets are the client's records: a headless world without them draws no sprite to play.)
        if (_records != null && _world.TryGet<SpriteRenderer>(entity, out var sprite) && !sprite.Sheet.IsEmpty
            && _records.TypeNameOf(typeof(SpriteSheetRecord)) != null && _records.TryGet(sprite.Sheet, out SpriteSheetRecord found))
            sheet = found;
        object? clips = sheet?.Animations;
        if (ReferenceEquals(sheet, instance.Sheet) && ReferenceEquals(clips, instance.SheetClips)) return;
        instance.Sheet = sheet;
        instance.SheetClips = clips;
        instance.ResolvedVersion = 0;
    }

    private AnimatorScratch Scratch(Skeleton skeleton)
    {
        if (!_scratch.TryGetValue(skeleton, out var scratch)) _scratch.Add(skeleton, scratch = new AnimatorScratch(skeleton));
        return scratch;
    }
}
