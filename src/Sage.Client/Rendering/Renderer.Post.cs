#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Post-processing (issue 4h-6; HDR, bloom, anti-aliasing and the depth hook, issue #316): the renderer's
// half of the chain. PostChain decides what is on and PostChainPlan (headless) which steps a frame
// draws; this declares the targets and draws them.
public sealed partial class Renderer
{
    // What the device can draw (issue #316): probed once, and lowered when a target comes out less than
    // asked (a format that would not make, fewer samples). PostChainPlan.Fallback turns these into
    // what the frame draws and the one warning.
    private bool? _hdrSupported;
    private int _maxSamples = 8;

    // Plans this frame's chain (PostChain, PostChainPlan): true when it is on, with `sage:scene` declared
    // at the render scale's size (colour and depth: the views draw into it as into the screen; half-float
    // with r_hdr, multisampled with r_aa msaa) and the ping-pong pair and the bloom chain beside it when a
    // step writes them.
    private bool PreparePost()
    {
        _hdrSupported ??= QueryHdr();
        if (!_post.Prepare(TargetSize(RenderViewPlan.Screen), _hdrSupported.Value, _maxSamples, _waterWanted)) return false;
        var options = _post.Options;
        _sceneSize = _post.SceneSize;
        var format = options.Hdr ? SurfaceFormat.HdrBlendable : SurfaceFormat.Color;
        _sceneTarget = _targets.Declare(PostChainPlan.SceneTarget, _sceneSize.X, _sceneSize.Y, format, DepthFormat.Depth24, options.Samples);
        if (!MakeSceneTarget(options)) return PreparePost();   // fell back: plan again with what the device has

        foreach (var step in _post.Steps)
        {
            if (step.Destination is PostTarget.Post0 or PostTarget.Post1)
                _targets.Declare(PostChainPlan.TargetName(step.Destination), _sceneSize.X, _sceneSize.Y, SurfaceFormat.Color, DepthFormat.None);
            else if (PostChainPlan.IsBloom(step.Destination))
            {
                var (w, h) = PostChainPlan.BloomSize(_sceneSize.X, _sceneSize.Y, step.Destination - PostTarget.Bloom0);
                _targets.Declare(PostChainPlan.TargetName(step.Destination), w, h, format, DepthFormat.None);
            }
        }
        return true;
    }

    // Whether the adapter can render into half-float targets (SurfaceFormat.HdrBlendable).
    private bool QueryHdr()
    {
        bool exact = _device.Adapter.QueryRenderTargetFormat(_device.GraphicsProfile, SurfaceFormat.HdrBlendable, DepthFormat.Depth24, 0,
                                                             out var selected, out _, out _);
        return exact || selected == SurfaceFormat.HdrBlendable;
    }

    // Makes `sage:scene` now, so a format or sample count the device refuses is found before the views
    // draw: false when it fell back (the capability is lowered and the next plan warns once).
    private bool MakeSceneTarget(in PostOptions options)
    {
        RenderTarget2D texture;
        try { texture = _targets.Texture(_sceneTarget); }
        catch (Exception ex) when (options.Hdr && ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            Log.Debug(LogCat.Render, $"sage:scene would not make as HdrBlendable: {ex.Message}");
            _hdrSupported = false;
            return false;
        }
        if (options.Samples > 0 && texture.MultiSampleCount < options.Samples)
        {
            _maxSamples = texture.MultiSampleCount;
            return false;
        }
        return true;
    }

    private int PostTargetId(PostTarget target) =>
        target == PostTarget.Scene ? _sceneTarget : _targets.Id(PostChainPlan.TargetName(target));

    // The r_stats line's options (issue #316).
    private string PostOptionStats()
    {
        var o = _post.Options;
        return (o.Hdr ? ", hdr" : "") + (o.Bloom ? $", bloom {o.BloomLevels}" : "")
             + (o.Aa == AntiAliasing.Fxaa ? ", fxaa" : o.Msaa ? $", msaa {o.Samples}x" : "")
             + (o.Water ? ", water" : "") + (_post.NeedsDepth ? ", depth" : "");
    }

    // ---- The depth hook (issue #316) ----

