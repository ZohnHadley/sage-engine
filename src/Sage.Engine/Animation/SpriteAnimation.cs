#nullable enable
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

// Animation phase (Fixed). Frame events (12 §3) come with the event bus (04); until then a
// non-looping clip simply holds its last frame.
public sealed class SpriteAnimationSystem : ISystem
{
    private readonly ArchetypeQuery<SpriteAnimator> _animators;

    public SpriteAnimationSystem(World world)
    {
        _animators = world.Query<SpriteAnimator>();
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;
        foreach (var (animators, _) in _animators.Chunks)
        {
            var a = animators.Span;
            for (int n = 0; n < a.Length; n++)
            {
                if (!a[n].Playing) continue;
                a[n].Time += dt * (a[n].Speed == 0f ? 1f : a[n].Speed);
            }
        }
    }
}
