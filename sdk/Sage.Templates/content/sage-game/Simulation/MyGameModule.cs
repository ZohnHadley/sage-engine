using System.Numerics;

namespace MyGame;

// MyGame's simulation: one IGameModule per game. The Sage namespaces come from Sage.Sdk.
//
// It stands on terrain and puts a player in the world, so it names the plugins those come from.
[Plugin("mygame", "0.1.0")]
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

// A heightfield generator: rolling hills, big enough to see.
public sealed class Hills : ITerrainGenerator
{
    public void Generate(SectorCoord sector, Heightfield heights, int seed)
    {
        Vector3 corner = sector.Origin(Terrain.SectorSize);
        for (int z = 0; z < heights.Resolution; z++)
            for (int x = 0; x < heights.Resolution; x++)
            {
                float worldX = corner.X + x * heights.Spacing, worldZ = corner.Z + z * heights.Spacing;
                heights[x, z] = MathF.Sin(worldX * 0.012f) * 9f + MathF.Cos(worldZ * 0.009f) * 7f;
            }
    }
}
