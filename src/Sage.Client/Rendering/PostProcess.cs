#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// `sage:post` (issue 4h-6): the chain of `post_effect` records, once, on the screen, after every view
// and before the UI. The renderer has already drawn the screen's views into `sage:scene` when the
// chain is on (Renderer.Draw); this reads it and ends on the back buffer.
[RenderPass("sage:post", RenderStage.PostProcess)]
internal sealed class PostProcessPass : IRenderPass
{
    public void Draw(RenderContext context) => context.Renderer.DrawPostChain(context);
}

// The client's half of post-processing: which effects the records declare and which are on this frame,
// the plan (PostChainPlan, headless), and the targets it draws through. Owned by the renderer.
internal sealed class PostChain
{
    // What a post effect's shader is given by the engine, so a material need not (and cannot) set it:
    // the picture it reads, that picture's size (w, h, 1/w, 1/h), and how far into the night the sky is.
    public static readonly HashSet<string> EngineParams = new(StringComparer.Ordinal) { "Source", "SourceSize", "Night" };

    private readonly RecordStore _records;
    private readonly CVarRegistry _cvars;
    private readonly RendererCVars _settings;
    private readonly MaterialCache _materials;

    private (RecordId Id, PostEffectRecord Effect, CVar? CVar, int Material)[] _effects = Array.Empty<(RecordId, PostEffectRecord, CVar?, int)>();
    private bool _dirty = true;
    private bool[] _enabled = Array.Empty<bool>();
    private PostStep[] _steps = new PostStep[1];

    public PostChain(RecordStore records, CVarRegistry cvars, RendererCVars settings, MaterialCache materials)
    {
        _records = records;
        _cvars = cvars;
        _settings = settings;
        _materials = materials;
        records.Reloaded += () => _dirty = true;
    }

    // This frame's steps (Prepare), and the scene's size while they draw.
    public int Count { get; private set; }
    public ReadOnlySpan<PostStep> Steps => _steps.AsSpan(0, Count);
    public Point SceneSize { get; private set; }
    public int Effects => _effects.Length;

    public int Material(int effect) => _effects[effect].Material;
    public RecordId Id(int effect) => _effects[effect].Id;

    // Plans the frame for a back buffer of `back`: true when the chain is on, and the screen's views
    // must draw into `sage:scene` at `SceneSize`. `r_post` switches the effects; `r_scale` works with or
    // without them.
    public bool Prepare(Point back)
    {
        if (_dirty) Rebuild();
        bool post = _settings.Post.Value;
        for (int i = 0; i < _effects.Length; i++)
            _enabled[i] = post && PostChainPlan.IsOn(_effects[i].CVar);
        Count = PostChainPlan.Plan(_enabled, _settings.Scale.Value, _steps);
        var (w, h) = PostChainPlan.ScaledSize(back.X, back.Y, _settings.Scale.Value);
        SceneSize = new Point(w, h);
        return Count > 0;
    }

    // How far into the night the world's sky is (0 with none): the grade's night tint.
    public float Night(World world) =>
        world.Resources.TryGet<WorldClock>(out var clock) ? PostChainPlan.Night(_records, clock) : 0f;

    private void Rebuild()
    {
        _dirty = false;
        var ordered = PostChainPlan.Ordered(_records);
        _effects = new (RecordId, PostEffectRecord, CVar?, int)[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            var (id, effect) = ordered[i];
            CVar? cvar = null;
            if (effect.Cvar.Length > 0)
            {
                cvar = _cvars.Find(effect.Cvar);
                if (cvar == null)
                    Log.Warn(LogCat.Render, $"Post effect {id}: cvar '{effect.Cvar}' is not registered; the effect is on whenever r_post is");
            }
            _effects[i] = (id, effect, cvar, _materials.Resolve(effect.Material));
        }
        _enabled = new bool[_effects.Length];
        _steps = new PostStep[Math.Max(1, _effects.Length)];
    }
}
