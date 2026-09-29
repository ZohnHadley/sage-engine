#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Draws the frame's debug lines (docs/design/06 §3.2): one dynamic vertex buffer, one draw call per
// batch, no material and no sorting — debug geometry is the one thing in the frame that is allowed
// to be simple.
internal sealed class DebugLineBatch : IDisposable
{
    private const int MaxLinesPerDraw = 8192;

    private readonly GraphicsDevice _device;
    private readonly DynamicVertexBuffer _buffer;
    private VertexPositionColor[] _vertices = new VertexPositionColor[MaxLinesPerDraw * 2];
    private Effect? _effect;
    private EffectParameter? _viewProj;

    public DebugLineBatch(GraphicsDevice device)
    {
        _device = device;
        _buffer = new DynamicVertexBuffer(device, VertexPositionColor.VertexDeclaration, MaxLinesPerDraw * 2, BufferUsage.WriteOnly);
    }

    // The effect is loaded lazily: a game with debug drawing switched off never pays for it, and a
    // missing shader is a warning rather than a dead renderer.
    public bool Ready(ContentService content)
    {
        if (_effect != null) return true;
        _effect = content.LoadEffect(AssetPath.Intern("shaders/debug.mgfxo"));
        if (_effect == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "debug-effect", "shaders/debug.mgfxo is missing: debug draw has nothing to draw with");
            return false;
        }
        _viewProj = _effect.Parameters["ViewProj"];
        return true;
    }

    // `lines` holds pairs of vertices, already camera-relative. Returns the number of draw calls.
    public int Draw(PooledList<VertexPositionColor> lines, in Matrix viewProjection, bool throughWalls)
    {
        if (_effect == null || lines.Count < 2) return 0;

        _viewProj?.SetValue(viewProjection);
        var previousDepth = _device.DepthStencilState;
        var previousBlend = _device.BlendState;
        var previousRaster = _device.RasterizerState;
        _device.DepthStencilState = throughWalls ? DepthStencilState.None : DepthStencilState.DepthRead;
        // NonPremultiplied, because a debug colour is written as it reads (0xRRGGBBAA) and nothing
        // premultiplies it on the way here.
        _device.BlendState = BlendState.NonPremultiplied;
        _device.RasterizerState = RasterizerState.CullNone;

        int draws = 0;
        int total = lines.Count - (lines.Count & 1);      // whole lines only
        for (int offset = 0; offset < total; offset += MaxLinesPerDraw * 2)
        {
            int count = Math.Min(MaxLinesPerDraw * 2, total - offset);
            for (int i = 0; i < count; i++) _vertices[i] = lines[offset + i];

            _buffer.SetData(_vertices, 0, count, SetDataOptions.Discard);
            _device.SetVertexBuffer(_buffer);
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawPrimitives(PrimitiveType.LineList, 0, count / 2);
            }
            draws++;
        }

        _device.DepthStencilState = previousDepth;
        _device.BlendState = previousBlend;
        _device.RasterizerState = previousRaster;
        return draws;
    }

    public void Dispose() => _buffer.Dispose();
}
