using System.Numerics;

namespace Sandbox;

// The Sandbox's terrain (docs/design/14 §3): rolling hills from a few sine waves plus value noise,
// so the slice has ground to stand on. A real game would generate from its world map (and later
// blend in authored overrides). The scene sits in the middle of the sector.
public sealed class HillsGenerator : ITerrainGenerator
{
    public void Generate(SectorCoord sector, Heightfield heights, int seed)
    {
        var origin = sector.Origin(Terrain.SectorSize);
        float spacing = heights.Spacing;

        for (int z = 0; z < heights.Resolution; z++)
        {
            for (int x = 0; x < heights.Resolution; x++)
            {
                // World position of this vertex, so neighbouring sectors line up at their edges.
                float wx = origin.X + x * spacing, wz = origin.Z + z * spacing;
                // Wavelengths of roughly 300 m and 120 m: hills you can see across one sector.
                float height =
                    11f * MathF.Sin(wx * 0.021f + seed * 0.37f) * MathF.Cos(wz * 0.018f) +
                    4f * MathF.Sin(wx * 0.052f + 1.3f) * MathF.Sin(wz * 0.047f + 0.6f) +
                    1.5f * Noise(wx * 0.08f, wz * 0.08f, seed);

                // A flat clearing around the scene, rising into hills just beyond it.
                float dx = wx - SceneCenter.X, dz = wz - SceneCenter.Z;
                float distance = MathF.Sqrt(dx * dx + dz * dz);
                height *= Smooth((distance - 12f) / 45f);

                heights[x, z] = height;
            }
        }
    }

    public static readonly Vector3 SceneCenter = new(Terrain.SectorSize * 0.5f, 0, Terrain.SectorSize * 0.5f);

    // 0 near the scene, 1 further out: keeps the play area flat-ish without a visible seam.
    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // Cheap value noise: hash the grid corners and interpolate.
    private static float Noise(float x, float z, int seed)
    {
        int x0 = (int)MathF.Floor(x), z0 = (int)MathF.Floor(z);
        float tx = x - x0, tz = z - z0;
        float h00 = Hash(x0, z0, seed), h10 = Hash(x0 + 1, z0, seed);
        float h01 = Hash(x0, z0 + 1, seed), h11 = Hash(x0 + 1, z0 + 1, seed);
        tx = tx * tx * (3f - 2f * tx);
        tz = tz * tz * (3f - 2f * tz);
        return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz;
    }

    private static float Hash(int x, int z, int seed)
    {
        uint h = (uint)(x * 374761393 + z * 668265263 + seed * 1013904223);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xFFFF) / 32768f - 1f;   // -1..1
    }
}
