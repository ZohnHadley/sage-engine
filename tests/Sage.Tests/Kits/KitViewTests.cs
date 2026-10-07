#nullable enable
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The kit screens that were tested only on the Sandbox's content, or not opened as screens at all (issue
// #394): the journal, the map and the perks list, each opened from the RPG kit's own `screen` record on a
// bare kit app and driven as the client drives it — through the world's UiScreenStack, a pad or a click.
public class KitViewTests
{
    public KitViewTests() { _ = TestEnv.UserRoot; }

    private static RecordId Id(string name) => new("sage", name);

    private const string Records = """
    [
      { "type": "item", "id": "pelt", "label": "wolf pelt", "weight": 0.5, "maxStack": 10 },
      { "type": "quest", "id": "pelts", "label": "Two Pelts", "start": "gather",
        "stages": [
          { "id": "gather", "text": "Bring two pelts.",
            "objectives": [ { "kind": "Have", "item": "sage:pelt", "count": 2 } ], "next": "deliver" },
          { "id": "deliver", "text": "Take them to the tanner." } ] },

      { "type": "attribute", "id": "luck", "start": 40, "min": 0, "max": 100 },
      { "type": "attribute", "id": "perk_points", "start": 1, "min": 0 },
      { "type": "rpg_conventions", "id": "rules", "perkPoints": "perk_points" },
      { "type": "effect", "id": "lucky", "duration": "Infinite", "modifiers": [ { "attribute": "luck", "op": "Add", "value": 3 } ] },
      { "type": "perk", "id": "lucky", "name": "Lucky", "cost": 1, "effects": ["lucky"] },
      { "type": "perk", "id": "luckier", "name": "Luckier", "cost": 5, "effects": ["lucky"] }
    ]
    """;

    private static HeadlessApp Boot() =>
        HeadlessApp.Gameplay().With(new UiModule(), new RpgKitModule()).WithEngineContent()
            .File("data/kit_views.json", Records).Boot("kit-views");

    private static RpgScreenTests.Pad Open(HeadlessApp app, RecordId screen, Entity subject)
    {
        var stack = app.World.Resources.Get<UiScreenStack>();
        stack.CloseAll();
        var pad = new RpgScreenTests.Pad(stack, stack.Open(screen, new UiBindContext(app.World, subject)));
        stack.Update(UiInput.Wait(0f));
        return pad;
    }

    private static string Text(RpgScreenTests.Pad pad, string node) => pad.Screen.View.Find<Label>(node)!.Text;

    // Up to the top of a list, then down to the row that says `text`, as a player would.
    private static void Focus(RpgScreenTests.Pad pad, string text)
    {
        pad.Nav(UiNavigation.Up, 8);
        pad.NavUntil(UiNavigation.Down, p => p.Focused is ListRow row && row.Text == text);
    }

    private static UiInput ClickOn(UiLayer layer, Widget widget) =>
        UiInput.Click(layer.Root.ToPixels(new Vector2(widget.Rect.X + widget.Rect.Width * 0.5f, widget.Rect.Y + widget.Rect.Height * 0.5f)));

