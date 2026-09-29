using System.Numerics;

namespace Hello;

// The smallest game this engine can run (docs/MAKING_A_GAME.md).
//
// Everything a game must supply in C# is here, and there is not much of it: a module that furnishes a
// world, rules that greet the player the scene put in it, and a terrain generator so there is something
// to stand on. (A game with no ground of its own to generate needs no C# at all: tests/games/scene-only.) What it
// deliberately does *not* have is a client half — the engine's own client draws the world, so a game
// only needs one when it wants a HUD or screens of its own (games/Sandbox.Client is that).
//
// Read it top to bottom; it is about fifty lines and every one of them is a decision a game makes.
//
// It stands on terrain and puts a player in the world, so it names the plugins those come from: a game
// that leaves one out of game.json's `plugins` hears so at boot, not as a crash in OnWorldCreated.
[Plugin("hello", "0.1.0")]
[RequiresPlugin("sage.streaming", ">=0.1")]
[RequiresPlugin("sage.gameplay.character", ">=0.1")]
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
        terrain.Generator = new RollingHills();
        terrain.Seed = 1;
        terrain.Load(SectorCoord.Zero);
    }

    // The game's rules for each world; the engine starts them once every module has set the world up.
    public GameRules CreateRules(World world) => new HelloRules();
}

// What this game does, which is the smallest thing a game can do: put a player in the world and say
// hello. *Where* the player goes is data — the scene game.json names (content/data/hello.json) — so the
// engine's default rules spawn it, and these only add the greeting. A real game overrides more of these:
// `OnEntityDied` to decide what dying means, `OnLoaded` to put things back after a save.
public sealed class HelloRules : GameRules
{
    public override void OnWorldStarted(World world)
    {
        // The default: the scene's `player`, standing on the ground at its start.
        base.OnWorldStarted(world);

        // `Say` puts a line in the world's message log, which every world has — and **nothing draws it
        // here**, because drawing is a client's job and this game has no client half. That is the split
        // working as intended rather than a bug: a headless server would say the same words to nobody.
        // Write a HUD (13 §3, and games/Sandbox.Client/Hud.cs) and these appear on screen.
        world.Say("Hello. Look around with the mouse, walk with WASD.", MessageKind.Good, 8f);
        Log.Info(LogCat.Gameplay, "Hello: a player is standing on the hills at 512, 512.");
    }
}

// A heightfield is a grid of heights in metres, and a generator fills one sector of it.
//
// Two sine waves and a ripple, and the amplitudes are chosen to be *visible*: the first version of this
// used gentle two-metre hills, and standing in them the world looked like a flat green wall, because
// nothing within a hundred metres changed height enough to cast a shadow or turn away from the sun.
// A first landscape should look like one.
public sealed class RollingHills : ITerrainGenerator
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
