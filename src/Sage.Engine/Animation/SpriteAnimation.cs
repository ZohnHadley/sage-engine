#nullable enable
using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace sage_engine;

// Sprite animation (docs/design/12 §3): the simulation owns animation time. `SpriteAnimationSystem`
// advances it in the Animation phase, at the tick rate, so animation is deterministic and the same
// on a future server. Rendering only reads the time to pick a frame (06 §3.8); it never advances it.
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

// Animation phase (Fixed): advances every playing clip and raises the events its frames carry.
public sealed class SpriteAnimationSystem : ISystem
{
    private const int MaxStepsPerTick = 64;   // a clip that somehow jumps a long way doesn't spin here

    private readonly ArchetypeQuery<SpriteAnimator, SpriteRenderer> _animators;
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
                if (!a[n].Playing) continue;
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