    // rpg:journal on the kit's own layout: "nothing yet" before a quest, then the quest, its stage and its
    // objective's progress as rows; a click on the quest's row tracks it or stops; moving on leaves the stage
    // it left as a `past` line, and a finished quest is one line marked done over the story so far.
    [Fact]
    public void TheJournalScreenListsAQuestItsStageAndProgressAndTracksIt()
    {
        using var app = Boot();
        var world = app.World;
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddInventory(hero, 0f);
        Assert.True(world.Give(hero, Id("pelt"), 1));

        var pad = Open(app, RpgKitModule.JournalScreen, hero);
        var view = Assert.IsType<JournalView>(pad.Screen.ViewModel);
        Assert.Equal(new RecordId("rpg", "journal"), pad.Screen.View.Layout);
        Assert.True(view.Empty);
        Assert.True(pad.Screen.View.Find<Label>("empty")!.Visible);

        Assert.True(Quests.Start(world, Id("pelts")));
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.False(view.Empty);
        Assert.False(pad.Screen.View.Find<Label>("empty")!.Visible);
        Assert.Equal((1, 0), (view.Active, view.Finished));
        Assert.Equal("1 quest on the go, 0 done", Text(pad, "summary"));
        Assert.Equal(new[] { "Two Pelts", "Bring two pelts." }, view.Lines.Take(2).Select(l => l.Text));
        Assert.True(view.Lines[0].Quest && view.Lines[0].CanTrack);
        Assert.True(view.Lines[1].Stage);
        var objective = Assert.Single(view.Lines, l => l.Objective);
        Assert.Equal("1/2", objective.Progress);

        var lines = (ItemList)pad.Screen.View.Find("lines")!;
        Assert.Equal(view.Lines.Count, lines.ChildCount);                                   // a row per line
        Assert.True(lines.Child(0).Find("quest")!.Visible);
        Assert.False(lines.Child(0).Find("stage")!.Visible);
        Assert.True(lines.Child(1).Find("stage")!.Visible);
        Assert.Equal("1/2", ((Label)lines.Child(2).Find("progress")!).Text);

        // A second pelt counts on the open screen.
        Assert.True(world.Give(hero, Id("pelt"), 1));
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.Equal("2/2", view.Lines.Single(l => l.Objective).Progress);

        // A click on the quest's row tracks it, or stops.
        bool tracked = Quests.IsTracked(world, Id("pelts"));
        pad.Stack.Update(ClickOn(pad.Layer, lines.Child(0)));
        Assert.Equal(!tracked, Quests.IsTracked(world, Id("pelts")));
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.Equal(!tracked, view.Lines[0].Tracked);
        Assert.Equal(!tracked, lines.Child(0).Find("tracked")!.Visible);
        pad.Stack.Update(ClickOn(pad.Layer, lines.Child(0)));
        Assert.Equal(tracked, Quests.IsTracked(world, Id("pelts")));

        // Moving on: the stage it left is history.
        Assert.True(Quests.SetStage(world, Id("pelts"), "deliver"));
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.Equal(new[] { "Two Pelts", "Bring two pelts.", "Take them to the tanner." }, view.Lines.Select(l => l.Text));
        Assert.True(view.Lines[1].Past);
        Assert.True(view.Lines[2].Stage);
        Assert.True(lines.Child(1).Find("past")!.Visible);

        // Finished: one line, done, over the story so far; nothing to track.
        Assert.True(Quests.Finish(world, Id("pelts")));
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.Equal((0, 1), (view.Active, view.Finished));
        Assert.True(view.Lines[0].Done && !view.Lines[0].CanTrack && !view.Lines[0].Failed);
        Assert.True(lines.Child(0).Find("done")!.Visible);
        Assert.DoesNotContain(view.Lines, l => l.Stage || l.Objective);
        Assert.All(view.Lines.Skip(1), l => Assert.True(l.Past));
    }

