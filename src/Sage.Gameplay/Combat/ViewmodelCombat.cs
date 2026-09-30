#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// The first-person arms follow the fight (issue #121, docs/design/12 "As built (first-person arms)").
//
// Gameplay phase, after the swing: for every pawn with a Melee and a camera with a Viewmodel (the
// player's), the camera shows the `arms` of the attack in the pawn's hands — bare hands' when a weapon
// comes off — and the arms' anim_graph hears about the fight: a swing that started this tick sets its
// `attack` trigger, and the Reload button its `reload` trigger (gameplay_conventions `animations` name
// both). Reload only plays: ammunition is phase 4e's, and a game's. A graph without the trigger ignores it.
//
// Allocation-free: two short loops over a handful of entities.
[System(Id, Phase.Gameplay, After = new[] { "sage.combat.melee" })]
internal sealed class ViewmodelCombatSystem : ISystem
{
    public const string Id = "sage.combat.viewmodel";

    private readonly World _world;
    private readonly RecordStore _records;
    private readonly Query<PawnIntent, Melee> _fighters;
    private readonly Query<Viewmodel> _cameras;
    private readonly ActionId _reload;

    public ViewmodelCombatSystem(World world, RecordStore records, ActionRegistry actions)
    {
        _world = world;
        _records = records;
        _fighters = world.Query<PawnIntent, Melee>();
        _cameras = world.Query<Viewmodel>();
        _reload = actions.Get(world.Conventions().Actions.Reload);
    }

    public void Run(in SystemContext ctx)
    {
        var conventions = _world.Conventions();
        foreach (var (intents, melees, entities) in _fighters.Chunks)
        {
            var intent = intents.Span;
            var melee = melees.Span;
            for (int n = 0; n < melee.Length; n++)
            {
                var camera = Viewmodels.CameraOf(_world, _cameras, entities.EntityAt(n));
                if (camera.IsNull) continue;

                var attackId = melee[n].Attack.IsEmpty ? conventions.Attack.Id : melee[n].Attack;
                var arms = !attackId.IsEmpty && _records.TryGet(attackId, out AttackRecord attack) ? attack.Arms.Id : default;
                ref var viewmodel = ref _world.Get<Viewmodel>(camera);
                if (viewmodel.Record != arms) viewmodel.Record = arms;   // ViewmodelSystem respawns in Animation

                var shown = Viewmodels.ArmsOf(_world, camera);
                if (shown.IsNull || !_world.Has<Animator>(shown)) continue;
                // The swing began this tick: MeleeCombatSystem (just before) moved it to Windup at 0.
                if (melee[n].Phase == MeleePhase.Windup && melee[n].Timer == 0f && !melee[n].Swung)
                    Animators.SetTrigger(_world, shown, conventions.Animations.Attack);
                if (_reload.IsValid && intent[n].Pressed.Has(_reload))
                    Animators.SetTrigger(_world, shown, conventions.Animations.Reload);
            }
        }
    }
}

// **A stand-in for #119** (clip events), kept to the viewmodel's arms: Animation phase, after the
// animator, it sends an AnimationEvent (the sprite animator's, which combat already reads) for each of
// the arms' clip events (AnimationClip.Events) that the base layer's clip crossed this tick — `mag_out`
// and `mag_in` of a reload, in order, once each. It watches the public Animator (state and normalised
// phase) rather than hooking the stepper, so #119's AnimatorStepper events replace it by deleting this
// class: the two must not both run, or each event would arrive twice.
[System(Id, Phase.Animation, After = new[] { Animators.SystemId })]
internal sealed class ViewmodelEventSystem : ISystem
{
    public const string Id = "sage.combat.viewmodel_events";

    private readonly World _world;
    private readonly RecordStore _records;
    private readonly Query<Viewmodel> _cameras;
    private readonly GameEvents _events;
    private readonly Dictionary<int, Seen> _seen = new();   // by arms entity id; a handful of entries

    private struct Seen
    {
        public Entity Arms;
        public string? State;
        public float Phase;
        public long Tick;
    }

    public ViewmodelEventSystem(World world, RecordStore records)
    {
        _world = world;
        _records = records;
        _cameras = world.Query<Viewmodel>();
        _events = world.Events;
    }

