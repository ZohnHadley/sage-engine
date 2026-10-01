#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A load that cannot half-happen (REDESIGN §4.5, phase 4i issue 4i-2): a corrupt save is refused whole
// and changes nothing; an entity whose prefab is gone, and a component, tag or resource this game does not
// know, survive a session without the plugin that made them; and the header says which plugins and
// content wrote the save, so a mismatch is reported rather than discovered.
public class TrustedLoadTests
{
    public TrustedLoadTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "attribute", "id": "armour", "max": 95 },
      { "type": "damage_type", "id": "physical", "resist": "armour" },
      { "type": "attack", "id": "fists", "damage": 5, "reach": 1.5 },
      { "type": "tag", "id": "state.cursed" },
      { "type": "effect", "id": "curse", "duration": "Infinite", "grantTags": ["state.cursed"],
        "modifiers": [ { "attribute": "armour", "op": "Add", "value": -10 } ] },
      { "type": "prefab", "id": "hero", "name": "hero",
        "tags": ["player_controlled"],
        "parts": { "character": { "layer": "player" }, "attributes": {}, "melee": { "attack": "fists" } } },
      { "type": "prefab", "id": "goblin", "name": "goblin",
        "components": { "ai_state": { "schedule": "sage:idle" } },
        "parts": { "character": { "layer": "enemy" }, "attributes": {} } }
    ]
    """;

    // The prefab a "mod" adds: present in one run of the game, gone in the next.
    private const string Ogre = """
    [
      { "type": "prefab", "id": "ogre", "name": "ogre",
        "components": { "ai_state": { "schedule": "sage:idle" } },
        "parts": { "character": { "layer": "enemy" }, "attributes": {} } }
    ]
    """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp NewApp(string saves, bool withOgre = false, params string[] worlds)
    {
        var builder = HeadlessApp.Gameplay().WithHands().File("data/trusted.json", Records);
        if (withOgre) builder.File("data/ogre.json", Ogre);
        var app = builder.Build();
        app.Engine.Saves.Root = saves;
        foreach (string name in worlds.Length == 0 ? new[] { "main" } : worlds)
            app.Engine.CreateWorld(name);
        return app;
    }

    private static Entity Place(World world, string prefab, string id, Vector3 at)
    {
        var entity = world.Spawn(Id(prefab), at);
        world.Add(entity, new Persistent { Id = PersistentId.FromName(id) });
        world.FlushCommands();
        return entity;
    }

    private static JsonObject WorldFile(string saves, string slot, string world = "main") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(saves, slot, $"world_{world}.json")))!.AsObject();

    private static JsonObject SavedEntity(JsonObject file, string id) =>
        file["entities"]!.AsArray().Single(e => (string?)e!["id"] == PersistentId.FromName(id).ToString())!.AsObject();

    // The acceptance test of "cannot half-happen": the second world's file is corrupt, so the first world,
    // which the old load had already destroyed and rebuilt by then, must be exactly as it was.
    [Xunit.Fact]
    public void ACorruptWorldFileLeavesEveryWorldAsItWas()
    {
        string saves = TestEnv.NewTempDir();
        using var app = NewApp(saves, withOgre: false, "main", "other");
        var main = app.Engine.Worlds.Single(w => w.Name == "main");
        var hero = Place(main, "hero", "hero", new Vector3(1, 0.1f, 1));
        Assert.True(app.Engine.Saves.Save("slot"));

        // Played on: moved, and something new spawned.
        main.Get<Transform>(hero).LocalPosition = new Vector3(50, 0, 50);
        var goblin = Place(main, "goblin", "goblin", new Vector3(0, 0.1f, -3));

        File.WriteAllText(Path.Combine(saves, "slot", "world_other.json"), "{ \"entities\": [ { \"id\": ");
        using var log = new CaptureSink();
        Assert.False(app.Engine.Saves.Load("slot"));

        Assert.Equal(hero, main.Resolve(PersistentId.FromName("hero")));
        Assert.Equal(new Vector3(50, 0, 50), main.Get<Transform>(hero).LocalPosition);
        Assert.Equal(goblin, main.Resolve(PersistentId.FromName("goblin")));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Load 'slot' failed, and nothing was changed"));
    }

    // RecordId.Parse throws FormatException, which escaped the load's catch half-way through (REDESIGN
    // §4.5): now it is read before anything is touched, and refuses the save instead.
    [Xunit.Fact]
    public void AMalformedPrefabIdIsRefusedBeforeAnythingChanges()
    {
        string saves = TestEnv.NewTempDir();
        using var app = NewApp(saves);
        var world = app.Engine.Worlds[0];
        var hero = Place(world, "hero", "hero", Vector3.Zero);
        Place(world, "goblin", "goblin", new Vector3(0, 0.1f, -3));
        Assert.True(app.Engine.Saves.Save("slot"));

        var file = WorldFile(saves, "slot");
        SavedEntity(file, "goblin")["prefab"] = "Not A Prefab!";
        File.WriteAllText(Path.Combine(saves, "slot", "world_main.json"), file.ToJsonString());

        world.Get<Transform>(hero).LocalPosition = new Vector3(7, 0, 7);
        Assert.False(app.Engine.Saves.Load("slot"));   // refused, not thrown
        Assert.Equal(hero, world.Resolve(PersistentId.FromName("hero")));
        Assert.Equal(new Vector3(7, 0, 7), world.Get<Transform>(hero).LocalPosition);
    }

    // A prefab that a removed mod added: the entity loads as an inert placeholder, keeps its place and its
    // id (the hero's curse still names it as the source), is written back verbatim, and when the mod comes
    // back so does the ogre, with the state it was saved with.
    [Xunit.Fact]
    public void AnEntityWhosePrefabIsGoneIsKeptAsAPlaceholderUntilItComesBack()
    {
        string saves = TestEnv.NewTempDir();
        JsonObject original;
        using (var withMod = NewApp(saves, withOgre: true))
        {
            var world = withMod.Engine.Worlds[0];
            var hero = Place(world, "hero", "hero", Vector3.Zero);
            var ogre = Place(world, "ogre", "ogre", new Vector3(4, 0.1f, -6));
            Effects.Apply(world, hero, Id("curse"), ogre);
            world.RunFixed(1f / 60f);
            world.Get<AIState>(ogre).Schedule = Id("chase");   // after the tick, which would pick its own
            Assert.True(withMod.Engine.Saves.Save("with_mod"));
            original = SavedEntity(WorldFile(saves, "with_mod"), "ogre");
        }

        using (var without = NewApp(saves))
        {
            var world = without.Engine.Worlds[0];
            Assert.True(without.Engine.Saves.Load("with_mod"));

            var placeholder = world.Resolve(PersistentId.FromName("ogre"));
            Assert.False(placeholder.IsNull);
            Assert.Equal(Id("ogre"), world.Get<SavePlaceholder>(placeholder).Prefab);
            Assert.False(world.Has<AIState>(placeholder));
            var at = original["components"]!["sage:transform"]!["data"]!["LocalPosition"]!.AsArray().Select(v => (float)v!).ToArray();
            Assert.Equal(new Vector3(at[0], at[1], at[2]), world.Get<Transform>(placeholder).LocalPosition);
            Assert.Equal(4f, at[0]);
            // Inert: no query, so no system, sees it.
            Assert.DoesNotContain(placeholder, world.Query<Transform>().Entities.ToEntityList());
            Assert.DoesNotContain(placeholder, world.QueryAll().Entities.ToEntityList());

            var hero = world.Resolve(PersistentId.FromName("hero"));
            Assert.Equal(placeholder, world.Get<ActiveEffects>(hero).Effects.Single(e => e.Record == Id("curse")).Source);

            world.RunFixed(1f / 60f);
            Assert.True(without.Engine.Saves.Save("without_mod"));
            Assert.True(JsonNode.DeepEquals(original, SavedEntity(WorldFile(saves, "without_mod"), "ogre")),
                "the placeholder writes back exactly what it was loaded from");

            // And a second load of the same session replaces the placeholder rather than doubling it.
            Assert.True(without.Engine.Saves.Load("without_mod"));
            Assert.NotEqual(placeholder, world.Resolve(PersistentId.FromName("ogre")));
            Assert.Single(WorldFile(saves, "without_mod")["entities"]!.AsArray(), e => (string?)e!["prefab"] == "sage:ogre");
        }

        using (var modBack = NewApp(saves, withOgre: true))
        {
            var world = modBack.Engine.Worlds[0];
            Assert.True(modBack.Engine.Saves.Load("without_mod"));
            var ogre = world.Resolve(PersistentId.FromName("ogre"));
            Assert.False(world.Has<SavePlaceholder>(ogre));
            Assert.Equal(Id("chase"), world.Get<AIState>(ogre).Schedule);
            Assert.Equal(4f, world.Get<Transform>(ogre).LocalPosition.X);
            Assert.Contains(ogre, world.Query<AIState>().Entities.ToEntityList());
            var hero = world.Resolve(PersistentId.FromName("hero"));
            Assert.Equal(ogre, world.Get<ActiveEffects>(hero).Effects.Single(e => e.Record == Id("curse")).Source);
        }
    }

    // What a plugin that is not loaded now wrote — a component, a tag, a saved resource — is not lost by
    // a session without it: the next save writes it back as it was.
    [Xunit.Fact]
    public void AnUnknownComponentTagAndResourceAreWrittenBackUnchanged()
    {
        string saves = TestEnv.NewTempDir();
        using var app = NewApp(saves);
        var world = app.Engine.Worlds[0];
        Place(world, "hero", "hero", Vector3.Zero);
        Assert.True(app.Engine.Saves.Save("slot"));

        var modComponent = new JsonObject { ["version"] = 3, ["data"] = new JsonObject { ["Strength"] = 5, ["Colours"] = new JsonArray(1, 2) } };
        var modResource = new JsonObject { ["version"] = 1, ["data"] = new JsonObject { ["Debts"] = 12 } };
        var file = WorldFile(saves, "slot");
        var hero = SavedEntity(file, "hero");
        hero["components"]!["mod:mana_shield"] = modComponent.DeepClone();
        hero["tags"]!.AsArray().Add("mod:blessed");
        file["resources"]!["mod:ledger"] = modResource.DeepClone();
        File.WriteAllText(Path.Combine(saves, "slot", "world_main.json"), file.ToJsonString());

        Assert.True(app.Engine.Saves.Load("slot"));
        var loaded = world.Resolve(PersistentId.FromName("hero"));
        Assert.True(loaded.Tags.Has<PlayerControlled>(), "the known tag loads as ever");
        world.RunFixed(1f / 60f);
        Assert.True(app.Engine.Saves.Save("again"));

        var again = WorldFile(saves, "again");
        var heroAgain = SavedEntity(again, "hero");
        Assert.True(JsonNode.DeepEquals(modComponent, heroAgain["components"]!["mod:mana_shield"]));
        Assert.Contains("mod:blessed", heroAgain["tags"]!.AsArray().Select(t => (string?)t));
        Assert.Contains("sage:player_controlled", heroAgain["tags"]!.AsArray().Select(t => (string?)t));
        Assert.True(JsonNode.DeepEquals(modResource, again["resources"]!["mod:ledger"]));
        Assert.False(heroAgain["components"]!.AsObject().ContainsKey("sage:unknown_saved_data"), "the keeper is not saved itself");
    }

    // The header lists the runtime plugins and the content mounts; a save written with others says how
    // they differ, on the slot and in the load's warning, and loads anyway.
    [Xunit.Fact]
    public void TheHeaderListsPluginsAndContentAndAMismatchWarnsButLoads()
    {
        string saves = TestEnv.NewTempDir();
        using var app = NewApp(saves);
        Place(app.Engine.Worlds[0], "hero", "hero", Vector3.Zero);
        Assert.True(app.Engine.Saves.Save("slot"));

        var slot = app.Engine.Saves.Slots.Single(s => s.Name == "slot");
        Assert.Contains(slot.Plugins, p => p.Id == "sage.gameplay.items" && p.Version.Length > 0);
        Assert.Contains(slot.Content, c => c.Namespace == "sage");
        Assert.Empty(slot.Mismatches);

        string path = Path.Combine(saves, "slot", "header.json");
        var header = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var plugins = header["plugins"]!.AsArray();
        plugins.Add(new JsonObject { ["id"] = "mod.gone", ["version"] = "1.2.0" });
        plugins.Single(p => (string?)p!["id"] == "sage.gameplay.items")!["version"] = "0.0.1";
        header["content"]!.AsArray().Add(new JsonObject { ["mount"] = "extras/gone", ["namespace"] = "gone" });
        File.WriteAllText(path, header.ToJsonString());

        app.Engine.Saves.Rescan();
        slot = app.Engine.Saves.Slots.Single(s => s.Name == "slot");
        Assert.Contains("plugin 'mod.gone' 1.2.0 is not loaded", slot.Mismatches);
        Assert.Contains(slot.Mismatches, m => m.StartsWith("plugin 'sage.gameplay.items' was 0.0.1 and is ", StringComparison.Ordinal));
        Assert.Contains("content 'extras/gone' is not mounted", slot.Mismatches);

        using var log = new CaptureSink();
        Assert.True(app.Engine.Saves.Load("slot"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("Save 'slot' was written with other plugins or content")
                                          && e.Message.Contains("mod.gone"));
    }
}
