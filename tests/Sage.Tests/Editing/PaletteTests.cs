#nullable enable
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The prefab palette and placing (issue #222, docs/design/15 §6): what the panel lists, where a click
// lands, and `ed_place`, which is the same placing typed.
public class PaletteTests
{
    public PaletteTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "post", "components": { "transform": {} } },
          { "type": "prefab", "id": "lamp", "components": { "transform": {} } },
          { "type": "prefab", "id": "base_thing", "abstract": true, "components": { "transform": {} } },
          { "type": "placements", "id": "yard", "place": [ { "prefab": "post", "at": [0, 0, 0], "name": "post" } ] }
        ]
        """;

    private const string Extra = """
        [
          { "type": "prefab", "id": "crate", "components": { "transform": {} } },
          { "type": "prefab", "id": "post_big", "components": { "transform": {} } }
        ]
        """;

    private static (Engine Engine, EditDocument Document) Open()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Write("extra", "data/extra.json", Extra);
        fixture.Mount("game", "sandbox");
        fixture.Mount("extra", "other");
        var engine = HeadlessApp.Bare().With(new PhysicsModule()).Mount(fixture).Build().Engine;
        var document = new EditDocument(engine.CreateWorld("edit"));
        EditorCommands.Register(engine.CVars, () => document);
        Assert.True(document.Open(new RecordId("sandbox", "yard")));
        return (engine, document);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) <= 1e-3f, $"expected {expected}, got {actual}");

    [Fact]
    public void ThePaletteListsPrefabsByNamespaceAndTheSearchNarrowsThem()
    {
        var (engine, _) = Open();
        using (engine)
        {
            var palette = new PrefabPalette(engine.Records);
            var groups = palette.Groups();
            Assert.Equal(new[] { "other", "sandbox" }, groups.Select(g => g.Namespace).Where(n => n is "other" or "sandbox"));
            Assert.Contains(new RecordId("other", "crate"), groups.Single(g => g.Namespace == "other").Prefabs);
            var sandbox = groups.Single(g => g.Namespace == "sandbox").Prefabs.Select(p => p.Name).ToList();
            Assert.Contains("lamp", sandbox);
            Assert.DoesNotContain("base_thing", sandbox);   // abstract: a template, not a thing to place

            palette.Search = "POST";                         // any case, anywhere in the id
            Assert.Equal(new[] { "other:post_big", "sandbox:post" }, palette.Groups().SelectMany(g => g.Prefabs).Select(p => p.ToString()).Where(s => s.Contains("post")));
            palette.Search = "other crate";                  // every word must match
            Assert.Equal(new[] { new RecordId("other", "crate") }, palette.Groups().SelectMany(g => g.Prefabs).ToArray());
            palette.Search = "zzz";
            Assert.Empty(palette.Groups());

            palette.Arm(new RecordId("sandbox", "lamp"));
            Assert.True(palette.IsArmed);
            palette.Disarm();
            Assert.False(palette.IsArmed);
        }
    }

    [Fact]
    public void EdPlaceAddsAPlacementWithAUniqueNameAndUndoRemovesIt()
    {
        var (engine, document) = Open();
        using (engine)
        {
            var cvars = engine.CVars;
            Assert.True(cvars.Execute("ed_place lamp 1 2 3 90"));
            Assert.True(cvars.Execute("ed_place lamp 4 0 0"));
            Assert.True(cvars.Execute("ed_place post"));                 // "post" is taken by the document's own
            Assert.True(cvars.Execute("ed_place sandbox:lamp 0 0 5 0 north lamp"));

            var names = document.Placements.Select(p => p.Name).ToList();
            Assert.Equal(new[] { "post", "lamp", "lamp_2", "post_2", "north lamp" }, names);
            Assert.Equal(names.Count, document.Placements.Select(p => p.Id).Distinct().Count());
            var first = document.Find("lamp")!;
            Near(new Vector3(1, 2, 3), first.At);
            Assert.Equal(90f, first.Yaw);
            Near(new Vector3(0, 0, 0), document.Find("post_2")!.At);     // no position: the origin
            Assert.False(document.EntityOf(first).IsNull);

            cvars.Execute("ed_undo");
            Assert.Equal(4, document.Placements.Count);
            Assert.Null(document.Find("north lamp"));
            cvars.Execute("ed_undo 3");
            Assert.Single(document.Placements);
            Assert.True(document.World.FindByName("lamp").IsNull);

            // Not a prefab, no document: a warning and no change.
            cvars.Execute("ed_place nothing_like_it");
            Assert.Single(document.Placements);
            document.Close();
            cvars.Execute("ed_place lamp");
            Assert.Empty(document.Placements);
        }
    }

    [Fact]
    public void PlacingOnAColliderLandsOnItsTopAndOnEmptyGroundAtYZero()
    {
        var (engine, document) = Open();
        using (engine)
        {
            var world = document.World;
            var box = world.Create(Transform.At(new Vector3(10, 1, -5)), "block");   // a 2 m cube, top at y = 2
            world.Add(box, Collider.Box(new Vector3(2, 2, 2)));
            for (int i = 0; i < 2; i++) world.RunFixed(1f / 60f);

            var down = new EditorRay(new Vector3(10, 20, -5), -Vector3.UnitY);
            Near(new Vector3(10, 2, -5), Placing.Surface(world, down)!.Value);
            var onBlock = Placing.PlaceAt(document, new RecordId("sandbox", "lamp"), down)!;
            Near(new Vector3(10, 2, -5), onBlock.At);

            // Slanted at the ground, nothing under the cursor: the plane y = 0.
            var slant = new EditorRay(new Vector3(0, 4, 0), Vector3.Normalize(new Vector3(1, -1, 0)));
            Near(new Vector3(4, 0, 0), Placing.Surface(world, slant)!.Value);

            // Pointing at the sky, or along the ground: nothing to place on.
            Assert.Null(Placing.Surface(world, new EditorRay(new Vector3(0, 4, 0), Vector3.UnitY)));
            Assert.Null(Placing.Surface(world, new EditorRay(new Vector3(0, 4, 0), Vector3.UnitX)));
            Assert.Null(Placing.PlaceAt(document, new RecordId("sandbox", "lamp"), new EditorRay(new Vector3(0, 4, 0), Vector3.UnitY)));
        }
    }
}