    // The scene's depth for the effects that declare `depth`: every screen view's opaque and alpha-tested
    // items drawn again into `sage:depth` (R32F, the scene's size, each view in its own rectangle) with
    // lit.fx's ShadowCaster technique, which writes z/w; cleared to 1, the sky. A depth-only view (the
    // viewmodel) clears depth in its rectangle first, as it does when it draws, so the hands are in front.
    // Alpha-tested holes are solid here. Called before the views draw (soft particles read it while they do,
    // issue 4n-6) with `_toScene` set; without the chain (soft particles only) it is the back buffer's size.
    // False when it could not be drawn (no default material or caster: a build without shaders).
    private bool DrawSceneDepth(RenderSnapshot s)
    {
        var size = _toScene ? _sceneSize : TargetSize(RenderViewPlan.Screen);
        int target = _targets.Declare(PostChainPlan.DepthTarget, size.X, size.Y, SurfaceFormat.Single, DepthFormat.Depth24);
        _device.SetRenderTarget(_targets.Texture(target));
        _device.Viewport = new Viewport(0, 0, size.X, size.Y);
        _device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Vector4.One, 1f, 0);
        _bound = NotBound;
        _passBound = false;
        _current = -1;

        // A default material that could not be built (no shaders: Linux CI) is already reported, under Shaders.
        var material = Materials.Get(Materials.Resolve(MaterialRecord.Default));
        if (material == null || material.IsError) return false;
        var caster = material.Effect;
        if (caster.ShadowCaster == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "no-depth-caster", $"{MaterialRecord.Default}'s effect has no ShadowCaster technique; post effects and soft particles read no depth");
            return false;
        }
        var effect = caster.Effect;
        _device.BlendState = BlendState.Opaque;
        _device.DepthStencilState = DepthStencilState.Default;
        ulong opaque = (ulong)RenderStages.SortKeyPass(RenderStage.Opaque);
        ulong alphaTested = (ulong)RenderStages.SortKeyPass(RenderStage.AlphaTested);

        for (int v = 0; v < s.Views.Count; v++)
        {
            ref var view = ref s.Views[s.DrawOrder[v]];
            if (view.Target != RenderViewPlan.Screen || view.ShadowCaster) continue;
            var rect = ViewRect(view);
            _device.Viewport = new Viewport(rect);
            if (view.DepthOnly)
            {
                _device.ScissorRectangle = rect;
                _device.RasterizerState = ScissorClear;
                _device.Clear(ClearOptions.DepthBuffer, Vector4.One, 1f, 0);
            }
            _device.RasterizerState = RasterizerState.CullNone;
            caster.ViewProj?.SetValue(view.ViewProj);

            for (int k = view.ItemStart; k < view.ItemStart + view.ItemCount; k++)
            {
                ulong pass = s.SortKeys[k] >> 60;
                if (pass != opaque && pass != alphaTested) continue;
                ref var item = ref s.Items[s.Order[k]];
                var technique = caster.ShadowCaster;
                if (item.BoneCount > 0 && caster.ShadowCasterSkinned != null && caster.Bones != null)
                {
                    var palette = s.Bones.AsSpan().Slice(item.BoneStart, item.BoneCount);
                    for (int i = 0; i < palette.Length; i++) _bones[i] = palette[i];   // System.Numerics → MonoGame (implicit)
                    caster.Bones.SetValue(_bones);
                    technique = caster.ShadowCasterSkinned;
                }
                caster.World?.SetValue(item.World);
                effect.CurrentTechnique = technique;
                var part = Mesh(item.Mesh).Parts[item.Part];
                _device.SetVertexBuffer(VertexBufferOf(item, part));
                _device.Indices = part.IndexBuffer;
                foreach (var p in technique.Passes)
                {
                    p.Apply();
                    _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, part.VertexOffset, part.StartIndex, part.PrimitiveCount);
                    _stats.DrawCalls++;
                    _stats.Triangles += part.PrimitiveCount;
                }
            }
        }
        caster.FrameStamp = -1;   // the next view sets its own frame parameters on this effect
        _current = -1;
        return true;
    }

    // ---- Drawing the chain ----

    // A full-screen quad, already in clip space: what post.fx's vertex shader passes through.
    private static readonly VertexPositionTexture[] PostQuad =
    {
        new(new Vector3(-1f, 1f, 0f), new Vector2(0f, 0f)),
        new(new Vector3(1f, 1f, 0f), new Vector2(1f, 0f)),
        new(new Vector3(-1f, -1f, 0f), new Vector2(0f, 1f)),
        new(new Vector3(1f, -1f, 0f), new Vector2(1f, 1f)),
    };

    // `sage:post`: the frame's chain, a full-screen draw a step, each reading the target the one before
    // wrote and the last writing the back buffer at full size (the UI draws over it in Overlay). An
    // effect's material is drawn with its own technique, params and sampler (point by default, so a
    // render scale upscales as big pixels); the engine sets Source, SourceSize and Night, and (issue
    // #316) Bloom, BloomOn, Hdr, SceneDepth and DepthParams. The engine's steps (bloom, tonemap, FXAA)
    // draw their own materials from post.json; a step whose material did not build is a plain copy (the
    // tonemap, FXAA) or left out (the whole bloom, which then adds nothing).
    internal void DrawPostChain(RenderContext ctx)
    {
        var steps = _post.Steps;
        if (steps.Length == 0) return;
        var options = _post.Options;
        float night = _post.Night(ctx.World);
        var s = ctx.Snapshot;

        bool bloom = options.Bloom && Built(PostStepKind.BloomPrefilter) && Built(PostStepKind.BloomDown) && Built(PostStepKind.BloomUp);
        Texture2D? bloomTexture = bloom ? _targets.Texture(_targets.Id(PostChainPlan.TargetName(PostChainPlan.BloomTarget(0)))) : null;
        Texture2D? depth = _post.NeedsDepth && _targets.TryFind(PostChainPlan.DepthTarget, out int depthId) ? _targets.Existing(depthId) : null;
        var depthParams = Vector4.Zero;
        if (s.MainView >= 0)
        {
            ref var main = ref s.Views[s.MainView];
            depthParams = new Vector4(main.Near, main.Far, main.Projection.M44 == 1f ? 1f : 0f, 0f);
        }

        for (int i = 0; i < steps.Length; i++)
        {
            var step = steps[i];
            bool isBloom = step.Kind is PostStepKind.BloomPrefilter or PostStepKind.BloomDown or PostStepKind.BloomUp;
            if (isBloom && !bloom) continue;
            var source = _targets.Texture(PostTargetId(step.Source));
            Rebind(step.Destination == PostTarget.Screen ? RenderViewPlan.Screen : PostTargetId(step.Destination));
            int material = step.Kind == PostStepKind.Effect ? (step.Effect < 0 ? -1 : _post.Material(step.Effect)) : _post.StepMaterial(step.Kind);
            var m = material < 0 ? null : Materials.Get(material);
            if (m == null || m.IsError)
            {
                // A plain copy: the render scale's upscale with no effect on, or a step whose material
                // did not build (the material cache logged why) - the picture goes through unchanged.
                DrawFullScreen(source, null, null, SamplerState.PointClamp);
                continue;
            }

            MaterialCache.Apply(_device, m, wireframe: false);
            _device.BlendState = step.Kind == PostStepKind.BloomUp ? BlendState.Additive : BlendState.Opaque;
            _device.DepthStencilState = DepthStencilState.None;
            _device.RasterizerState = RasterizerState.CullNone;
            var effect = m.Effect.Effect;
            effect.Parameters["Source"]?.SetValue(source);
            effect.Parameters["SourceSize"]?.SetValue(new Vector4(source.Width, source.Height, 1f / source.Width, 1f / source.Height));
            effect.Parameters["Night"]?.SetValue(night);
            effect.Parameters["Hdr"]?.SetValue(options.Hdr ? 1f : 0f);
            // Only the tonemap reads the bloom: a bloom step must never have its own target bound to read.
            var bloomIn = step.Kind == PostStepKind.Tonemap ? bloomTexture : null;
            effect.Parameters["BloomOn"]?.SetValue(bloomIn != null ? 1f : 0f);
            effect.Parameters["Bloom"]?.SetValue(bloomIn ?? source);
            if (step.Kind == PostStepKind.Water) SetWaterParams(effect, s);   // issue #411
            if (effect.Parameters["SceneDepth"] is { } sceneDepth)
            {
                bool reads = (step.Kind == PostStepKind.Effect && step.Effect >= 0 && _post.ReadsDepth(step.Effect)) || step.Kind == PostStepKind.Water;
                if (!reads)
                    Log.Once(LogCat.Render, LogLevel.Warn, "post-depth-undeclared:" + m.Id,
                             $"Post material {m.Id} reads SceneDepth, but its post_effect does not say \"depth\": true; it reads a blank");
                sceneDepth.SetValue(reads && depth != null ? depth : Materials.MissingTexture);
                effect.Parameters["DepthParams"]?.SetValue(depthParams);
            }
            foreach (var pass in m.Technique.Passes)
            {
                pass.Apply();
                _device.DrawUserPrimitives(PrimitiveType.TriangleStrip, PostQuad, 0, 2);
                _stats.DrawCalls++;
                _stats.Triangles += 2;
            }
            if (m.DrawnFrame != _frame) { m.DrawnFrame = _frame; m.Drawn = 0; }
            m.Drawn++;
        }

        // Nothing the chain read stays bound past it: next frame draws into these targets.
        _device.Textures[0] = null;
        _device.Textures[1] = null;
        _device.Textures[2] = null;
        _current = -1;   // the chain set its own device state
    }

    // Whether one of the engine's steps has a material that built (no shaders: none does).
    private bool Built(PostStepKind kind) => _post.Built(kind);
}
