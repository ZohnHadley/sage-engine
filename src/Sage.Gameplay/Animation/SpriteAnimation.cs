#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// Sprite animation (docs/design/12 §3): the simulation owns animation time. `SpriteAnimationSystem`
// advances it in the Animation phase, at the tick rate, so animation is deterministic and the same
// on a future server. Rendering only reads the time to pick a frame (06 §3.8); it never advances it.
[Component("sage:sprite_animator")]
public struct SpriteAnimator : IComponent
{
    public int Clip;        // index into the sheet's ClipNames
    public float Time;      // seconds into the clip
    public float Speed;     // 0 or 1 = normal speed
    public bool Playing;

    public static SpriteAnimator Play(int clip, float speed = 1f) => new() { Clip = clip, Speed = speed, Playing = true };
}

// A named moment in a clip (12 §3): "the blow lands here", "a foot hits the ground here". Gameplay
// reacts to the name, so the timing of a swing lives with the art instead of being guessed at in code.
// Sent by SpriteAnimationSystem for a sprite that plays its clips itself, and — through the
// IAnimationEventSink this module installs — by the animator for every clip event its graph crosses,
// a sprite sheet's or a skinned model's (anim_events, issue #119). Raised in Phase.Animation, so a
// Gameplay reader sees it on the next tick.
[GameEvent]
public readonly record struct AnimationEvent(Entity Entity, string Name);

public static class SpriteAnimationExtensions
{
    // Starts a clip *by name* on whatever sheet the entity is wearing. By name, never by index: clip
    // 0 is whichever name sorts first, which for a Daggerfall sheet is "attack" (12 §3). Silent when
    // the entity has no sheet or the sheet has no such clip — a creature with no art still fights.
    public static void PlayClip(this World world, Entity entity, string name, RecordStore records)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!world.Has<SpriteAnimator>(entity) || !world.TryGet<SpriteRenderer>(entity, out var renderer)) return;
        if (!records.TryGet(renderer.Sheet, out SpriteSheetRecord sheet)) return;

        int clip = sheet.ClipIndex(name);
        if (clip < 0) return;
        world.Get<SpriteAnimator>(entity) = SpriteAnimator.Play(clip);
    }
}

// Where the animator's clip events go in a world with gameplay (issue #119): the AnimationEvent bus.
internal sealed class AnimationEventBus : IAnimationEventSink
{
    public void Raise(World world, Entity entity, string name) => world.Events.Send(new AnimationEvent(entity, name));
}

// anim_debug for sprites: the entity's sheet, clip, step and sheet frame (direction 0), time and speed.
// One line per sprite with a SpriteAnimator, whoever advances it (its own clip or its graph).
internal sealed class SpriteAnimDebug : IAnimDebugSource
{
    private readonly RecordStore _records;
    public SpriteAnimDebug(RecordStore records) { _records = records; }

    public void Describe(World world, string filter, List<string> lines)
    {
        foreach (var e in world.Query<SpriteAnimator, SpriteRenderer>().Entities)
        {
            string label = World.Describe(e);
            if (filter.Length > 0 && !label.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(Describe(world, e, _records));
        }
    }

    internal static string Describe(World world, Entity entity, RecordStore records)
    {
        var sb = new System.Text.StringBuilder(World.Describe(entity));
        if (!world.IsAlive(entity) || !world.TryGet<SpriteAnimator>(entity, out var a) || !world.TryGet<SpriteRenderer>(entity, out var r))
            return sb.Append(": no sprite animator").ToString();
        sb.Append(": sprite ").Append(r.Sheet);
        if (!records.TryGet(r.Sheet, out SpriteSheetRecord sheet)) return sb.Append(" (no such sprite_sheet)").ToString();
        var clip = sheet.Clip(a.Clip);
        sb.Append(", clip ").Append(clip == null ? $"#{a.Clip} (none)" : sheet.ClipNames[a.Clip]);
        if (clip != null)
        {
            int steps = clip.Dirs.Count > 0 ? clip.Dirs[0].Count : 0;
            int step = (int)MathF.Floor(MathF.Max(a.Time, 0f) * MathF.Max(clip.Fps, 0.0001f));
            step = clip.Loop && steps > 0 ? step % steps : Math.Min(step, Math.Max(steps - 1, 0));
            sb.Append(System.Globalization.CultureInfo.InvariantCulture,
                $" step {step}/{steps} (sheet frame {SpriteMath.FrameAt(clip, 0, a.Time)}), {clip.Fps:0.##} fps, {(clip.Loop ? "loops" : "once")}");
        }
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $", t={a.Time:F2}s speed {(a.Speed == 0f ? 1f : a.Speed):0.##}")
          .Append(a.Playing ? ", playing" : ", stopped");
        if (world.TryGet<Animator>(entity, out var anim)) sb.Append(", driven by graph ").Append(anim.Graph);
        return sb.ToString();
    }
}

