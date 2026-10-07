#nullable enable
using System.Linq;
using System.Text.Json.Nodes;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The inspector past scalars (issue #368): a list (an NPC's inventory) is edited row by row — an
// element's field set, an element added, moved and removed — and a component the prefab does not name
// (a light) is added to one placement alone. Every change is an override of the placement, undoable,
// and a save, a reload and a reopen keep it.
public class NestedInspectorTests
{
    public NestedInspectorTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "item", "id": "bread", "label": "bread", "weight": 1, "maxStack": 10 },
          { "type": "item", "id": "apple", "label": "apple", "weight": 0.5, "maxStack": 10 },
          { "type": "item", "id": "knife", "label": "knife", "weight": 1, "maxStack": 10 },
          { "type": "prefab", "id": "trader",
            "components": { "transform": {} },
            "parts": { "inventory": { "items": [ { "item": "bread", "count": 3 }, { "item": "apple", "count": 2 } ] } } },
          { "type": "prefab", "id": "crate", "components": { "transform": {}, "timer": { "interval": 2 } } },
          {
            "type": "placements",
            "id": "market",
            "place": [
              { "prefab": "trader", "at": [0, 0, 0], "name": "trader" },
              { "prefab": "crate", "at": [2, 0, 0], "name": "crate" },
              { "prefab": "crate", "at": [4, 0, 0], "name": "crate2" }
            ]
          }
        ]
        """;

    private static (Engine Engine, EditDocument Document) Open()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/market.json", Records);
        fixture.Mount("game", "sandbox");
        var engine = HeadlessApp.Gameplay().Mount(fixture).Build().Engine;
        Assert.Equal(0, engine.Records.ErrorCount);
        var document = new EditDocument(engine.CreateWorld("edit"));
        EditorCommands.Register(engine.CVars, () => document);
        InspectorCommands.Register(engine.CVars, () => document);
        Assert.True(document.Open(new RecordId("sandbox", "market")));
        return (engine, document);
    }

    private static Entity EntityOf(EditDocument document, string name) => document.EntityOf(document.Find(name)!);

    private static InspectorModel Inspect(EditDocument document, string name) =>
        InspectorModel.Of(document, document.World, EntityOf(document, name));

    // What the trader carries, in order: "knife:1 apple:5".
    private static string Carried(EditDocument document)
    {
        var items = document.World.Get<Inventory>(EntityOf(document, "trader")).Items;
        return string.Join(" ", items.Select(s => $"{s.Item.Name}:{s.Count}"));
    }

    // The part's options as the panel shows them: one row per element, its fields under it.
    private static string Rows(EditDocument document) =>
        string.Join(" ", Inspect(document, "trader").Find("inventory.items")!.Children
            .Select(e => $"{((IRecordRef)e.Children.Single(c => c.Name == "item").Value!).Id.Name}:{e.Children.Single(c => c.Name == "count").Value}"));

    [Fact]
    public void AnNpcsInventoryListIsEditedRowByRowAndASaveKeepsIt()
    {
        var (engine, document) = Open();
        using (engine)
        {
            var trader = document.Find("trader")!;
            var model = Inspect(document, "trader");
            var items = model.Find("inventory.items")!;
            Assert.True(items.Editable);
            Assert.Equal(2, items.Children.Count);
            Assert.Equal("inventory.items[1].count", items.Children[1].Children.Single(c => c.Name == "count").Path);
            Assert.Equal(2, model.Find("inventory.items[1].count")!.Value);
            Assert.Equal("bread:3 apple:2", Carried(document));

            // A nested field: the list is the override, written whole, and the trader re-spawns with it.
            Assert.True(engine.CVars.Execute("ed_set trader inventory.items[1].count 5"));
            Assert.Equal("""{"inventory":{"items":[{"item":"sandbox:bread","count":3},{"item":"sandbox:apple","count":5}]}}""",
                         trader.Overrides!.Parts!.ToJsonString());
            Assert.Equal("bread:3 apple:5", Carried(document));
            Assert.True(Inspect(document, "trader").Find("inventory.items[1].count")!.Overridden);

            // Added at the end (the element's default), then its item set; moved to the front; one removed.
            Assert.True(engine.CVars.Execute("ed_add trader inventory.items"));
            Assert.Equal("bread:3 apple:5 :1", Rows(document));
            Assert.True(engine.CVars.Execute("ed_set trader inventory.items[2].item knife"));
            Assert.True(engine.CVars.Execute("ed_reorder trader inventory.items[2] 0"));
            Assert.Equal("knife:1 bread:3 apple:5", Rows(document));
            Assert.Equal("knife:1 bread:3 apple:5", Carried(document));
            Assert.True(engine.CVars.Execute("ed_remove trader inventory.items[1]"));
            Assert.Equal("knife:1 apple:5", Carried(document));
            Assert.Equal(5, document.History.Position);

            // Every step undoes, back to the prefab's list and no override at all; and redoes.
            document.Undo();
            Assert.Equal("knife:1 bread:3 apple:5", Carried(document));
            document.Undo();
            Assert.Equal("bread:3 apple:5 knife:1", Rows(document));
            document.Undo();
            Assert.Equal("bread:3 apple:5 :1", Rows(document));
            for (int i = 0; i < 2; i++) document.Undo();
            Assert.Null(trader.Overrides);
            Assert.Equal("bread:3 apple:2", Carried(document));
            for (int i = 0; i < 5; i++) document.Redo();
            Assert.Equal("knife:1 apple:5", Carried(document));

            // Saved, reloaded and reopened: the edited list is what the trader carries.
            Assert.True(engine.CVars.Execute("doc_save"));
            engine.CVars.Execute("doc_close");
            engine.Records.Reload();
            Assert.Equal(0, engine.Records.ErrorCount);
            engine.CVars.Execute("doc_open market");
            Assert.Equal("knife:1 apple:5", Carried(document));
            Assert.Equal("knife:1 apple:5", Rows(document));
            Assert.True(Inspect(document, "trader").Find("inventory.items")!.Overridden);

            // A revert of a row inside the list reverts the list: the override is the field's, whole.
            Assert.True(Inspect(document, "trader").Revert(Inspect(document, "trader").Find("inventory.items[0].count")!, out _));
            Assert.Equal("bread:3 apple:2", Carried(document));
        }
    }

    [Fact]
    public void ALightIsAddedToOnePlacedCrateAndASaveKeepsIt()
    {
        var (engine, document) = Open();
        using (engine)
        {
            var world = document.World;
            var model = Inspect(document, "crate");
            Assert.Contains("sage:point_light", model.AddableComponents());
            Assert.DoesNotContain("sage:timer", model.AddableComponents());       // it has one: the prefab's
            Assert.DoesNotContain("sage:transform", model.AddableComponents());
            Assert.False(model.AddComponent("timer", out string why));
            Assert.Contains("already has", why);

            Assert.True(engine.CVars.Execute("ed_add_component crate point_light"));
            Assert.True(world.Has<PointLight>(EntityOf(document, "crate")));
            Assert.False(world.Has<PointLight>(EntityOf(document, "crate2")));
            var group = Inspect(document, "crate").Group("point_light")!;
            Assert.True(group.AddedByPlacement);
            Assert.Equal(OverrideSection.Component, group.Section);
            Assert.All(group.Rows, r => Assert.True(r.Editable));
            Assert.DoesNotContain("sage:point_light", Inspect(document, "crate").AddableComponents());

            // Its fields are set like any other; reverting one leaves the light (an empty body is what adds it).
            Assert.True(engine.CVars.Execute("ed_set crate point_light.range 12"));
            Assert.Equal(12f, world.Get<PointLight>(EntityOf(document, "crate")).Range);
            Assert.Equal("this placement (sandbox:market)", Inspect(document, "crate").Find("point_light.range")!.Provenance);
            document.History.EndMerge();
            Assert.True(engine.CVars.Execute("ed_revert crate point_light.range"));
            Assert.True(world.Has<PointLight>(EntityOf(document, "crate")));
            Assert.Equal(0f, world.Get<PointLight>(EntityOf(document, "crate")).Range);
            document.Undo();
            Assert.Equal(12f, world.Get<PointLight>(EntityOf(document, "crate")).Range);

            // Saved, reloaded and reopened: the one crate is still lit.
            Assert.True(engine.CVars.Execute("doc_save"));
            engine.CVars.Execute("doc_close");
            engine.Records.Reload();
            Assert.Equal(0, engine.Records.ErrorCount);
            engine.CVars.Execute("doc_open market");
            Assert.Equal(12f, world.Get<PointLight>(EntityOf(document, "crate")).Range);
            Assert.False(world.Has<PointLight>(EntityOf(document, "crate2")));
            Assert.True(Inspect(document, "crate").Group("sage:point_light")!.AddedByPlacement);

            // Taken off again, with its overrides; undo puts it back. A prefab's own cannot be removed.
            Assert.False(Inspect(document, "crate").RemoveComponent(Inspect(document, "crate").Group("timer")!, out why));
            Assert.True(engine.CVars.Execute("ed_remove_component crate point_light"));
            Assert.False(world.Has<PointLight>(EntityOf(document, "crate")));
            Assert.Null(document.Find("crate")!.Overrides);
            document.Undo();
            Assert.Equal(12f, world.Get<PointLight>(EntityOf(document, "crate")).Range);
        }
    }

    // A drag over a nested number is one undo, as one over a top-level field is; a change of a list's
    // shape is an edit of its own; what is not an element, or out of range, changes nothing.
    [Fact]
    public void ANestedDragIsOneUndoAndBadListEditsChangeNothing()
    {
        var (engine, document) = Open();
        using (engine)
        {
            for (int i = 1; i <= 4; i++)
            {
                var model = Inspect(document, "trader");
                Assert.True(model.Set(model.Find("inventory.items[0].count")!, JsonValue.Create(3 + i), out _));
            }
            Assert.Equal(1, document.History.Position);
            Assert.Equal("bread:7 apple:2", Carried(document));
            var again = Inspect(document, "trader");
            Assert.True(again.AddItem(again.Find("inventory.items")!, null, out _));
            Assert.Equal(2, document.History.Position);

            var now = Inspect(document, "trader");
            Assert.False(now.RemoveItem(now.Find("inventory.items")!, out string why));
            Assert.Contains("not an element", why);
            Assert.False(now.MoveItem(now.Find("inventory.items[0]")!, 0, out why));
            Assert.False(now.TryRemoveItem("inventory.items[9]", out why));
            Assert.False(now.TryAddItem("inventory.capacity", "", out why));
            Assert.Contains("not a list", why);
            Assert.Equal(2, document.History.Position);

            // ed_inspect lists a list's elements and their fields under it.
            string text = InspectorCommands.Describe(Inspect(document, "trader"));
            Assert.Contains("inventory.items = [3] *", text);
            Assert.Contains("inventory.items[0].count = 7", text);
        }
    }
}
