#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Overlay phase (docs/design/13 §3): draws whatever was queued into UiDraw this frame, plus the
// crosshair, and clears the queue. One SpriteBatch, one pass, after the world and before the dev UI.
// Only the screen world's UI is drawn (`Renderer.ScreenWorld`, issue #77): another world's queue is
// emptied unseen, the way its views into the screen are never made.
[System("sage.client.ui", Phase.Overlay)]
internal sealed class UiRenderSystem : ISystem
{
    private readonly UiDraw _ui;
    private readonly GraphicsDevice _device;
    private readonly ContentService _content;
    private readonly ActiveCamera _camera;
    private readonly UiResources _shared;
    private readonly CVar<bool> _crosshair;
    private readonly Renderer _renderer;
    private readonly CVar<int> _testView;
    private bool _fontWarned;

    // Interned once; the font itself is looked up every frame (see `Run`).
    private static readonly AssetPath FontPath = AssetPath.Intern("textures/font.png");

    // The batch and the white pixel are the module's: a system is built per world, and two worlds
    // would mean two of each with nothing to dispose them (review #57's lesson, applied early).
    public UiRenderSystem(World world, ClientHost host, ContentService content, UiResources shared, CVar<bool> crosshair,
                          Renderer renderer, CVar<int> testView)
    {
        _renderer = renderer;
        _testView = testView;
        _ui = world.Resources.Get<UiDraw>();
        _device = host.GraphicsDevice;
        _content = content;
        _camera = world.Resources.Get<ActiveCamera>();
        _shared = shared;
        _crosshair = crosshair;
    }

    public void Run(in SystemContext ctx)
    {
        if (!_renderer.IsScreenWorld(ctx.World)) { _ui.Clear(); return; }

        // The font is content like anything else, so it is loaded on the first frame that wants it
        // rather than at boot, and a missing one costs the text, not the HUD (13 §3).
        //
        // Asked for **every frame**, not held: a hot reload drops the wrapper and disposes the texture
        // under it, and a system that kept the old one would draw from a disposed texture — which GL
        // renders as black boxes where the text was, without an error anywhere (F32, R12). The ask is a
        // dictionary lookup on an interned path; the warning is the part that happens once.
        var font = _content.LoadFont(FontPath);
        _ui.SetFont(font);
        if (font == null && !_fontWarned)
        {
            _fontWarned = true;
            Log.Warn(LogCat.Render, $"{FontPath} is missing: the HUD draws its bars but no text");
        }

        var viewport = _device.Viewport;
        _ui.Size = new Vector2(viewport.Width, viewport.Height);

        // The crosshair is the engine's one piece of HUD: combat and the Use action both aim from the
        // centre of the screen, so not drawing it is a handicap rather than a style. With a screen
        // open there is nothing to aim at, and a cross floating over an inventory looks like a bug.
        bool screenOpen = ctx.World.Resources.TryGet<ScreenStack>(out var screens) && screens!.IsOpen;
#pragma warning disable CS0618   // obsolete for games (issue #76); the crosshair follows the rig until #78/#81
        bool driven = _camera.DrivenByRig;
#pragma warning restore CS0618
        if (_crosshair.Value && driven && !screenOpen)
        {
            const float Arm = 6f, Thickness = 2f;
            float x = viewport.Width * 0.5f, y = viewport.Height * 0.5f;
            var colour = new Color(255, 255, 255, 150);
            _ui.Rect(x - Arm, y - Thickness * 0.5f, Arm * 2f, Thickness, colour);
            _ui.Rect(x - Thickness * 0.5f, y - Arm, Thickness, Arm * 2f, colour);
        }

        // `r_testview 2`: the render target the top-down test view drew into, in the top-right corner.
        if (_testView.Value == 2 && _renderer.FindTarget(ViewSource.TestTarget) is { } map)
        {
            const int Size = 192, Margin = 12;
            var at = new Rectangle(viewport.Width - Size - Margin, Margin, Size, Size);
            _ui.Rect(at.X - 2, at.Y - 2, at.Width + 4, at.Height + 4, new Color(0, 0, 0, 180));
            _ui.Image(map, at, Color.White);
        }

        _shared.Batch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp);
        _ui.Draw(_shared.Batch, _shared.White);
        _shared.Batch.End();
        _ui.Clear();
    }
}

// What every world's UI shares: one SpriteBatch and one white pixel, owned by ClientModule and
// disposed with it.
internal sealed class UiResources : IDisposable
{
    public SpriteBatch Batch { get; }
    public Texture2D White { get; }

    public UiResources(GraphicsDevice device)
    {
        Batch = new SpriteBatch(device);
        White = new Texture2D(device, 1, 1);
        White.SetData(new[] { Color.White });
    }

    public void Dispose()
    {
        Batch.Dispose();
        White.Dispose();
    }
}
