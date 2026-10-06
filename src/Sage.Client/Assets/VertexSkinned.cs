#nullable enable
using System;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;

namespace Sage.Client;

// Models are read headlessly now (issue #302): `MeshGeometry` in the simulation reads a `.glb` (R12's runtime
// glTF loading, which lived here as GltfLoader) or its cooked `.sgmesh`, into vertex structs laid out byte for
// byte as the ones below, and the renderer hands those arrays to its vertex buffers as they are.
// `VertexLayouts.Check` says so at startup rather than letting a drift draw garbage.
internal static class VertexLayouts
{
    public static void Check()
    {
        if (Marshal.SizeOf<MeshVertex>() != VertexMesh.VertexDeclaration.VertexStride
            || Marshal.SizeOf<SkinnedMeshVertex>() != VertexSkinned.VertexDeclaration.VertexStride)
            throw new InvalidOperationException("MeshVertex/SkinnedMeshVertex no longer match VertexMesh/VertexSkinned");
    }
}

// A model's rigid vertex (issue #410): VertexPositionNormalTexture's three elements, then a tangent (w: the
// bitangent's sign) for lit.fx's normal maps and a colour for a material's `vertexColors`. A mesh built
// without them (a brush, a box made at run time) is still drawn by lit.fx: GL reads a missing element as
// 0 0 0 1, which the shader takes as "no tangent" (the normal as it is) and ignores unless vertexColors.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct VertexMesh : IVertexType
{
    public readonly Vector3 Position;
    public readonly Vector3 Normal;
    public readonly Vector2 TextureCoordinate;
    public readonly Vector4 Tangent;
    public readonly Color Color;

    public VertexMesh(Vector3 position, Vector3 normal, Vector2 uv, Vector4 tangent, Color color)
    {
        Position = position;
        Normal = normal;
        TextureCoordinate = uv;
        Tangent = tangent;
        Color = color;
    }

    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.Tangent, 0),
        new VertexElement(48, VertexElementFormat.Color, VertexElementUsage.Color, 0));

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
}

// A skinned vertex (issue #117): lit.fx's `Skinned` technique reads BLENDINDICES0 as four joint
// indices into the draw's palette and BLENDWEIGHT0 as their weights; then the tangent and colour, as
// VertexMesh's (issue #410). The first three elements are VertexPositionNormalTexture's, so an effect
// that does not skin (a material without a `Skinned` technique) still draws it, in its bind pose.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct VertexSkinned : IVertexType
{
    public readonly Vector3 Position;
    public readonly Vector3 Normal;
    public readonly Vector2 TextureCoordinate;
    public readonly Byte4 BlendIndices;
    public readonly Vector4 BlendWeights;
    public readonly Vector4 Tangent;
    public readonly Color Color;

    public VertexSkinned(Vector3 position, Vector3 normal, Vector2 uv, Byte4 indices, Vector4 weights, Vector4 tangent, Color color)
    {
        Position = position;
        Normal = normal;
        TextureCoordinate = uv;
        BlendIndices = indices;
        BlendWeights = weights;
        Tangent = tangent;
        Color = color;
    }

    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Byte4, VertexElementUsage.BlendIndices, 0),
        new VertexElement(36, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0),
        new VertexElement(52, VertexElementFormat.Vector4, VertexElementUsage.Tangent, 0),
        new VertexElement(68, VertexElementFormat.Color, VertexElementUsage.Color, 0));

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
}
