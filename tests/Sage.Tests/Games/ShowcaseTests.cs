#nullable enable
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The graphics showcase (games/Showcase): a walkable tech demo in data alone, its zones round a hub. This proves
// it validates, boots with no record errors and no C#, places its five brush levels on the ground, and that each
// zone's sign and the things it shows are there. What it looks like is checked by eye (games/Showcase/README.md).
#pragma warning disable SAGE0129   // terrain and the open world (4g)
public class ShowcaseTests
{
    public ShowcaseTests() { _ = TestEnv.UserRoot; }

    private static string Root => TestEnv.FolderAbove("engine_content");

    private static string GameDirectory => Path.Combine(Root, "games", "Showcase");

    private static int Count(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Count(e => e.Name == name);

    [Xunit.Fact]
    public void TheShowcaseValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = GameDirectory,
            EngineContentDirectory = Path.Combine(Root, "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
    }

    [Xunit.Fact]
    public void TheShowcaseBootsWithEveryZoneInPlace()
    {
        using var app = HeadlessApp.ForGame(GameDirectory).WithEngineContent().Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Engine.Modules.Game);                    // no C#
        var world = app.World;
        for (int i = 0; i < 30; i++) world.RunFixed(1f / 60f);

        // The five brush levels stand on the terrain.
        var levels = world.Resources.Get<MapLevels>().Loaded;
        Assert.Equal(5, levels.Count);
        Assert.All(levels, level => Assert.True(level.Placed, $"{level.Source} is not placed"));

        // Every zone's sign.
        foreach (var zone in new[] { "hub", "gallery", "hall", "fog_linear", "fog_exp2", "fog_field", "yard", "campfire",
                                     "court", "pool_sign", "pond_sign", "deck", "knoll" })
            Assert.Equal(1, Count(world, zone));

        // What the zones show: the gallery's seven plinths, the hall's lamps, the campfire, the water, the
        // mannequins and the instanced crystal field.
        foreach (var name in new[] { "normal map", "specular map", "emissive map", "environment map", "vertex colours",
                                     "emissive", "moss", "hall spot", "moving lamp", "hall torch", "hall accent",
                                     "fire", "smoke", "embers", "fire light", "pond", "pool", "walker", "drill" })
            Assert.True(Count(world, name) == 1, $"no '{name}'");
        Assert.Equal(48, Count(world, "crystal"));

        // The skies the signs name (the weathers are the client's records, not loaded headless).
        var records = app.Records;
        Assert.True(records.TryGet<SkyRecord>(new RecordId("showcase", "painted"), out var painted) && !painted.Skybox.IsEmpty);
        Assert.False(painted.SkyboxNight.IsEmpty);
        Assert.True(records.TryGet<SkyRecord>(new RecordId("showcase", "linear"), out _));
    }
}
