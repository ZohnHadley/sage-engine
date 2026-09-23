#nullable enable
using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// One camera-facing quad in the snapshot (docs/design/06 §3.2). Positions are camera-relative; the
// quad corners are expanded by the batcher, from the view's right/up (Spherical) or world up
// (Cylindrical), so nothing in the simulation knows about the camera.
public struct SpriteInstance
{
    public Vector3 Center;        // the entity position (where the pivot sits), camera-relative
    public Vector2 Size;          // metres
    public Vector2 Pivot;         // 0..1 inside the quad, from the top-left (0.5, 1 = bottom centre)
    public Vector4 Uv;            // u0, v0, u1, v1
    public Vector4 Tint;
    public int Material;
    public int Texture;           // Renderer texture id (the sheet's)
    public BillboardMode Mode;
    public ulong SortKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpriteVertex : IVertexType
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TextureCoordinate;
    public Color Color;

    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Color, VertexElementUsage.Color, 0));

    readonly VertexDeclaration IVertexType.VertexDeclaration => Declaration;
}

// CPU-expanded billboard quads in one dynamic vertex buffer (06 §3.7): one draw per run of sprites
// that share a material and a texture. Works on every GL driver; instancing is a later experiment.
internal sealed class SpriteBatcher : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly int _maxQuads;
    private readonly DynamicVertexBuffer _vertexBuffer;
    private readonly IndexBuffer _indexBuffer;
    private SpriteVertex[] _vertices;
    private int _quads;                      // quads written into _vertices this flush
    private Vector3 _right, _up, _normal;    // camera basis for this view

    public SpriteBatcher(GraphicsDevice device, int maxQuads = 4096)
    {
        _device = device;
        _maxQuads = maxQuads;
        _vertices = new SpriteVertex[maxQuads * 4];
        _vertexBuffer = new DynamicVertexBuffer(device, SpriteVertex.Declaration, maxQuads * 4, BufferUsage.WriteOnly);

        var indices = new int[maxQuads * 6];
        for (int q = 0; q < maxQuads; q++)
        {
            int v = q * 4, i = q * 6;
            indices[i] = v; indices[i + 1] = v + 1; indices[i + 2] = v + 2;
            indices[i + 3] = v; indices[i + 4] = v + 2; indices[i + 5] = v + 3;
        }
        _indexBuffer = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
        _indexBuffer.SetData(indices);
    }

    // The camera's own basis. Sprites orient themselves per quad (see Append); these are the
    // fallbacks for the degenerate case of a sprite sitting exactly on the camera.
    public void Begin(in RenderView view)
    {
        _right = new Vector3(view.View.M11, view.View.M21, view.View.M31);
        _up = new Vector3(view.View.M12, view.View.M22, view.View.M32);
        _normal = -view.Forward;   // sprites face the camera
    }

    // Draws sprites[start..start+count) — all sharing material and texture — in as few draws as the
    // buffer allows. Returns the number of draw calls.
    public int Draw(PooledList<SpriteInstance> sprites, int[] order, int start, int count, Texture2D texture, EffectPass pass, EffectParameter? albedo)
    {
        int draws = 0;
        for (int offset = 0; offset < count; offset += _maxQuads)
        {
            int batch = Math.Min(_maxQuads, count - offset);
            _quads = 0;
            for (int i = 0; i < batch; i++)
                Append(ref sprites[order[start + offset + i]]);

            _vertexBuffer.SetData(_vertices, 0, _quads * 4, SetDataOptions.Discard);
            albedo?.SetValue(texture);
            pass.Apply();   // after the texture: Apply commits parameter values
            _device.SetVertexBuffer(_vertexBuffer);
            _device.Indices = _indexBuffer;
            _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _quads * 2);
            draws++;
        }
        return draws;
    }

    private void Append(ref SpriteInstance s)
    {
        // Each sprite turns to face the camera's *position*, not the screen plane (06 §3.8). Aligning
        // quads with the view plane instead stretches them toward the edges of a wide view — two
        // creatures the same distance apart end up drawn at different sizes — and it disagrees with
        // the direction group, which is chosen from the angle to the camera.
        //
        // Cylindrical turns about Y only, so characters and trees stay upright when the camera looks
        // up or down; Spherical faces the camera fully (effects, item pickups).
        Vector3 toCamera = -s.Center;   // positions are camera-relative, so the camera is the origin
        Vector3 right, up, normal;
        if (s.Mode == BillboardMode.Cylindrical)
        {
            var flat = new Vector3(toCamera.X, 0, toCamera.Z);
            normal = flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : FlattenedViewNormal();
            right = Vector3.Cross(Vector3.Up, normal);
            right = right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : _right;
            up = Vector3.Up;
        }
        else
        {
            normal = toCamera.LengthSquared() > 1e-8f ? Vector3.Normalize(toCamera) : _normal;
            right = Vector3.Cross(Vector3.Up, normal);
            right = right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : _right;   // straight up or down
            up = Vector3.Normalize(Vector3.Cross(normal, right));
        }

        // The pivot sits at the entity position: the quad spans left/right and up/down around it.
        float left = -s.Pivot.X * s.Size.X, rightEdge = (1f - s.Pivot.X) * s.Size.X;
        float top = s.Pivot.Y * s.Size.Y, bottom = -(1f - s.Pivot.Y) * s.Size.Y;
        var color = new Color(s.Tint.X, s.Tint.Y, s.Tint.Z, s.Tint.W);

        int v = _quads * 4;
        Set(v + 0, s.Center + right * left + up * top, normal, new Vector2(s.Uv.X, s.Uv.Y), color);
        Set(v + 1, s.Center + right * rightEdge + up * top, normal, new Vector2(s.Uv.Z, s.Uv.Y), color);
        Set(v + 2, s.Center + right * rightEdge + up * bottom, normal, new Vector2(s.Uv.Z, s.Uv.W), color);
        Set(v + 3, s.Center + right * left + up * bottom, normal, new Vector2(s.Uv.X, s.Uv.W), color);
        _quads++;
    }

    private Vector3 FlattenedViewNormal()
    {
        var flat = new Vector3(_normal.X, 0, _normal.Z);
        return flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.Backward;
    }

    private void Set(int index, Vector3 position, Vector3 normal, Vector2 uv, Color color)
    {
        ref var vertex = ref _vertices[index];
        vertex.Position = position;
        vertex.Normal = normal;
        vertex.TextureCoordinate = uv;
        vertex.Color = color;
    }

    public void Dispose()
    {
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
    }
}
