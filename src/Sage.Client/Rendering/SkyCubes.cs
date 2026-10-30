#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// A painted sky's six pictures as one cube (issue #635), and the mean colour of its sky for the water, made
// once per set of pictures: a reload swaps a texture and it is made again. Null while a picture is not loaded,
// and for one the CPU cannot read back (a compressed format) or that is not one square size: said once, and
// the sky is its gradient.
internal sealed class SkyCubes : IDisposable
{
    internal sealed record Cube(TextureCube Texture, Texture2D[] Faces, Vector3 Mean);

    private readonly GraphicsDevice _device;
    private readonly ContentService _content;
    private readonly Dictionary<string, Cube> _cubes = new();

    public SkyCubes(GraphicsDevice device, ContentService content)
    {
        _device = device;
        _content = content;
    }

    public Cube? Get(SkyboxFaces? faces)
    {
        if (faces == null) return null;
        string key = $"{faces.Px}|{faces.Nx}|{faces.Py}|{faces.Ny}|{faces.Pz}|{faces.Nz}";
        var loaded = new Texture2D?[]
        {
            _content.LoadTexture(faces.Px), _content.LoadTexture(faces.Nx), _content.LoadTexture(faces.Py),
            _content.LoadTexture(faces.Ny), _content.LoadTexture(faces.Pz), _content.LoadTexture(faces.Nz),
        };
        if (_cubes.TryGetValue(key, out var cube) && Same(cube.Faces, loaded)) return cube;

        int size = loaded[0]?.Width ?? 0;
        foreach (var face in loaded)
            if (face == null || face.Width != size || face.Height != size || face.Format != SurfaceFormat.Color)
            {
                Log.Once(LogCat.Render, LogLevel.Warn, "sky-cube:" + key, $"The sky's six faces ({faces.Pz} and the rest) are not all one square, uncompressed size: the sky is its gradient");
                return null;
            }

        cube?.Texture.Dispose();
        return _cubes[key] = Build(loaded!, size);
    }

    // The faces are named as seen from inside a right-handed world, and a cube is looked up from outside a
    // left-handed one: each side is mirrored, and the top and bottom turned over, as they go in.
    private Cube Build(Texture2D[] faces, int size)
    {
        var texture = new TextureCube(_device, size, false, SurfaceFormat.Color) { Name = "(sky cube)" };
        var pixels = new Color[size * size];
        var mirrored = new Color[size * size];
        var sum = Vector3.Zero;
        int counted = 0;
        CubeMapFace[] slots =
        {
            CubeMapFace.PositiveX, CubeMapFace.NegativeX, CubeMapFace.PositiveY, CubeMapFace.NegativeY, CubeMapFace.PositiveZ, CubeMapFace.NegativeZ,
        };
        for (int f = 0; f < faces.Length; f++)
        {
            faces[f].GetData(pixels);
            bool cap = f is 2 or 3;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    mirrored[y * size + x] = cap ? pixels[(size - 1 - y) * size + x] : pixels[y * size + size - 1 - x];
            texture.SetData(slots[f], mirrored);

            // The sky is the top and the upper half of each side: as the panorama's upper half is.
            int rows = f == 2 ? size : f == 3 ? 0 : size / 2;
            for (int i = 0; i < rows * size; i++) sum += pixels[i].ToVector3();
            counted += rows * size;
        }
        return new Cube(texture, faces, sum / Math.Max(counted, 1));
    }

    private static bool Same(Texture2D[] faces, Texture2D?[] loaded)
    {
        for (int f = 0; f < faces.Length; f++)
            if (!ReferenceEquals(faces[f], loaded[f])) return false;
        return true;
    }

    public void Dispose()
    {
        foreach (var cube in _cubes.Values) cube.Texture.Dispose();
        _cubes.Clear();
    }
}
