#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A placement's overrides and wires kept when the placed entity crosses a sector edge (issue #279, phase
// 4m-5; design 14 §3's limit, now lifted). A house prefab with child furniture, placed by a placements
// document a streamed scene names, has its `state_machine` part's `machine` overridden and a wire added in
// the editor; the game it is saved into plays it, the house crosses into the next sector (a runtime spawn
// of that sector from then on, ContentBaseline.Leave), and through a save, a load, and its sector sleeping
// and waking it is still the house the editor made: its machine, the state it was in, its furniture, its
// wire and how often that fired.
public class PrefabHandoffTests
{
    public PrefabHandoffTests() { _ = TestEnv.UserRoot; }

    private const float Sector = Terrain.SectorSize;

    private const string Content = """
    [
      { "type": "state_machine", "id": "shutters", "initial": "closed",
        "states": { "closed": { "transitions": [ { "to": "open", "on": "Open" } ] }, "open": {} } },
      { "type": "state_machine", "id": "door_b", "initial": "shut",
        "states": { "shut": { "transitions": [ { "to": "ajar", "on": "Open" } ] }, "ajar": {} } },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
      { "type": "prefab", "id": "chair", "name": "chair" },
      { "type": "prefab", "id": "table", "name": "table", "parts": { "light": { "range": 3 } } },
      { "type": "prefab", "id": "bell", "name": "bell" },
      { "type": "prefab", "id": "house", "name": "house",
        "parts": { "state_machine": { "machine": "shutters" }, "light": { "range": 5 } },
        "children": [ { "prefab": "chair", "at": [1, 0, 0], "name": "chair" },
                      { "prefab": "table", "at": [0, 0, 1], "name": "table" } ] },
      { "type": "placements", "id": "village",
        "place": [ { "prefab": "house", "at": [30, 0, 30], "name": "house", "id": "house" },
                   { "prefab": "bell", "at": [40, 0, 40], "name": "bell" } ] },
      { "type": "scene", "id": "valley", "streamed": true,
        "player": { "prefab": "hero", "at": [20, 1, 20] }, "placements": [ "village" ] }
    ]
    """;

    private static readonly RecordId Village = new("game", "village");
    private static readonly RecordId DoorB = new("game", "door_b");

    private static MountFixture Files(string content)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", content);
        files.Mount("game", "game");
        return files;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static void Walk(World world, float x, float z)
    {
        world.Get<Transform>(Hero(world)).LocalPosition = world.Origin().ToOrigin(new Vector3(x, 1, z));
        Tick(world);
    }

    // The editor's half: the override and the wire, saved into the document's file. Returns the file's text.
    private static string EditInTheEditor()
    {
        var files = Files(Content);
        var engine = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay().Mount(files).Build().Engine;
        using (engine)
        {
            var document = new EditDocument(engine.CreateWorld("edit"));
            Assert.True(document.Open(Village));
            var house = document.Find("house")!;
            document.Execute(new SetOverride(document, house, OverrideSection.Part, "state_machine", "machine", JsonValue.Create("door_b")));
            document.Execute(new SetOverride(document, house, OverrideSection.Part, "light", "range", JsonValue.Create(9f)));
            document.Execute(new SetOutputs(document, house, new[] { new Connection { Output = "OnStateChanged", Target = "bell", Input = "Kill", Times = 2, Delay = 5 } }));

            // In the editor's world, re-spawned with its furniture and the overridden machine.
            var world = document.World;
            var entity = document.EntityOf(house);
            Assert.Equal(DoorB, world.Get<StateMachine>(entity).Machine);
            Assert.Equal(2, entity.ChildEntities.ToArray().Count(c => c.Name is "chair" or "table"));
            Assert.True(document.Save());
            return File.ReadAllText(document.Path);
        }
    }

    private static void AssertTheEditorsHouse(World world, string state, int fired)
    {
        var house = One(world, "house");
        var machine = world.Get<StateMachine>(house);
        Assert.Equal(DoorB, machine.Machine);
        Assert.Equal(state, StateMachines.StateOf(world, house));
        Assert.Equal(9f, world.Get<PointLight>(house).Range);   // a part's other option, overridden too
        Assert.Equal(new[] { "chair", "table" }, house.ChildEntities.ToArray().Select(c => c.Name).OrderBy(n => n).ToArray());
        Assert.Equal(3f, world.Get<PointLight>(One(world, "table")).Range);
        var wire = Assert.Single(world.Get<IOConnections>(house).Wires);
        Assert.Equal(("OnStateChanged", "bell", "Kill", 2, 5f), (wire.Output, wire.Target, wire.Input, wire.Times, wire.Delay));
        Assert.Equal(fired, wire.Fired);
        var ids = world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Xunit.Fact]
    public void AHouseWithAnOverriddenMachineRoundTripsTheEditorASaveAndASectorCrossing()
    {
        string saved = EditInTheEditor();

        // The file a game loads says what the editor did.
        var file = Assert.IsType<JsonArray>(JsonNode.Parse(saved, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip }));
        var village = file.OfType<JsonObject>().Single(r => (string?)r["id"] == "village");
        Assert.Equal("door_b", (string?)village["place"]![0]!["overrides"]!["parts"]!["state_machine"]!["machine"]);

