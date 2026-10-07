#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Revert to prefab, whole, and nesting placements into a new prefab (issue #372; F31's editor half). A
// field's revert is ClearOverride (#223, InspectorTests); these are the placement-wide ones.
public class PrefabNestingTests
{
    public PrefabNestingTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "post", "components": { "transform": {}, "timer": { "interval": 2 } } },
          { "type": "prefab", "id": "lamp", "components": { "transform": {} } },
          {
            "type": "placements",
            "id": "yard",
            "place": [
              { "prefab": "post", "at": [10, 0, -4], "name": "corner", "overrides": { "components": { "timer": { "interval": 5 } } } },
              { "prefab": "lamp", "at": [12, 0, -4], "yaw": 90, "name": "corner lamp" },
              { "prefab": "post", "at": [0, 0, 0], "name": "gate",
                "outputs": [ { "output": "OnTimer", "target": "bell", "input": "Disable" } ] },
              { "prefab": "post", "at": [20, 0, 0], "name": "bell" }
            ]
          }
        ]
        """;

    private static readonly RecordId Yard = new("sandbox", "yard");

    private static (Engine Engine, MountFixture Fixture, EditDocument Document) Open()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sandbox");
        var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
        var document = new EditDocument(engine.CreateWorld("edit"));
        PrefabCommands.Register(engine.CVars, () => document);
        Assert.True(document.Open(Yard));
        return (engine, fixture, document);
    }

    private static float Interval(EditDocument document, string name) =>
        document.World.Get<LogicTimer>(document.EntityOf(document.Find(name)!)).Interval;

    [Fact]
    public void RevertAllTakesEveryOverrideAwayAsOneUndoStep()
    {
        var (engine, _, document) = Open();
        using (engine)
        {
            Assert.Equal(5f, Interval(document, "corner"));
            Assert.True(engine.CVars.Execute("ed_revert_all corner"));
            Assert.Null(document.Find("corner")!.Overrides);
            Assert.Equal(2f, Interval(document, "corner"));   // the prefab's, on the entity it re-spawned
            Assert.Equal("Revert corner to its prefab", Assert.Single(document.History.Entries).Description);

            Assert.False(PrefabCommands.RevertAll(document, document.Find("corner")!, out string error));
            Assert.Contains("overrides nothing", error);

            Assert.True(document.Undo());
            Assert.Equal(5f, Interval(document, "corner"));
        }
    }

    // Nesting: two placements become a new prefab's children where they stood, written to the game's data and
    // placed once where the first was; the world has the prefab and its two children, a fresh load of the
    // folder has the prefab, and undo puts the two placements back.
    [Fact]
    public void MakePrefabNestsPlacementsIntoANewPrefabPlacedWhereTheyStood()
    {
        var (engine, fixture, document) = Open();
        using (engine)
        {
            var corner = document.Find("corner")!;
            var lamp = document.Find("corner lamp")!;
            var placed = PrefabCommands.MakePrefab(document, new RecordId("sandbox", "corner_post"), new[] { corner, lamp }, out string error);
            Assert.True(placed != null, error);
            Assert.Equal(3, document.Placements.Count);   // the gate, the bell and the new one
            Assert.Equal(new Vector3(10, 0, -4), placed!.At);
            Assert.Equal(0, document.IndexOf(placed));

            var root = document.EntityOf(placed);
            var children = document.World.Query<Transform>().AllTags(Tags.Get<FromParentPrefab>()).Entities.ToEntityList();
            Assert.Equal(2, children.Count);
            Assert.All(children, c => Assert.Equal(root, c.Parent));
            var post = children.Single(c => c.Name == "corner");
            Assert.Equal(5f, document.World.Get<LogicTimer>(post).Interval);   // its override went with it
            Assert.Equal(new Vector3(2, 0, 0), document.World.Get<Transform>(children.Single(c => c.Name == "corner lamp")).LocalPosition);

            Assert.True(File.Exists(Path.Combine(fixture.Dir("game"), "data", "corner_post.json")));
            Assert.True(document.Save());
            using (var fresh = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build())
            {
                Assert.True(fresh.Records.ErrorCount == 0, string.Join("\n", fresh.Records.LoadErrors));
                var prefab = fresh.Records.Get<PrefabRecord>(new RecordId("sandbox", "corner_post"));
                Assert.Equal(2, prefab.Children.Count);
                Assert.Equal(new Vector3(2, 0, 0), prefab.Children[1].At);
                Assert.Equal(90f, prefab.Children[1].Yaw);
                Assert.NotNull(prefab.Children[0].Overrides);
            }

            Assert.True(document.Undo());
            Assert.Equal(4, document.Placements.Count);
            Assert.Same(corner, document.Placements[0]);
            Assert.Same(lamp, document.Placements[1]);
        }
    }

    // What a prefab's child cannot say is refused, with why: a placement with wires, one a wire names.
    [Fact]
    public void MakePrefabRefusesWiredPlacements()
    {
        var (engine, _, document) = Open();
        using (engine)
        {
            Assert.Null(PrefabCommands.MakePrefab(document, new RecordId("sandbox", "a"), new[] { document.Find("gate")! }, out string error));
            Assert.Contains("has wires", error);
            Assert.Null(PrefabCommands.MakePrefab(document, new RecordId("sandbox", "b"), new[] { document.Find("bell")! }, out error));
            Assert.Contains("is wired to", error);
            Assert.Null(PrefabCommands.MakePrefab(document, new RecordId("sandbox", "post"), new[] { document.Find("corner lamp")! }, out error));
            Assert.Contains("already", error);
            Assert.Empty(document.History.Entries);
        }
    }
}
