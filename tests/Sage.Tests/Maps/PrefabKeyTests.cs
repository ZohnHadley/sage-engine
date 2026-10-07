#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Per-entity keys in a map, and the FGD that offers them (docs/design/15 §3, issue #18). TrenchBroom
// shows a mapper typed, described keys for each prefab — one per field of each part and component the
// prefab writes, from the metadata table — and the importer applies exactly those keys.
public class PrefabKeyTests
{
    public PrefabKeyTests() { _ = TestEnv.UserRoot; }

    private static string SandboxDirectory => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");

    // The acceptance test for the FGD: the Sandbox's prefabs arrive in TrenchBroom with keys that have a
    // type the editor draws a widget for, the prefab's own value as the default and a description made
    // of the field's tooltip, unit, range and what it refers to.
    [Fact]
    public void TheSandboxFgdHasTypedDescribedKeysForItsPrefabs()
    {
        using var app = HeadlessApp.ForGame(SandboxDirectory, new Sandbox.SandboxModule()).Build();

        string fgd = FgdExport.Build(app.Engine);
        Assert.DoesNotContain("\r", fgd);   // the same bytes on every platform

        // The lamp: a float with its unit and range, a colour picker, both at the prefab's values.
        Assert.Contains("light.range(float) : \"light: Range\" : \"7\" : \"Where the light fades to nothing (m, at least 0, part light)\"", fgd);
        Assert.Matches(@"light\.colour\(color1\) : ""light: Colour"" : ""1(\.0)? 0\.82 0\.55"" : ""Linear RGB; 0 0 0 means white", fgd);

        // An enum is a dropdown of its names; a record reference says which record type it names.
        Assert.Contains("body.shape(choices) : \"body: Shape\" : \"Capsule\" : \"Box, Sphere or Capsule; a capsule stands on the origin (part body)\" = [ \"Box\" : \"Box\" \"Sphere\" : \"Sphere\" \"Capsule\" : \"Capsule\" \"Mesh\" : \"Mesh\" ]", fgd);
        Assert.Contains("melee.attack(string) : \"melee: Attack\" : \"claws\" : \"What it swings (attack record id, part melee)\"", fgd);
        Assert.Contains("body.trigger(choices) : \"body: Trigger\" : 1 : \"Reports overlaps and never blocks (part body)\" = [ 0 : \"No\" 1 : \"Yes\" ]", fgd);

        // A component the prefab writes gets keys too, named as the prefab names it.
        Assert.Contains("ai_state.taskIndex(integer) : \"ai_state: Task index\" : 0 : \"Which task of the schedule it is on (at least 0, component ai_state)\"", fgd);

        // And never a [Transient] field: a spawn would not keep it.
        Assert.DoesNotContain("ai_state.conditions", fgd);

        // Every prefab with a key-worthy part has a block of keys; the watcher has several sections, its
        // `base` prefab's sprite included.
        var watcher = Regex.Match(fgd, @"= watcher :[^\n]*\n\[\n(?<keys>(    [^\n]*\n)+)\]");
        Assert.True(watcher.Success, "the watcher has no keys");
        var sections = watcher.Groups["keys"].Value.Split('\n', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split('.')[0]).Distinct().ToList();
        Assert.Equal(new[] { "sprite", "character", "faction", "melee", "inventory", "loot", "ai_state", "map_marker" }, sections);   // map_marker: on the map (#99); inventory: its body is looted (#344); loot: its drop (#379)

        // Outputs come from what the plugins declare, with what each means.
        Assert.Matches(@"//   OnUse +Something used this entity", fgd);
    }

    // What the FGD offers the importer reads: each key's default, typed back into a map, parses as its
    // field — so no key in the file is one TrenchBroom lets you fill and the engine then refuses.
    [Fact]
    public void EveryKeyTheFgdOffersReadsBackItsOwnDefault()
    {
        using var app = HeadlessApp.ForGame(SandboxDirectory, new Sandbox.SandboxModule()).Build();
        string fgd = FgdExport.Build(app.Engine);
        int checkedKeys = 0;

        foreach (var id in app.Records.Ids("prefab"))
        {
            Assert.True(app.Records.TryGet(id, out PrefabRecord prefab));
            foreach (var key in PrefabKeys.For(app.Engine, id, prefab))
            {
                Assert.Contains(key.Key + "(", fgd);
                Assert.True(PrefabKeys.TryParse(key.Field, key.DefaultText, out _, out string why),
                            $"{id}: '{key.Key}' offers \"{key.DefaultText}\", which {why}");
                checkedKeys++;
            }
        }
        Assert.True(checkedKeys > 30, $"only {checkedKeys} keys");
    }

    private const string Map = """
        {
        "classname" "worldspawn"
        }
        {
        "classname" "lamp"
        "origin" "0 0 0"
        "targetname" "red"
        "light.range" "12"
        "light.colour" "1 0 0"
        }
        {
        "classname" "lamp"
        "origin" "64 0 0"
        "targetname" "plain"
        }
        {
        "classname" "lamp"
        "origin" "128 0 0"
        "targetname" "bad"
        "light.range" "far"
        "light.intensity" "-3"
        }
        """;

    private const string Records = """
        [
          { "type": "map", "id": "room", "file": "maps/room.map" },
          { "type": "prefab", "id": "lamp", "parts": { "light": { "range": 7, "intensity": 2 } } }
        ]
        """;

    // A mapper's per-entity value overrides the prefab's, for that entity only; a value that does not
    // read as the field's type — or is outside its [Property] range — is an error naming the key, and
    // the prefab's own value stands.
    [Fact]
    public void AMapEntitysKeysSetItsPrefabsFieldsForThatEntityAlone()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "maps/room.map", Map);
        fixture.Write("game", "data/level.json", Records);
        fixture.Mount("game", "sandbox");
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay().With(new MapModule()).Mount(fixture).Build();
        var world = app.CreateWorld("level");

        MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        var red = world.Get<PointLight>(world.FindByName("red"));
        Assert.Equal(12f, red.Range);
        Assert.Equal(new Vector3(1, 0, 0), red.Colour);
        Assert.Equal(2f, red.Intensity);                           // not overridden: the prefab's

        var plain = world.Get<PointLight>(world.FindByName("plain"));
        Assert.Equal(7f, plain.Range);                             // the prefab itself is untouched

        var bad = world.Get<PointLight>(world.FindByName("bad"));
        Assert.Equal((7f, 2f), (bad.Range, bad.Intensity));
        var errors = capture.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        Assert.Contains(errors, m => m.Contains("'light.range' is a float, and \"far\" is not a number"));
        Assert.Contains(errors, m => m.Contains("'light.intensity' is a float, and \"-3\" is below its minimum, 0"));
    }
}
