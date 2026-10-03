#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Ground without C# (phase 4g issue 4g-3, the 4g plan's decision 5).
//
// A generator was C# (ITerrainGenerator), so a data-only game had no ground at all. A `terrain` record
// names one of the engine's built-in generators instead, and a scene names the record (`"terrain"`): the
// world's Terrain is given that generator and seed when the scene is placed. A game's own C# generator
// is untouched — a scene that names no terrain leaves whatever the game set (the Sandbox keeps its own).
//
// The built-ins are deliberately two: `Flat` (a plane at `height`) and `Hills` (smooth value noise around
// `height`, `amplitude` up and down, one hill every `wavelength` metres, from `seed`). Both are functions
// of absolute position, so neighbouring sectors meet without a step.
[Record("terrain", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class TerrainRecord
{
    [Property(Tooltip = "The built-in generator: Flat (a plane) or Hills (smooth noise)")]
    public TerrainGeneratorKind Generator = TerrainGeneratorKind.Flat;
    [Property(Tooltip = "What the hills are made from; the same seed is the same ground")]
    public int Seed;
    [Property(Unit = "m", Tooltip = "The height of flat ground, and the middle of the hills")]
    public float Height;
    [Property(Min = 0, Max = 1000, Unit = "m", Tooltip = "How far hills rise above, and fall below, `height`")]
    public float Amplitude = 20f;
    [Property(Min = 8, Max = 100000, Unit = "m", Tooltip = "How far apart the hills are")]
    public float Wavelength = 256f;
    [RecordRef("physics_material"), Property(Tooltip = "What the ground is made of, by terrain layer: the first is everywhere a generator paints nothing else")]
    public List<RecordId> Surfaces = new();
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public enum TerrainGeneratorKind
{
    Flat,
    Hills,
}

// The generators a `terrain` record names.
internal static class BuiltInTerrain
{
    public static ITerrainGenerator Create(TerrainRecord record) => record.Generator switch
    {
        TerrainGeneratorKind.Hills => new HillsGenerator(record.Height, record.Amplitude, MathF.Max(record.Wavelength, 8f)),
        _ => new FlatGenerator(record.Height),
    };

    // Whether `generator` is the one `record` would make: placing the same scene again keeps its ground.
    public static bool Matches(ITerrainGenerator? generator, int seed, TerrainRecord record) =>
        seed == record.Seed && generator switch
        {
            FlatGenerator flat => record.Generator == TerrainGeneratorKind.Flat && flat.Height == record.Height,
            HillsGenerator hills => record.Generator == TerrainGeneratorKind.Hills && hills.Height == record.Height
                                    && hills.Amplitude == record.Amplitude && hills.Wavelength == MathF.Max(record.Wavelength, 8f),
            _ => false,
        };

    internal sealed class FlatGenerator : ITerrainGenerator, ITerrainSampler
    {
        public FlatGenerator(float height) => Height = height;
        public float Height { get; }

        public void Generate(SectorCoord sector, Heightfield heights, int seed) => Array.Fill(heights.Heights, Height);

        public float SampleHeight(double absoluteX, double absoluteZ, int seed) => Height;
    }

    // Two octaves of smoothed value noise on a lattice `wavelength` apart, in absolute metres.
    internal sealed class HillsGenerator : ITerrainGenerator, ITerrainSampler
    {
        public HillsGenerator(float height, float amplitude, float wavelength)
        {
            Height = height;
            Amplitude = amplitude;
            Wavelength = wavelength;
        }

        public float Height { get; }
        public float Amplitude { get; }
        public float Wavelength { get; }

        public void Generate(SectorCoord sector, Heightfield heights, int seed)
        {
            // In doubles, from the corner, exactly as Terrain asks SampleHeight past the edge: a vertex two
            // sectors share gets the same height from both, and so do the normals there (#277).
            var corner = sector.Origin(Terrain.SectorSize);
            double spacing = heights.Spacing;
            for (int z = 0; z < heights.Resolution; z++)
                for (int x = 0; x < heights.Resolution; x++)
                    heights[x, z] = SampleHeight(corner.X + x * spacing, corner.Z + z * spacing, seed);
        }

        public float SampleHeight(double absoluteX, double absoluteZ, int seed)
        {
            // Doubles for the lattice coordinate: a hundred kilometres out, a float would step.
            double u = absoluteX / (double)Wavelength, v = absoluteZ / (double)Wavelength;
            float n = 0.7f * Noise(u, v, seed) + 0.3f * Noise(u * 2.3, v * 2.3, seed ^ 0x5bd1e995);
            return Height + Amplitude * n;
        }

        // Smooth value noise in [-1, 1].
        private static float Noise(double u, double v, int seed)
        {
            double fu = Math.Floor(u), fv = Math.Floor(v);
            long iu = (long)fu, iv = (long)fv;
            float tu = Smooth((float)(u - fu)), tv = Smooth((float)(v - fv));
            float a = Lattice(iu, iv, seed), b = Lattice(iu + 1, iv, seed);
            float c = Lattice(iu, iv + 1, seed), d = Lattice(iu + 1, iv + 1, seed);
            return (a + (b - a) * tu) + ((c + (d - c) * tu) - (a + (b - a) * tu)) * tv;
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        // A hash of the lattice point to [-1, 1]: the same everywhere, every run.
        private static float Lattice(long x, long z, int seed)
        {
            unchecked
            {
                ulong h = (ulong)x * 0x9E3779B97F4A7C15UL ^ (ulong)z * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(uint)seed * 0x165667B19E3779F9UL;
                h ^= h >> 33;
                h *= 0xFF51AFD7ED558CCDUL;
                h ^= h >> 33;
                h *= 0xC4CEB9FE1A85EC53UL;
                h ^= h >> 33;
                return (h >> 40) / (float)(1UL << 24) * 2f - 1f;
            }
        }
    }
}
