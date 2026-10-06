using System.Numerics;

namespace MyGame;

// MyGame's simulation: one IGameModule per game. The Sage namespaces come from Sage.Sdk.
//
// It stands on terrain and puts a player in the world, so it names the plugins those come from.
[Plugin("mygame", "0.1.0")]
[RequiresPlugin("sage", ">=0.1")]   // the engine versions this game is made for: "^0.1" pins the 0.1 series
[RequiresPlugin("sage.streaming", ">=0.1")]
[RequiresPlugin("sage.gameplay.character", ">=0.1")]
public sealed class MyGameModule : IGameModule
{
    // Register only: cvars, console commands, record types, prefab parts. Records are not loaded yet.
    public void Init(ModuleContext ctx)
    {
    }

    // Every world, after the engine's modules have furnished it: the ground to stand on.
    public void OnWorldCreated(World world)
    {
        var terrain = world.Resources.Get<Terrain>();
        terrain.Generator = new Hills();
        terrain.Seed = 1;
        terrain.Load(SectorCoord.Zero);
    }

    public GameRules CreateRules(World world) => new MyGameRules();
}

// The game's rules. The scene (content/data/scene.json) places the player; these add a greeting, which
// the client half's HUD draws.
public sealed class MyGameRules : GameRules
{
    public override void OnWorldStarted(World world)
    {
        base.OnWorldStarted(world);
        world.Say("Welcome to MyGame. Look around with the mouse, walk with WASD.", MessageKind.Good, 8f);
        Log.Info(LogCat.Gameplay, "MyGame: the player is standing on the hills.");
    }
}

// A heightfield generator: rolling hills, big enough to see. It is also an ITerrainSampler — the height at
// any point, the same function Generate fills the grid with — so a sector's edge is lit from its
// neighbour's heights and the ground shows no seam where two sectors meet.
public sealed class Hills : ITerrainGenerator, ITerrainSampler
{
    public void Generate(SectorCoord sector, Heightfield heights, int seed)
    {
        Vector3 corner = sector.Origin(Terrain.SectorSize);
        double spacing = heights.Spacing;
        for (int z = 0; z < heights.Resolution; z++)
            for (int x = 0; x < heights.Resolution; x++)
                heights[x, z] = SampleHeight(corner.X + x * spacing, corner.Z + z * spacing, seed);
    }

    public float SampleHeight(double absoluteX, double absoluteZ, int seed) =>
        MathF.Sin((float)absoluteX * 0.012f) * 9f + MathF.Cos((float)absoluteZ * 0.009f) * 7f;
}
