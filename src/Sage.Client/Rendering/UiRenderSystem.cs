#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// Overlay phase (docs/design/13 §3): draws whatever was queued into UiDraw this frame, plus the
// crosshair, and clears the queue. One SpriteBatch, one pass, after the world and before the dev UI.
internal sealed class UiRenderSystem : ISystem
{
    private readonly UiDraw _ui;
    private readonly GraphicsDevice _device;
    private readonly ContentService _content;
    private readonly ActiveCamera _camera;
    private readonly UiResources _shared;
    private readonly CVar<bool> _crosshair;
    private bool _fontTried;

    // The batch and the white pixel are the module's: a system is built per world, and two worlds
    // would mean two of each with nothing to dispose them (review #57's lesson, applied early).
    public UiRenderSystem(World world, ClientHost host, ContentService content, UiResources shared, CVar<bool> crosshair)
    {
        _ui = world.Resources.Get<UiDraw>();
        _device = host.GraphicsDevice;
        _content = content;
        _camera = world.Resources.Get<ActiveCamera>();
        _shared = shared;
        _crosshair = crosshair;
    }

    public void Run(in SystemContext ctx)
    {
        // The font is content like anything else, so it is loaded on the first frame that wants it
        // rather than at boot, and a missing one costs the text, not the HUD (13 §3).
        if (!_fontTried)
        {
            _fontTried = true;
            var font = _content.LoadFont(AssetPath.Intern("fonts/ui"));
            _ui.SetFont(font);
            if (font == null) Log.Warn(LogCat.Render, "fonts/ui is missing: the HUD draws its bars but no text");
        }

        var viewport = _device.Viewport;
        _ui.Size = new Vector2(viewport.Width, viewport.Height);

        // The crosshair is the engine's one piece of HUD: combat and the Use action both aim from the
        // centre of the screen, so not drawing it is a handicap rather than a style. With a screen
        // open there is nothing to aim at, and a cross floating over an inventory looks like a bug.
        bool screenOpen = ctx.World.Resources.TryGet<ScreenStack>(out var screens) && screens!.IsOpen;
        if (_crosshair.Value && _camera.DrivenByRig && !screenOpen)
        {
            const float Arm = 6f, Thickness = 2f;
            float x = viewport.Width * 0.5f, y = viewport.Height * 0.5f;
            var colour = new Color(255, 255, 255, 150);
            _ui.Rect(x - Arm, y - Thickness * 0.5f, Arm * 2f, Thickness, colour);
            _ui.Rect(x - Thickness * 0.5f, y - Arm, Thickness, Arm * 2f, colour);
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
