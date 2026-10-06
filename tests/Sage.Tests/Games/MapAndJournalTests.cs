#nullable enable
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The map and the journal in depth (issue #349), on the Sandbox's real content: the kit's own `rpg:map` and
// `rpg:journal`, the main scene's `area_map` (a picture, and fog in 8 m squares that lifts 16 m round the
// player), and "Thin the Wood", whose stages name the watcher and the hermit as their targets.
public class MapAndJournalTests
{
    public MapAndJournalTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Thin = new("sandbox", "thin_the_wood");
    private static readonly RecordId Main = new("sandbox", "main");

    private static UiInput ClickOn(UiLayer layer, Widget widget) =>
        UiInput.Click(layer.Root.ToPixels(new Vector2(widget.Rect.X + widget.Rect.Width * 0.5f, widget.Rect.Y + widget.Rect.Height * 0.5f)));

    private static Vector3 Absolute(World world, Entity entity) =>
        world.Origin().ToAbsolute(world.Get<Transform>(entity).LocalPosition);

    // The acceptance's first half: where the player has been comes out of the fog as they walk, the map's
    // fog rows are the squares still under it, and a save keeps what was explored.
    [Fact]
    public void ExploringLiftsTheMapsFogAndASaveKeepsIt()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        CameraRigTests.Step(world, 2);
        var player = SandboxScreensTests.Player(world);
        var map = AreaMapRecord.Of(world, Main);
        Assert.NotNull(map);
        Assert.Equal(8f, map!.Cell);

        var discovery = MapDiscovery.Of(world);
        var start = Absolute(world, player);
        Assert.True(discovery.IsDiscovered(Main, map.Cell, start));                       // where you stand, at once
        var east = start + new Vector3(40f, 0f, 0f);
        Assert.False(discovery.IsDiscovered(Main, map.Cell, east));                       // 40 m off: still fogged

        var stack = SandboxScreensTests.Stack(world);
        var layer = stack.Open(RpgKitModule.MapScreen, new UiBindContext(world, player));
        stack.Update(UiInput.Wait(0f));
        var view = Assert.IsType<MapView>(layer.Screen!.ViewModel);
        Assert.True(view.HasFog);
        int fogged = view.Fog.Count;
        Assert.True(fogged > 0);
        var fog = (Box)layer.Content.Find("fog")!;
        Assert.Equal(fogged, fog.ChildCount);                                              // a row per square of fog
        var square = fog.Child(0);
        var first = view.Fog[0];
        Assert.Equal(new Anchors(first.X, first.Y, first.X + first.W, first.Y + first.H), square.Anchors);   // x, y, w and h
        Assert.Equal("rpg:map_fog", square.Style);
        // No fog over the middle, where the player is.
        Assert.DoesNotContain(view.Fog, f => f.X <= 0.5f && 0.5f < f.X + f.W && f.Y <= 0.5f && 0.5f < f.Y + f.H);

        int version = discovery.Version;
        world.Teleport(player, Transform.At(world.Origin().ToOrigin(east) + new Vector3(0f, 1f, 0f)));
        CameraRigTests.Step(world, 2);
        Assert.True(discovery.Version > version);
        Assert.True(discovery.IsDiscovered(Main, map.Cell, east));                         // walked there: lifted
        Assert.True(discovery.IsDiscovered(Main, map.Cell, start));                        // and where you were, kept
        stack.Update(UiInput.Wait(0f));
        Assert.DoesNotContain(view.Fog, f => f.X <= 0.5f && 0.5f < f.X + f.W && f.Y <= 0.5f && 0.5f < f.Y + f.H);

        // Saved with the world: a load brings back what was explored.
        Assert.True(app.Engine.Saves.Save("explored"));
        world.Resources.Replace(new MapDiscovery());
        Assert.False(MapDiscovery.Of(world).IsDiscovered(Main, map.Cell, start));
        Assert.True(app.Engine.Saves.Load("explored"));
        var loaded = MapDiscovery.Of(app.World);
        Assert.True(loaded.IsDiscovered(Main, map.Cell, start));
        Assert.True(loaded.IsDiscovered(Main, map.Cell, east));
    }

    // The acceptance's second half: a quest the player tracks marks its target on the map — the watchers
    // while the hunt is on, the hermit once it is time to report — and one they stop tracking does not.
    [Fact]
    public void ATrackedQuestShowsItsTargetOnTheMap()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        CameraRigTests.Step(world, 2);
        var player = SandboxScreensTests.Player(world);
        var stack = SandboxScreensTests.Stack(world);
        var layer = stack.Open(RpgKitModule.MapScreen, new UiBindContext(world, player));
        stack.Update(UiInput.Wait(0f));
        var view = Assert.IsType<MapView>(layer.Screen!.ViewModel);
        Assert.DoesNotContain(view.Markers, m => m.Quest);

        Assert.True(Quests.Start(world, Thin));
        Assert.True(Quests.IsTracked(world, Thin));                                       // starting one tracks it
        stack.Update(UiInput.Wait(0f));
        var target = Assert.Single(view.Markers, m => m.Quest);
        Assert.Equal("watcher", target.Entity.Name);
        Assert.Equal("Thin the Wood", target.Label);
        Assert.Equal(Thin, target.QuestId);
        Assert.Equal(MapView.QuestStyle, target.Style);
        Assert.True(target.Discovered);                                                    // never hidden by the fog
        var markers = (Box)layer.Content.Find("markers")!;
        var row = Enumerable.Range(0, markers.ChildCount).Select(markers.Child).Single(r => r.Data == target);
        Assert.Equal("rpg:map_quest", row.Find("dot")!.Style);
        Assert.True(row.Visible);

        Assert.True(Quests.Track(world, Thin, false));
        stack.Update(UiInput.Wait(0f));
        Assert.DoesNotContain(view.Markers, m => m.Quest);

        Assert.True(Quests.Track(world, Thin));
        Assert.True(Quests.SetStage(world, Thin, "report"));
        stack.Update(UiInput.Wait(0f));
        target = Assert.Single(view.Markers, m => m.Quest);
        Assert.Equal("hermit", target.Entity.Name);                                        // the stage's own target

        Assert.True(Quests.Finish(world, Thin));
        stack.Update(UiInput.Wait(0f));
        Assert.DoesNotContain(view.Markers, m => m.Quest);                                 // done: nothing to go to
    }

    // The journal keeps the stages a quest moved on from (and a finished quest's last), and choosing an
    // unfinished quest's line tracks it or stops.
    [Fact]
    public void TheJournalKeepsFinishedStagesAndTracksAQuest()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        CameraRigTests.Step(world);
        var stack = SandboxScreensTests.Stack(world);
        var layer = stack.Open(RpgKitModule.JournalScreen, new UiBindContext(world, SandboxScreensTests.Player(world)));
        Assert.True(Quests.Start(world, Thin));
        Assert.True(Quests.SetStage(world, Thin, "report"));
        stack.Update(UiInput.Wait(0f));
        var journal = Assert.IsType<JournalView>(layer.Screen!.ViewModel);
        Assert.Equal(new[] { "hunt" }, Quests.JournalOf(world)!.Of(Thin)!.History);

        Assert.Equal(new[] { "Thin the Wood", "The hermit asked you to thin the watchers below.", "Tell the hermit it is done." },
                     journal.Lines.Select(l => l.Text));
        Assert.True(journal.Lines[0].Tracked);
        Assert.True(journal.Lines[1].Past);
        Assert.True(journal.Lines[2].Stage);
        var lines = (ItemList)layer.Content.Find("lines")!;
        Assert.True(lines.Child(0).Find("tracked")!.Visible);
        Assert.True(lines.Child(1).Find("past")!.Visible);
        Assert.False(lines.Child(1).Find("stage")!.Visible);

        // Choosing the quest's line stops tracking it; again, tracks it.
        stack.Update(UiInput.Wait(0f));                                                    // laid out, to click on
        stack.Update(ClickOn(layer, lines.Child(0)));
        Assert.False(Quests.IsTracked(world, Thin));
        stack.Update(UiInput.Wait(0f));
        Assert.False(journal.Lines[0].Tracked);
        Assert.False(lines.Child(0).Find("tracked")!.Visible);
        stack.Update(ClickOn(layer, lines.Child(0)));
        Assert.True(Quests.IsTracked(world, Thin));

        // Finished: the whole story, and nothing to track.
        Assert.True(Quests.Finish(world, Thin));
        stack.Update(UiInput.Wait(0f));
        Assert.Equal(new[] { "Thin the Wood", "The hermit asked you to thin the watchers below.", "Tell the hermit it is done." },
                     journal.Lines.Select(l => l.Text));
        Assert.True(journal.Lines[0].Done);
        Assert.False(journal.Lines[0].CanTrack);
        Assert.True(journal.Lines.Skip(1).All(l => l.Past));
        stack.Update(ClickOn(layer, lines.Child(0)));
        Assert.True(Quests.IsFinished(world, Thin));                                       // choosing it does nothing
    }

    // The scene's picture lies under the markers where its corners fall, in a box that cuts it off; the
    // map's buttons zoom it in and out and pan it, and Centre puts it back.
    [Fact]
    public void TheMapZoomsPansAndLaysItsPictureUnderTheMarkers()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        CameraRigTests.Step(world, 2);
        var player = SandboxScreensTests.Player(world);
        var stack = SandboxScreensTests.Stack(world);
        var layer = stack.Open(RpgKitModule.MapScreen, new UiBindContext(world, player));
        stack.Update(UiInput.Wait(0f));
        stack.Update(UiInput.Wait(0f));
        var view = Assert.IsType<MapView>(layer.Screen!.ViewModel);

        Assert.True(view.HasPicture);
        Assert.Equal("textures/map_main.png", view.Picture);
        int range = view.Range;
        Assert.Equal(128f / (2f * range), view.PictureW, 4);                                // 128 m across, in a map 2 × range wide
        var here = Absolute(world, player);
        Assert.Equal(0.5f + (448f - here.X) / (2f * range), view.PictureX, 3);             // its west edge, from the player in the middle
        Assert.Equal(0.5f + (448f - here.Z) / (2f * range), view.PictureY, 3);
        var area = (Box)layer.Content.Find("area")!;
        Assert.True(area.ClipChildren);
        var picture = (Image)layer.Content.Find("picture")!;
        Assert.Equal("textures/map_main.png", picture.Source);
        Assert.Equal(new Anchors(view.PictureX, view.PictureY, view.PictureX + view.PictureW, view.PictureY + view.PictureH), picture.Anchors);

        // Zoom in: half the reach, the picture twice as big.
        float width = view.PictureW;
        stack.Update(ClickOn(layer, layer.Content.Find("zoom_in")!));
        stack.Update(UiInput.Wait(0f));
        Assert.Equal(2f, view.Zoom);
        Assert.Equal(range / 2, view.Range);
        Assert.Equal(width * 2f, view.PictureW, 4);
        Assert.Equal(0.5f, view.YouX);

        // Pan east: the middle moves half the reach east, so the player is left of it.
        stack.Update(ClickOn(layer, layer.Content.Find("pan_east")!));
        stack.Update(UiInput.Wait(0f));
        Assert.True(view.Panned);
        Assert.Equal(range / 4f, view.Pan.X, 3);
        Assert.Equal(0.25f, view.YouX, 3);
        var you = layer.Content.Find("you")!;
        Assert.Equal(new Anchors(view.YouX, view.YouY, view.YouX, view.YouY), you.Anchors);

        stack.Update(ClickOn(layer, layer.Content.Find("recentre")!));
        stack.Update(UiInput.Wait(0f));
        Assert.False(view.Panned);
        Assert.Equal(1f, view.Zoom);
        Assert.Equal(range, view.Range);

        // Zoomed out as far as it goes, the button says no.
        for (int i = 0; i < 4; i++) view.ZoomOut();
        stack.Update(UiInput.Wait(0f));
        Assert.Equal(MapView.MinZoom, view.Zoom);
        Assert.False(layer.Content.Find("zoom_out")!.Enabled);
    }
}
