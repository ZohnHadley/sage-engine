#nullable enable
using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// One instanced mesh draw's per-instance data (issue 4n-5): the camera-relative world matrix, a row per
// element, read by lit.fx's instanced vertex shaders as TEXCOORD4..7 (the mesh's own UV is TEXCOORD0).
[StructLayout(LayoutKind.Sequential)]
internal struct InstanceTransform : IVertexType
{
    public Vector4 Row0, Row1, Row2, Row3;

    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 4),
        new VertexElement(16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 5),
        new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 6),
        new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 7));

    public void Set(in Matrix m)
    {
        Row0 = new Vector4(m.M11, m.M12, m.M13, m.M14);
        Row1 = new Vector4(m.M21, m.M22, m.M23, m.M24);
        Row2 = new Vector4(m.M31, m.M32, m.M33, m.M34);
        Row3 = new Vector4(m.M41, m.M42, m.M43, m.M44);
    }

    readonly VertexDeclaration IVertexType.VertexDeclaration => Declaration;
}

// A dynamic vertex buffer of per-instance data, written a run at a time behind the previous runs
// (NoOverwrite) and started again (Discard) when the next run does not fit, so the GPU never waits on
// data a draw earlier in the frame still reads.
internal sealed class InstanceStream<T> : IDisposable where T : struct, IVertexType
{
    private readonly DynamicVertexBuffer _buffer;
    private readonly int _stride;
    private int _cursor;

    public InstanceStream(GraphicsDevice device, VertexDeclaration declaration, int capacity)
    {
        Capacity = capacity;
        Data = new T[capacity];
        _stride = declaration.VertexStride;
        _buffer = new DynamicVertexBuffer(device, declaration, capacity, BufferUsage.WriteOnly);
    }

    public int Capacity { get; }

    // What the caller fills, from 0, before `Upload`.
    public T[] Data { get; }

    public VertexBuffer Buffer => _buffer;

    // Data[0, count) into the buffer; returns the vertex offset the draw's binding starts at.
    public int Upload(int count)
    {
        var options = SetDataOptions.NoOverwrite;
        if (_cursor + count > Capacity) { _cursor = 0; options = SetDataOptions.Discard; }
        int at = _cursor;
        _buffer.SetData(at * _stride, Data, 0, count, _stride, options);
        _cursor += count;
        return at;
    }

    public void Dispose() => _buffer.Dispose();
}
