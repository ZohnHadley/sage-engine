#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Saves name their mods (phase 4j, issue 4j-4): the header lists the active mods with their versions, a
// load without one (or with another version) warns and still loads, the missing mod's entity is a
// placeholder, and a mod's mount is not said twice as content.
public class ModSaveTests
{
    public ModSaveTests() { _ = TestEnv.UserRoot; }

    private const string Orcs = """
    [
      { "type": "prefab", "id": "ogre", "name": "ogre",
        "components": { "ai_state": { "schedule": "sage:idle" } },
        "parts": { "character": { "layer": "enemy" }, "attributes": {} } }
    ]
    """;

    private static HeadlessApp NewApp(string saves, string? modVersion)
    {
        var builder = HeadlessApp.Gameplay().WithHands();
        if (modVersion != null)
        {
            var fixture = new MountFixture();
            fixture.Mount("mods/orcs", "orcs");
            fixture.Write("mods/orcs", "data/ogre.json", Orcs);
            builder.Mount(fixture);
        }
        var app = builder.Build();
        if (modVersion != null)
            app.Engine.Mods = new ModLoadResult(new[] { new ModManifest { Id = "orcs", Version = modVersion } },
                Array.Empty<RefusedMod>(), Array.Empty<ModManifest>(), Array.Empty<string>());
        app.Engine.Saves.Root = saves;
        app.Engine.CreateWorld("main");
        return app;
    }

    private static void PlaceOgre(HeadlessApp app)
    {
        var world = app.Engine.Worlds[0];
        var ogre = world.Spawn(new RecordId("orcs", "ogre"), new Vector3(4, 0, -6));
        world.Add(ogre, new Persistent { Id = PersistentId.FromName("ogre") });
        world.FlushCommands();
    }

    private static JsonObject Header(string saves, string slot) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(saves, slot, "header.json")))!.AsObject();

    [Xunit.Fact]
    public void ASaveListsItsModsAndLeavesTheirMountsOutOfContent()
    {
        string saves = TestEnv.NewTempDir();
        using var app = NewApp(saves, "1.2.0");
        Assert.True(app.Engine.Saves.Save("slot"));

        var header = Header(saves, "slot");
        var mod = Assert.Single(header["mods"]!.AsArray())!.AsObject();
        Assert.Equal("orcs", (string?)mod["id"]);
        Assert.Equal("1.2.0", (string?)mod["version"]);
        Assert.DoesNotContain(header["content"]!.AsArray(), c => ((string?)c!["mount"])!.StartsWith("mods/", StringComparison.Ordinal));

        var slot = app.Engine.Saves.Slots.Single();
        Assert.Equal(new SavedMod("orcs", "1.2.0"), Assert.Single(slot.Mods));
        Assert.Empty(slot.Mismatches);
    }

    [Xunit.Fact]
    public void ALoadWithoutAModWarnsNamingItAndKeepsTheModsEntityAsAPlaceholder()
    {
        string saves = TestEnv.NewTempDir();
        using (var with = NewApp(saves, "1.2.0"))
        {
            PlaceOgre(with);
            Assert.True(with.Engine.Saves.Save("slot"));
        }

        using var without = NewApp(saves, null);
        Assert.Contains("mod 'orcs' 1.2.0 is not active", without.Engine.Saves.Slots.Single().Mismatches);
        using var log = new CaptureSink();
        Assert.True(without.Engine.Saves.Load("slot"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("orcs"));

        var world = without.Engine.Worlds[0];
        var placeholder = world.Resolve(PersistentId.FromName("ogre"));
        Assert.False(placeholder.IsNull);
        Assert.Equal(new RecordId("orcs", "ogre"), world.Get<SavePlaceholder>(placeholder).Prefab);
    }

    [Xunit.Fact]
    public void AModAtAnotherVersionAndAModNotThereBeforeAreSaid()
    {
        string saves = TestEnv.NewTempDir();
        using (var old = NewApp(saves, "1.1.0")) Assert.True(old.Engine.Saves.Save("slot"));
        using (var none = NewApp(saves, null)) Assert.True(none.Engine.Saves.Save("bare"));

        using var now = NewApp(saves, "1.2.0");
        Assert.Contains("mod 'orcs' was 1.1.0 and is 1.2.0", now.Engine.Saves.Slots.Single(s => s.Name == "slot").Mismatches);
        Assert.Contains("mod 'orcs' was not active then", now.Engine.Saves.Slots.Single(s => s.Name == "bare").Mismatches);
        Assert.True(now.Engine.Saves.Load("slot"));
    }

    [Xunit.Fact]
    public void ASaveFromBeforeHeadersListedModsSaysNothingAboutThem()
    {
        string saves = TestEnv.NewTempDir();
        using var app = NewApp(saves, "1.2.0");
        Assert.True(app.Engine.Saves.Save("slot"));
        string path = Path.Combine(saves, "slot", "header.json");
        var header = Header(saves, "slot");
        header.Remove("mods");
        File.WriteAllText(path, header.ToJsonString());
        app.Engine.Saves.Rescan();
        Assert.Empty(app.Engine.Saves.Slots.Single().Mismatches);
    }
}