    public void Run(in SystemContext ctx)
    {
        long tick = _world.Tick;
        float dt = ctx.Tick.Dt;
        foreach (var (_, cameras) in _cameras.Chunks)
            for (int i = 0; i < cameras.Length; i++)
            {
                var arms = Viewmodels.ArmsOf(_world, cameras.EntityAt(i));
                if (!arms.IsNull) Watch(arms, tick, dt);
            }
        // Forget arms that are gone (a respawn) so their ids can be reused cleanly.
        if (_seen.Count > 0) Forget(tick);
    }

    private void Watch(Entity arms, long tick, float dt)
    {
        if (!_world.TryGet<Animator>(arms, out var animator) || animator.Layers is not { Length: > 0 } layers) return;
        var layer = layers[0];
        _seen.TryGetValue(arms.Id, out var seen);
        bool known = seen.Arms == arms && seen.Tick >= 0 && seen.Tick != tick;
        _seen[arms.Id] = new Seen { Arms = arms, State = layer.State, Phase = layer.Phase, Tick = tick };
        if (!known || !Clips(animator, out var graph, out var set)) return;

        if (string.Equals(seen.State, layer.State, StringComparison.Ordinal))
        {
            Crossed(arms, graph, set, layer.State, seen.Phase, layer.Phase, entering: false);
            return;
        }
        // It changed state this tick: the old state's clip ran on from where it was to where it stopped
        // (the phase a cross-fade keeps, or one tick further), then the new one from its start.
        float end = layer.Fading && string.Equals(layer.From, seen.State, StringComparison.Ordinal)
            ? layer.FromPhase : Advance(graph, set, seen.State, seen.Phase, dt);
        Crossed(arms, graph, set, seen.State, seen.Phase, end, entering: false);
        Crossed(arms, graph, set, layer.State, 0f, layer.Phase, entering: true);
    }

    private bool Clips(in Animator animator, out AnimGraphRecord graph, out AnimationSet set)
    {
        set = null!;
        graph = null!;
        if (!_records.TryGet(animator.Graph, out graph)) return false;
        return _world.Resources.TryGet<GltfAnimationReader>(out var reader) && reader != null
            && reader.TryGet(animator.Model, out set!) && set != null;
    }

    private static AnimationClip? ClipOf(AnimGraphRecord graph, AnimationSet set, string? state, out bool loop)
    {
        loop = true;
        if (state == null || !graph.States.TryGetValue(state, out var s) || s == null || string.IsNullOrEmpty(s.Clip)) return null;
        loop = s.Loop;
        return set.FindClip(s.Clip);
    }

    private static float Advance(AnimGraphRecord graph, AnimationSet set, string? state, float phase, float dt)
    {
        var clip = ClipOf(graph, set, state, out bool loop);
        if (clip == null || !(clip.Duration > 0f)) return phase;
        float speed = state != null && graph.States.TryGetValue(state, out var s) && s != null ? s.Speed : 1f;
        float p = phase + dt * speed / clip.Duration;
        return loop ? p - MathF.Floor(p) : Math.Clamp(p, 0f, 1f);
    }

    // Every event of the state's clip in (from, to] of its normalised phase — [0, to] on entering — and
    // across the end when a loop wrapped.
    private void Crossed(Entity arms, AnimGraphRecord graph, AnimationSet set, string? state, float from, float to, bool entering)
    {
        var clip = ClipOf(graph, set, state, out _);
        if (clip == null || clip.Events.Count == 0 || !(clip.Duration > 0f)) return;
        if (to >= from)
        {
            Send(arms, clip, from, to, entering);
            return;
        }
        Send(arms, clip, from, 1f, entering);   // wrapped: to the end, then from the start
        Send(arms, clip, 0f, to, true);
    }

    private void Send(Entity arms, AnimationClip clip, float from, float to, bool inclusiveFrom)
    {
        var events = clip.Events;
        for (int i = 0; i < events.Count; i++)
        {
            float at = events[i].Time / clip.Duration;
            if ((inclusiveFrom ? at >= from : at > from) && at <= to) _events.Send(new AnimationEvent(arms, events[i].Name));
        }
    }

    private List<int>? _gone;

    private void Forget(long tick)
    {
        foreach (var (id, seen) in _seen)
            if (seen.Tick != tick) (_gone ??= new List<int>()).Add(id);
        if (_gone == null || _gone.Count == 0) return;
        foreach (var id in _gone) _seen.Remove(id);
        _gone.Clear();
    }
}
