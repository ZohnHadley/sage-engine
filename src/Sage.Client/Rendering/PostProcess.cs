#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
    // Issue #316 adds the bloom (`Bloom`, the chain's top level, and `BloomOn`, 0 when there is none), `Hdr`
    // (1 while the scene is HDR) and, for an effect that declares `depth`, `SceneDepth` and `DepthParams`.
    public static readonly HashSet<string> EngineParams = new(new[]
    {
        "Source", "SourceSize", "Night", "Bloom", "BloomOn", "Hdr", "SceneDepth", "DepthParams",
    }.Concat(WaterShading.EngineParams), StringComparer.Ordinal);   // and the water step's (issue #411)

    // The materials of the engine's own steps (engine_content/data/post.json), by PostStepKind.
    public static readonly RecordId[] StepMaterials =
    {
        default,                                  // Effect: the post_effect's own
        new("sage", "post_bloom_prefilter"),
        new("sage", "post_bloom_down"),
        new("sage", "post_bloom_up"),
        new("sage", "post_tonemap"),
        new("sage", "post_fxaa"),
        new("sage", "post_water"),                // issue #411: shaders/water.fx
    };

    private readonly RecordStore _records;
    private readonly CVarRegistry _cvars;
    private readonly RendererCVars _settings;
    private readonly MaterialCache _materials;

    private (RecordId Id, PostEffectRecord Effect, CVar? CVar, int Material)[] _effects = Array.Empty<(RecordId, PostEffectRecord, CVar?, int)>();
    private bool _dirty = true;
    private bool[] _enabled = Array.Empty<bool>();
    private bool[] _depth = Array.Empty<bool>();
    private int[] _stepMaterials = Array.Empty<int>();
    private PostStep[] _steps = new PostStep[PostChainPlan.Capacity(0)];

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

    // What this frame draws beyond the effects (issue #316): HDR, the bloom's levels and the
    // anti-aliasing, after the device's fallbacks; and whether an effect reads the scene's depth.
    public PostOptions Options { get; private set; }
    public bool NeedsDepth { get; private set; }

    public int Material(int effect) => _effects[effect].Material;
    public RecordId Id(int effect) => _effects[effect].Id;
    public bool ReadsDepth(int effect) => _depth[effect];

    // The material of one of the engine's own steps (bloom, tonemap, FXAA).
    public int StepMaterial(PostStepKind kind) => _stepMaterials[(int)kind];

    // Plans the frame for a back buffer of `back`: true when the chain is on, and the screen's views
    // must draw into `sage:scene` at `SceneSize`. `r_post` switches the effects; `r_scale`, `r_hdr`,
    // `r_bloom` and `r_aa` work with or without them. What the device cannot draw (`hdrSupported`,
    // `maxSamples`) falls back, with one warning (PostChainPlan.Fallback). `water` (issue #411): the frame
    // has water to draw, which turns the chain on by itself, like r_scale, while `sage:post_water` built.
    public bool Prepare(Point back, bool hdrSupported, int maxSamples, bool water = false)
    {
        if (_dirty) Rebuild();
        bool post = _settings.Post.Value;
        for (int i = 0; i < _effects.Length; i++)
            _enabled[i] = post && PostChainPlan.IsOn(_effects[i].CVar);
        float scale = _settings.Scale.Value;
        var (w, h) = PostChainPlan.ScaledSize(back.X, back.Y, scale);
        SceneSize = new Point(w, h);

        water = water && Built(PostStepKind.Water);
        var asked = new PostOptions(scale, _settings.Hdr.Value, _settings.Bloom.Value ? PostChainPlan.BloomLevels(w, h) : 0, _settings.Aa.Value, water);
        var options = PostChainPlan.Fallback(asked, hdrSupported, maxSamples, out string? warning);
        if (warning != null) Log.Once(LogCat.Render, LogLevel.Warn, "post-fallback:" + warning, warning);
        Options = options;
        Count = PostChainPlan.Plan(_enabled, options, _steps);
        NeedsDepth = Count > 0 && PostChainPlan.NeedsDepth(_enabled, _depth, options.Water);
        return Count > 0;
    }

    // Whether one of the engine's steps has a material that built (no shaders: none does).
    public bool Built(PostStepKind kind)
    {
        if (_dirty) Rebuild();
        var m = _materials.Get(_stepMaterials[(int)kind]);
        return m != null && !m.IsError;
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
        _depth = new bool[_effects.Length];
        for (int i = 0; i < _effects.Length; i++) _depth[i] = _effects[i].Effect.Depth;
        _steps = new PostStep[PostChainPlan.Capacity(_effects.Length)];
        _stepMaterials = new int[StepMaterials.Length];
        for (int k = 1; k < StepMaterials.Length; k++) _stepMaterials[k] = _materials.Resolve(StepMaterials[k]);
    }
}
