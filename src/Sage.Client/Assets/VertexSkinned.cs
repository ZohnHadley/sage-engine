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
        if (Marshal.SizeOf<MeshVertex>() != VertexPositionNormalTexture.VertexDeclaration.VertexStride
            || Marshal.SizeOf<SkinnedMeshVertex>() != VertexSkinned.VertexDeclaration.VertexStride)
            throw new InvalidOperationException("MeshVertex/SkinnedMeshVertex no longer match VertexPositionNormalTexture/VertexSkinned");
    }
}

// A skinned vertex (issue #117): lit.fx's `Skinned` technique reads BLENDINDICES0 as four joint
// indices into the draw's palette and BLENDWEIGHT0 as their weights. The first three elements are
// VertexPositionNormalTexture's, so an effect that does not skin (a material without a `Skinned`
// technique) still draws it, in its bind pose.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct VertexSkinned : IVertexType
{
    public readonly Vector3 Position;
    public readonly Vector3 Normal;
    public readonly Vector2 TextureCoordinate;
    public readonly Byte4 BlendIndices;
    public readonly Vector4 BlendWeights;

    public VertexSkinned(Vector3 position, Vector3 normal, Vector2 uv, Byte4 indices, Vector4 weights)
    {
        Position = position;
        Normal = normal;
        TextureCoordinate = uv;
        BlendIndices = indices;
        BlendWeights = weights;
    }

    public static readonly VertexDeclaration VertexDeclaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
        new VertexElement(32, VertexElementFormat.Byte4, VertexElementUsage.BlendIndices, 0),
        new VertexElement(36, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0));

    VertexDeclaration IVertexType.VertexDeclaration => VertexDeclaration;
}
