using System.Numerics;

namespace Hello;

// The smallest game this engine can run (docs/MAKING_A_GAME.md).
//
// Everything a game must supply is here, and there is not much of it: a module that furnishes a world,
// rules that put a player in it, and a terrain generator so there is something to stand on. What it
// deliberately does *not* have is a client half — the engine's own client draws the world, so a game
// only needs one when it wants a HUD or screens of its own (games/Sandbox.Client is that).
//
// Read it top to bottom; it is about sixty lines and every one of them is a decision a game makes.
public sealed class HelloModule : IGameModule
{
    // Register only: cvars, console commands, record types of your own, prefab parts, entity inputs.
    // Records are not loaded yet and there is no graphics device — anything that reads a record belongs
    // in `Start`, and anything per-world belongs in `OnWorldCreated`.
    //
    // A cvar registered later than this is silently dropped from config.cfg (01 §5.1), which is the
    // commonest way to lose a setting.
    public void Init(ModuleContext ctx)
    {
    }

    // Called for every world, after the engine's own modules have furnished it.
    public void OnWorldCreated(World world)
    {
        // Ground first, because everything else is placed on top of it. Without a generator `Load`
        // asserts and loads nothing, after which every height reads as zero.
        var terrain = world.Resources.Get<Terrain>();
        terrain.Generator = new GentleHills();
        terrain.Seed = 1;
        terrain.Load(SectorCoord.Zero);

        // The rules have to be installed *here*: the engine substitutes `DefaultGameRules` and calls
        // `OnWorldStarted` as soon as the last module has had its turn, so a game that installs them
        // later never gets started.
        world.Resources.Set<GameRules>(new HelloRules());
    }
}

// What this game does, which is the smallest thing a game can do: put a player in the world and say
// hello. A real game overrides more of these — `OnEntityDied` to decide what dying means, `OnLoaded` to
// put things back after a save.
public sealed class HelloRules : GameRules
{
    private static readonly RecordId Player = new("hello", "player");

    public override void OnWorldStarted(World world)
    {
        SpawnPlayer(world);
        world.Say("Hello. Look around with the mouse, walk with WASD.", MessageKind.Good, 8f);
    }

    public override Entity SpawnPlayer(World world)
    {
        // Two conversions, and both matter. The first turns absolute metres into the frame the
        // simulation is using at this moment — the world shifts under you once you have travelled far
        // enough (R6) — and the second puts the player's feet on the ground rather than inside it.
        var at = world.Origin().ToOrigin(new Vector3(512f, 0f, 512f));
        at.Y = world.Resources.Get<Terrain>().HeightAt(at.X, at.Z) + 2f;
        return world.Spawn(Player, at);
    }
}

// A heightfield is a grid of heights in metres, and a generator fills one sector of it.
//
// Two sine waves and a ripple, and the amplitudes are chosen to be *visible*: the first version of this
// used gentle two-metre hills, and standing in them the world looked like a flat green wall, because
// nothing within a hundred metres changed height enough to cast a shadow or turn away from the sun.
// A first landscape should look like one.
public sealed class GentleHills : ITerrainGenerator
{
    public void Generate(SectorCoord sector, Heightfield heights, int seed)
    {
        Vector3 corner = sector.Origin(Terrain.SectorSize);
        float spacing = heights.Spacing;

        for (int z = 0; z < heights.Resolution; z++)
            for (int x = 0; x < heights.Resolution; x++)
            {
                float worldX = corner.X + x * spacing;
                float worldZ = corner.Z + z * spacing;
                heights[x, z] = MathF.Sin(worldX * 0.012f) * 9f
                              + MathF.Cos(worldZ * 0.009f) * 7f
                              + MathF.Sin((worldX + worldZ) * 0.05f) * 1.2f;
            }
    }
}
