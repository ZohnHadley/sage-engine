#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// The sky (issue 4h-5): the engine's Sky-stage pass. Per view, after the opaque and alpha-tested runs and
// before the transparent one, one full-screen triangle at the far plane with the depth test on and depth
// writes off, so it colours only what nothing opaque covered: a gradient from the horizon colour
// (`ClearColor`) to `Zenith`, hazed to the fog colour along the horizon, the sun disc, and stars as the
// sun goes down. `shaders/sky.fx` draws it; what it draws is `SkyRules.ColorAt` and `SkyRules.StarsAt`,
// headless and tested.
//
// Drawn only while `RenderEnvironment.DrawSky` — the sky system turns it on for a world with a sky record
// — so a world without one keeps the clear colour, exactly as before. Not in a depth-only view (the
// viewmodel's, which is drawn over a finished picture), and into whatever target the view is bound to.
[RenderPass(Id, RenderStage.Sky)]
internal sealed class SkyPass : IRenderPass
{
    public const string Id = "sage:sky";

    public void Draw(RenderContext context) => context.Renderer.DrawSky(context);
}

// The sky's effect and its one triangle. The effect is asked for every frame, not held: a hot reload of
// `sky.fx` swaps it (the font's reason, in UiPass), and its parameters are looked up again when it does.
internal sealed class SkyDome : IDisposable
{
    private static readonly AssetPath EffectPath = AssetPath.Intern("shaders/sky.mgfxo");

    private readonly GraphicsDevice _device;
    private readonly VertexBuffer _triangle;
    private Effect? _effect;
    private EffectParameter? _invViewProj, _horizon, _zenith, _fogColor, _sunDir, _sunColor, _params, _disc;
    private EffectParameter? _haze, _starAxis, _moon, _moonLight, _cloud, _cloudColor, _moonTex, _cloudTex;
    private ContentService? _content;

    public SkyDome(GraphicsDevice device)
    {
        _device = device;
        // One triangle over the whole viewport (clip x and y from -1 to 3), at the far plane (z = w).
        _triangle = new VertexBuffer(device, VertexPosition.VertexDeclaration, 3, BufferUsage.WriteOnly);
        _triangle.SetData(new[]
        {
            new VertexPosition(new Vector3(-1f, -1f, 1f)),
            new VertexPosition(new Vector3(3f, -1f, 1f)),
            new VertexPosition(new Vector3(-1f, 3f, 1f)),
        });
    }

    public bool Ready(ContentService content)
    {
        var effect = content.LoadEffect(EffectPath);
        if (effect == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "sky-effect", $"{EffectPath} is missing: the sky is its clear colour");
            return false;
        }
        if (!ReferenceEquals(effect, _effect))
        {
            _effect = effect;
            EffectParameter? P(string name) => effect.Parameters[name];
            _invViewProj = P("SkyInvViewProj"); _horizon = P("SkyHorizon"); _zenith = P("SkyZenith");
            _fogColor = P("SkyFog"); _sunDir = P("SkySunDir"); _sunColor = P("SkySunColor");
            _params = P("SkyParams"); _disc = P("SkyDisc");
            _haze = P("SkyHaze"); _starAxis = P("SkyStarAxis"); _moon = P("SkyMoon"); _moonLight = P("SkyMoonLight");
            _cloud = P("SkyCloud"); _cloudColor = P("SkyCloudColor"); _moonTex = P("MoonTex"); _cloudTex = P("CloudTex");
        }
        _content = content;
        return true;
    }

    // The sky behind what the view drew. Returns the draw calls made.
    public int Draw(in RenderView view, in EnvironmentParams env)
    {
        if (_effect == null) return 0;
        _invViewProj?.SetValue(Matrix.Invert(view.ViewProj));
        _horizon?.SetValue(env.ClearColor);
        _zenith?.SetValue(env.Zenith);
        _fogColor?.SetValue(env.FogColor);
        _sunDir?.SetValue(env.SunDirection);
        _sunColor?.SetValue(env.SunColor);
        // x: stars (0-1), y: the horizon hazes to the fog colour (fog on), z: the haze band, w: time
        _params?.SetValue(new Vector4(env.Stars, env.FogParams.Z, SkyRules.HazeBand, env.Time));
        _disc?.SetValue(new Vector4(SkyRules.SunDiscOuter, SkyRules.SunDiscInner, SkyRules.SunDiscGain, SkyRules.StarsOut));

        // Issue 4n-16: the haze, the turning stars, the moon and the clouds. A picture that is not named, or
        // not loaded, switches its part off (a moon of radius 0, clouds of no cover).
        _haze?.SetValue(new Vector4(env.HazeBand, env.HazeAbove, env.StarTurn, 0f));
        _starAxis?.SetValue(env.StarAxis);
        var moon = !env.MoonTexture.IsEmpty ? _content?.LoadTexture(env.MoonTexture) : null;
        _moonTex?.SetValue(moon);
        _moon?.SetValue(new Vector4(env.MoonDirection, moon != null ? env.MoonSize : 0f));
        _moonLight?.SetValue(new Vector4(env.MoonAge, env.MoonLevel, SkyRules.MoonGain, 0f));
        var clouds = !env.CloudTexture.IsEmpty ? _content?.LoadTexture(env.CloudTexture) : null;
        _cloudTex?.SetValue(clouds);
        _cloud?.SetValue(new Vector4(clouds != null ? env.CloudCover : 0f, env.CloudScale, env.CloudScroll.X, env.CloudScroll.Y));
        _cloudColor?.SetValue(env.CloudColor);

        _device.BlendState = BlendState.Opaque;
        _device.DepthStencilState = DepthStencilState.DepthRead;   // behind everything drawn; writes nothing
        _device.RasterizerState = RasterizerState.CullNone;
        _device.SetVertexBuffer(_triangle);
        int draws = 0;
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawPrimitives(PrimitiveType.TriangleList, 0, 1);
            draws++;
        }
        return draws;
    }

    public void Dispose() => _triangle.Dispose();
}
