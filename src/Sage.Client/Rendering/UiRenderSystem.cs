#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Overlay phase (docs/design/13 §3): draws the renderer's Overlay stage (issue 4h-1): the game's UI
// (the `sage:ui` pass: whatever was queued into UiDraw this frame, plus the crosshair) and any pass a
// game orders around it; then clears the queue. After the world and before the dev UI.
// Only the screen world's UI is drawn (`Renderer.ScreenWorld`, issue #77): another world's queue is
// emptied unseen, the way its views into the screen are never made.
[System("sage.client.ui", Phase.Overlay)]
internal sealed class UiRenderSystem : ISystem
{
    private readonly UiDraw _ui;
    private readonly RenderSnapshot _snapshot;
    private readonly Renderer _renderer;

    public UiRenderSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        _ui = world.Resources.Get<UiDraw>();
        _snapshot = world.Resources.Get<RenderSnapshot>();
    }

    public void Run(in SystemContext ctx)
    {
        if (_renderer.IsScreenWorld(ctx.World)) _renderer.DrawOverlay(ctx.World, _snapshot);
        _ui.Clear();
    }
}

// What every world's UI shares: one SpriteBatch and one white pixel, owned by ClientModule and
// disposed with it.
internal sealed class UiResources : IDisposable
{
    public SpriteBatch Batch { get; }
    public Texture2D White { get; }

    // What a clipped stretch of the queue is drawn with (UiDraw.PushClip): the scissor test on.
    public RasterizerState Scissor { get; } = new() { CullMode = CullMode.None, ScissorTestEnable = true };

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
        Scissor.Dispose();
    }
}