// Animation phase (Fixed): advances every playing clip and raises the events its frames carry. A sprite
// with an animator is not its business: its graph plays it (SpriteGraphSystem).
[System("sage.animation.sprites", Phase.Animation)]
internal sealed class SpriteAnimationSystem : ISystem
{
    private const int MaxStepsPerTick = 64;   // a clip that somehow jumps a long way doesn't spin here

    private readonly Query<SpriteAnimator, SpriteRenderer> _animators;
    private readonly RecordStore _records;
    private readonly GameEvents _events;

    public SpriteAnimationSystem(World world, RecordStore records)
    {
        _animators = world.Query<SpriteAnimator, SpriteRenderer>();
        _records = records;
        _events = world.Events;
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;

        foreach (var (animators, renderers, entities) in _animators.Chunks)
        {
            var a = animators.Span;
            var r = renderers.Span;
            for (int n = 0; n < a.Length; n++)
            {
                if (!a[n].Playing || entities.EntityAt(n).HasComponent<Animator>()) continue;
                float before = a[n].Time;
                a[n].Time = before + dt * (a[n].Speed == 0f ? 1f : a[n].Speed);

                if (!_records.TryGet(r[n].Sheet, out SpriteSheetRecord sheet)) continue;
                var clip = sheet.Clip(a[n].Clip);
                if (clip == null || clip.Events.Count == 0) continue;
                Raise(entities.EntityAt(n), clip, before, a[n].Time);
            }
        }
    }

    // Fires the events of every step the clip crossed this tick. A step is an index into the clip's
    // own frame list (not the sheet's), so art can be re-cut without moving the events.
    private void Raise(Entity entity, SpriteAnimation clip, float before, float now)
    {
        int frames = clip.Dirs.Count > 0 ? clip.Dirs[0].Count : 0;
        if (frames == 0) return;

        float fps = MathF.Max(clip.Fps, 0.0001f);
        int first = before <= 0f ? -1 : (int)MathF.Floor(before * fps);   // a clip that just started is at step 0
        int last = (int)MathF.Floor(now * fps);
        if (last - first > MaxStepsPerTick) first = last - MaxStepsPerTick;

        for (int step = first + 1; step <= last; step++)
        {
            int index = clip.Loop ? step % frames : step;
            if (index >= frames) return;      // a non-looping clip has finished: it holds its last frame
            foreach (var e in clip.Events)
                if (e.Frame == index && !string.IsNullOrEmpty(e.Name))
                    _events.Send(new AnimationEvent(entity, e.Name));
        }
    }
}

// Sprites are graph leaves (issue #119): an entity with an Animator and a sprite has no model, and its
// graph's clips are its sheet's (AnimatorSystem plays them, raising their frame events). This copies what
// the graph shows — the base layer's clip, or the heaviest of its blend, and how far into it — into the
// SpriteAnimator the renderer reads, after the graph has stepped. Sprites do not cross-fade: the state
// being entered shows at once. Allocation-free.
[System("sage.animation.sprite_graph", Phase.Animation, After = new[] { "?" + Animators.SystemId })]
internal sealed class SpriteGraphSystem : ISystem
{
    private readonly World _world;
    private readonly Query<Animator, SpriteRenderer> _sprites;
    private readonly RecordStore _records;

    public SpriteGraphSystem(World world, RecordStore records)
    {
        _world = world;
        _sprites = world.Query<Animator, SpriteRenderer>();
        _records = records;
    }

    public void Run(in SystemContext ctx)
    {
        if (_records.TypeNameOf(typeof(SpriteSheetRecord)) == null) return;   // headless: the client's records
        foreach (var (animators, renderers, entities) in _sprites.Chunks)
        {
            var a = animators.Span;
            var r = renderers.Span;
            for (int n = 0; n < a.Length; n++)
            {
                if (!a[n].Model.IsEmpty) continue;                     // a skinned model's, drawn by its pose
                var entity = entities.EntityAt(n);
                if (!entity.HasComponent<SpriteAnimator>()) continue;
                if (!Animators.TryGetClip(_world, entity, out var clip, out float time)) continue;
                if (!_records.TryGet(r[n].Sheet, out SpriteSheetRecord sheet)) continue;
                int index = sheet.ClipIndex(clip);
                if (index < 0 || sheet.Clip(index) is not { } animation) continue;
                // A one-shot at its end holds its last frame, whatever the sheet says about looping.
                int frames = animation.Dirs.Count > 0 ? animation.Dirs[0].Count : 0;
                float fps = MathF.Max(animation.Fps, 0.0001f);
                if (frames > 0) time = MathF.Min(time, (frames - 0.5f) / fps);
                ref var animator = ref entity.GetComponent<SpriteAnimator>();
                animator.Clip = index;
                animator.Time = time;
                animator.Speed = 1f;
                animator.Playing = false;                              // the graph advances it, not SpriteAnimationSystem
            }
        }
    }
}
