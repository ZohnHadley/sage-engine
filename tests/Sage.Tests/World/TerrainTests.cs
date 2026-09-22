#nullable enable
using System;
using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Heightmap terrain (docs/design/14 §3, TODO F13). The heightfield and sector lookup are engine-side,
// so gameplay can stand on the ground headlessly; the chunk meshes are client-side (screenshots).
public class HeightfieldTests
{
    // A 5x5 field over 40 m (10 m spacing) with a ramp along +X: height == x metres / 10.
    private static Heightfield Ramp()
    {
        var field = new Heightfield(5, 40f);
        for (int z = 0; z < 5; z++)
            for (int x = 0; x < 5; x++)
                field[x, z] = x;
        return field;
    }

    [Fact]
    public void Spacing_AndIndexing()
    {
        var field = Ramp();
        Assert.Equal(10f, field.Spacing);
        Assert.Equal(25, field.Heights.Length);
        Assert.Equal(3f, field[3, 1]);
    }

    [Theory]
    [InlineData(0f, 0f)]       // on a vertex
    [InlineData(10f, 1f)]
    [InlineData(5f, 0.5f)]     // halfway between two vertices
    [InlineData(37.5f, 3.75f)]
    public void HeightAt_IsBilinear(float x, float expected) =>
        Assert.Equal(expected, Ramp().HeightAt(x, 13f), 4);

    [Fact]
    public void HeightAt_ClampsOutsideTheField()
    {
        var field = Ramp();
        Assert.Equal(0f, field.HeightAt(-50f, 0f), 4);
        Assert.Equal(4f, field.HeightAt(999f, 999f), 4);
    }

    [Fact]
    public void NormalAt_IsUpOnTheFlat_AndTiltsOnASlope()
    {
        var flat = new Heightfield(5, 40f);
        Assert.Equal(Vector3.UnitY, flat.NormalAt(20f, 20f));

        var normal = Ramp().NormalAt(20f, 20f);       // rises 1 m per 10 m along +X
        Assert.True(normal.X < 0, "the normal leans away from the uphill direction");
        Assert.Equal(0f, normal.Z, 4);
        Assert.Equal(1f, normal.Length(), 4);
        Assert.Equal(-0.0995f, normal.X, 3);          // slope 1:10
    }

    [Fact]
    public void ARealSectorIs129x129Over1024Metres()
    {
        var field = new Heightfield(Terrain.SectorResolution, Terrain.SectorSize);
        Assert.Equal(8f, field.Spacing);               // 14 §3: 8 m grid
        Assert.Throws<ArgumentOutOfRangeException>(() => new Heightfield(1, 10f));
    }
}

public class TerrainTests
{
    public TerrainTests() { _ = TestEnv.UserRoot; }

    // Every vertex gets the height of its sector's X index, so sector lookup is easy to assert.
    private sealed class CountingGenerator : ITerrainGenerator
    {
        public int Calls;
        public int LastSeed;

        public void Generate(SectorCoord sector, Heightfield heights, int seed)
        {
            Calls++;
            LastSeed = seed;
            for (int i = 0; i < heights.Heights.Length; i++) heights.Heights[i] = sector.X * 100f;
        }
    }

    [Fact]
    public void Load_GeneratesOncePerSector()
    {
        var generator = new CountingGenerator();
        var terrain = new Terrain { Generator = generator, Seed = 42 };

        var first = terrain.Load(SectorCoord.Zero);
        var again = terrain.Load(SectorCoord.Zero);

        Assert.Same(first, again);
        Assert.Equal(1, generator.Calls);
        Assert.Equal(42, generator.LastSeed);
        Assert.True(terrain.IsLoaded(SectorCoord.Zero));
        Assert.False(terrain.IsLoaded(new SectorCoord(1, 0)));
        Assert.Single(terrain.Sectors);
    }

    [Fact]
    public void SectorOf_FloorsIncludingNegatives()
    {
        Assert.Equal(new SectorCoord(0, 0), Terrain.SectorOf(0f, 1023f));
        Assert.Equal(new SectorCoord(1, 2), Terrain.SectorOf(1024f, 2048f));
        Assert.Equal(new SectorCoord(-1, -1), Terrain.SectorOf(-1f, -1024f));
        Assert.Equal(new Vector3(-1024, 0, 0), new SectorCoord(-1, 0).Origin(Terrain.SectorSize));
    }

    [Fact]
    public void HeightAt_UsesTheSectorThePositionIsIn_AndZeroWhereNothingIsLoaded()
    {
        var terrain = new Terrain { Generator = new CountingGenerator() };
        terrain.Load(SectorCoord.Zero);
        terrain.Load(new SectorCoord(2, 0));

        Assert.Equal(0f, terrain.HeightAt(10f, 10f), 3);          // sector (0,0) → x index 0
        Assert.Equal(200f, terrain.HeightAt(2100f, 10f), 3);      // sector (2,0) → 200
        Assert.Equal(0f, terrain.HeightAt(5000f, 5000f), 3);      // nothing loaded there
        Assert.Equal(Vector3.UnitY, terrain.NormalAt(5000f, 5000f));
    }

    [Fact]
    public void OnGround_DropsAPositionOntoTheTerrain()
    {
        var terrain = new Terrain { Generator = new CountingGenerator() };
        terrain.Load(new SectorCoord(1, 0));

        var placed = terrain.OnGround(new Vector3(1500f, 999f, 200f));
        Assert.Equal(new Vector3(1500f, 100f, 200f), placed);
    }

    [Fact]
    public void EveryWorldHasATerrainResource_AndLoadWithoutAGeneratorIsRefused()
    {
        using var world = new World("terrain");
        var terrain = world.Resources.Get<Terrain>();
        Assert.Empty(terrain.Sectors);
        Assert.Null(terrain.Load(SectorCoord.Zero));   // Ensure fails, the game keeps running
        Assert.Equal(0f, terrain.HeightAt(0, 0));
    }
}