        var files = Files(saved);
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .Mount(files).StartScene("game:valley").Boot();
        app.Engine.Saves.Root = TestEnv.NewTempDir();
        app.CVars.Execute("save_autosave 0");
        var world = app.World;
        Tick(world);
        Assert.Equal(0, app.Records.ErrorCount);

        // Placed by sector (0, 0), as the editor left it.
        var house = One(world, "house");
        var id = house.GetComponent<Persistent>().Id;
        Assert.True(StateMachines.SetState(world, house, "ajar"));
        world.Get<IOConnections>(house).Wires[0].Fired = 1;   // as if it had fired once (Times 2)
        AssertTheEditorsHouse(world, "ajar", fired: 1);

        // It crosses into (1, 0): that sector's runtime spawn now, no content's.
        world.Get<Transform>(house).LocalPosition = world.Origin().ToOrigin(new Vector3(Sector + 30, 0, 30));
        Tick(world);
        var baseline = ContentIds.Baseline(world);
        Assert.False(baseline.TryGetSource(id, out _));
        Assert.Equal(ContentIds.SectorSource(new RecordId("game", "valley"), new SectorCoord(1, 0)), world.Get<InCell>(house).Source);
        AssertTheEditorsHouse(world, "ajar", fired: 1);

        // A save and a load: rebuilt from its prefab with the placement's overrides, and wired again.
        Assert.True(app.Engine.Saves.Save("crossed"));
        StateMachines.SetState(world, One(world, "house"), "shut");
        Assert.True(app.Engine.Saves.Load("crossed"));
        Tick(world);
        AssertTheEditorsHouse(world, "ajar", fired: 1);
        Assert.Equal(id, One(world, "house").GetComponent<Persistent>().Id);

        // Its sector sleeps (the player walks far east) and wakes (back): the same house.
        Walk(world, 6 * Sector + 20, 20);
        Assert.Empty(Named(world, "house"));
        Assert.Empty(Named(world, "chair"));

        // Saved while asleep, loaded, and woken.
        Assert.True(app.Engine.Saves.Save("asleep"));
        Assert.True(app.Engine.Saves.Load("asleep"));
        Tick(world);
        Walk(world, 20, 20);
        Tick(world);
        AssertTheEditorsHouse(world, "ajar", fired: 1);
        Assert.Equal(new Vector3(Sector + 30, 0, 30), world.Origin().ToAbsolute(world.Get<Transform>(One(world, "house")).LocalPosition));
        Assert.Equal(id, One(world, "house").GetComponent<Persistent>().Id);

        // And sector (0, 0) did not place it a second time.
        Assert.Single(Named(world, "house"));
    }

    // The saved entry says what its placement said, and only for a root no content places: a house still in
    // its sector is written with its source and nothing more, as before.
    [Xunit.Fact]
    public void OnlyARootNoContentPlacesWritesItsPlacement()
    {
        var files = Files(Content.Replace("""{ "prefab": "house", "at": [30, 0, 30], "name": "house", "id": "house" }""",
            """{ "prefab": "house", "at": [30, 0, 30], "name": "house", "id": "house", "overrides": { "parts": { "state_machine": { "machine": "door_b" } } }, "outputs": [ { "output": "OnStateChanged", "target": "bell", "input": "Kill" } ] }"""));
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .Mount(files).StartScene("game:valley").Boot();
        var world = app.World;
        Tick(world);
        var house = One(world, "house");
        var engine = app.Engine;

        string id = house.GetComponent<Persistent>().Id.ToString();
        var placed = engine.Saves.Capture(world, new[] { house });
        Assert.Equal(3, placed.Count(e => e["source"] != null));   // the house and its furniture: content's
        Assert.All(placed, e => Assert.Null(e["overrides"]));
        Assert.All(placed, e => Assert.Null(e["outputs"]));

        world.Get<Transform>(house).LocalPosition = world.Origin().ToOrigin(new Vector3(Sector + 30, 0, 30));
        Tick(world);
        var left = engine.Saves.Capture(world, new[] { house });
        var root = Assert.Single(left, e => (string?)e["id"] == id);
        Assert.Null(root["source"]);
        Assert.Equal("door_b", (string?)root["overrides"]!["parts"]!["state_machine"]!["machine"]);
        Assert.Equal("bell", (string?)root["outputs"]![0]!["Target"]);
        Assert.Equal(2, left.Count(e => e["parent"] != null));   // the furniture, by its parent, as before
        Assert.All(left.Where(e => e["parent"] != null), e => Assert.Null(e["overrides"]));
    }
}