    // rpg:map on the kit's own layout, with no area_map: markers placed round the player with north up, the
    // reach grown in whole steps to hold the farthest, and the zoom, pan and recentre buttons moving them.
    [Fact]
    public void TheMapScreenPlacesMarkersRoundThePlayerAndItsButtonsZoomAndPan()
    {
        using var app = Boot();
        var world = app.World;
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        var hut = world.Create(Transform.At(new Vector3(15, 0, 0)), "hut");               // 15 m east
        world.Add(hut, new MapMarker { Label = "Hut" });
        var tower = world.Create(Transform.At(new Vector3(0, 0, -30)), "tower");          // 30 m north
        world.Add(tower, new MapMarker { Label = "Tower" });
        world.FlushCommands();
        world.RunFixed(1f / 60f);

        var pad = Open(app, RpgKitModule.MapScreen, hero);
        var view = Assert.IsType<MapView>(pad.Screen.ViewModel);
        Assert.Equal(new RecordId("rpg", "map"), pad.Screen.View.Layout);
        Assert.True(view.HasPlayer);
        Assert.False(view.HasPicture);
        Assert.False(view.HasFog);
        Assert.Equal(2, view.Count);
        Assert.Equal(40, view.Range);                                                       // 30 m × 1.1, up to a whole 10
        Assert.Equal("2 places within 40 m", Text(pad, "range"));

        var east = view.Markers.Single(m => m.Label == "Hut");
        var north = view.Markers.Single(m => m.Label == "Tower");
        Assert.Equal(0.5f + 15f / 80f, east.X, 3);
        Assert.Equal(0.5f, east.Y, 3);
        Assert.Equal(15, east.Distance);
        Assert.Equal(0.5f, north.X, 3);
        Assert.Equal(0.5f - 30f / 80f, north.Y, 3);                                        // north is up
        Assert.Equal((0.5f, 0.5f), (view.YouX, view.YouY));

        var markers = (Box)pad.Screen.View.Find("markers")!;
        Assert.Equal(2, markers.ChildCount);                                                // a row per marker
        Assert.All(Enumerable.Range(0, 2), i => Assert.True(markers.Child(i).Visible));

        // Zoom in: half the reach, the hut twice as far from the middle.
        pad.Stack.Update(ClickOn(pad.Layer, pad.Screen.View.Find("zoom_in")!));
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.Equal(2f, view.Zoom);
        Assert.Equal(20, view.Range);
        Assert.Equal(0.5f + 15f / 40f, east.X, 3);

        // Pan east by half the reach: the middle moves 10 m east and the player slides west of it.
        pad.Stack.Update(ClickOn(pad.Layer, pad.Screen.View.Find("pan_east")!));
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.True(view.Panned);
        Assert.Equal(0.5f - 10f / 40f, view.YouX, 3);
        Assert.Equal(0.5f + 5f / 40f, east.X, 3);
        Assert.Equal(15, east.Distance);                                                    // still from the player

        pad.Stack.Update(ClickOn(pad.Layer, pad.Screen.View.Find("recentre")!));
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.False(view.Panned);
        Assert.Equal(1f, view.Zoom);
        Assert.Equal(40, view.Range);
        Assert.Equal(0.5f, view.YouX, 3);
    }

    // rpg:perks on the kit's `list` layout: what it could take and what each costs, greyed with why not; A on
    // one it can afford takes it and pays its points, A on one it cannot says why and takes nothing.
    [Fact]
    public void ThePerksScreenTakesAPerkItCanAffordAndSaysWhyNotForTheRest()
    {
        using var app = Boot();
        var world = app.World;
        var hero = world.Create(Transform.At(Vector3.Zero), "hero");
        world.AddAttributes(hero);
        world.FlushCommands();

        var pad = Open(app, RpgKitModule.PerksScreen, hero);
        var view = Assert.IsType<PerksView>(pad.Screen.ViewModel);
        Assert.Equal(new RecordId("rpg", "list"), pad.Screen.View.Layout);
        Assert.Equal("Perks — 1 points", view.Title);
        Assert.Equal(new[] { "Luckier", "Lucky" }, view.Rows.Select(r => r.Text).OrderBy(t => t, System.StringComparer.Ordinal));
        var luckier = view.Rows.Single(r => r.Text == "Luckier");
        Assert.False(luckier.Enabled);
        Assert.Equal(ListRow.OffStyle, luckier.Style);
        Assert.Equal("not enough perk points", luckier.Reason);

        Focus(pad, "Luckier");
        Assert.Equal("not enough perk points", view.Reason);                               // the focused row's reason
        pad.A();
        Assert.Equal("not enough perk points", view.Message);
        Assert.Equal(1f, Perks.PointsOf(world, hero));

        Focus(pad, "Lucky");
        pad.A();
        world.RunFixed(1f / 60f);                                                           // the effect applies on the tick
        pad.Stack.Update(UiInput.Wait(0f));
        Assert.Equal(0f, Perks.PointsOf(world, hero));
        Assert.Equal(43f, world.Attribute(hero, Id("luck")), 3);
        Assert.Equal("Perks — 0 points", view.Title);
        Assert.True(view.Rows.Single(r => r.Text == "Lucky").Selected);
        Assert.Equal("•", view.Rows.Single(r => r.Text == "Lucky").Tick);
    }
}
