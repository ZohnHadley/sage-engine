#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;
using Sandbox;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The Sandbox's widget screens from its real content, headless (docs/design/13 "As built (the HUD,
// journal, map and menus)", issue #99): the main menu lists the saves (SaveSystem.Slots) and loading one
// from it restores the world; the journal and the map read the kit's view-models; the HUD is a layer that
// never takes input. What the client adds — drawing each layer — is the smoke run's.
public class SandboxScreensTests
{
    public SandboxScreensTests() { _ = TestEnv.UserRoot; }

    internal static string SandboxGame => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");

    internal static HeadlessApp Boot()
    {
        var app = HeadlessApp.ForGame(SandboxGame, new SandboxModule()).WithEngineContent().Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    internal static UiScreenStack Stack(World world)
    {
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        return stack;
    }

    internal static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static Entity Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().FirstOrDefault(e => e.Name == name);

    private static UiInput ClickOn(UiLayer layer, Widget widget) =>
        UiInput.Click(layer.Root.ToPixels(new Vector2(widget.Rect.X + widget.Rect.Width * 0.5f, widget.Rect.Y + widget.Rect.Height * 0.5f)));

    private static readonly RecordId MainMenu = new("sandbox", "main_menu");
    private static readonly RecordId Thin = new("sandbox", "thin_the_wood");
    private static readonly RecordId Sword = new("sandbox", "practice_sword");

    // The acceptance of #99: a save taken, the world changed after it — a quest started, an item picked
    // up, the hermit gone — and the main menu, chosen from its list, puts it back as it was saved.
    [Fact]
    public void TheMainMenuLoadsASaveSlotAndTheWorldIsRestored()
    {
        using var app = Boot();
        var world = app.World;
        CameraRigTests.Step(world, 3);
        int swords = world.CountOf(Player(world), Sword);
        Assert.False(Named(world, "hermit").IsNull);
        Assert.True(app.Engine.Saves.Save("before"));

        // What happens after the save.
        Assert.True(Quests.Start(world, Thin));
        Assert.True(world.Give(Player(world), Sword));
        world.Destroy(Named(world, "hermit"));
        CameraRigTests.Step(world, 2);
        Assert.True(Named(world, "hermit").IsNull);

        var stack = Stack(world);
        var layer = stack.Open(MainMenu, new UiBindContext(world, Player(world)));
        stack.Update(UiInput.Wait(0f));
        var menu = Assert.IsType<MainMenuView>(layer.Screen!.ViewModel);
        var slot = Assert.Single(menu.Slots);
        Assert.Equal("before", slot.Name);
        Assert.True(slot.CanLoad);

        var slots = (ItemList)layer.Content.Find("slots")!;
        var row = slots.Child(0);
        Assert.Same(slot, row.Data);                                         // the row is the slot it shows
        Assert.Equal("before", ((Label)row.Find("slotName")!).Text);
        stack.Update(ClickOn(layer, row));

        Assert.Equal("@sandbox.menu.loaded", menu.Message);
        Assert.False(stack.IsOpen);                                          // the menu closed over the restored world
        Assert.Null(Quests.JournalOf(world)?.Of(Thin));                      // the quest had not been started
        Assert.Equal(swords, world.CountOf(Player(world), Sword));           // nor the sword picked up
        Assert.False(Named(world, "hermit").IsNull);                         // and the hermit is back
        CameraRigTests.Step(world, 2);                                       // and the world runs on
    }

    // Save writes a new slot from the menu, which lists it at once; an unreadable save is listed and
    // refused with a reason rather than loaded.
    [Fact]
    public void TheMainMenuSavesToANewSlotAndRefusesOneItCannotRead()
    {
        using var app = Boot();
        var world = app.World;
        CameraRigTests.Step(world);
        var stack = Stack(world);
        var layer = stack.Open(MainMenu, new UiBindContext(world, Player(world)));
        stack.Update(UiInput.Wait(0f));
        var menu = (MainMenuView)layer.Screen!.ViewModel!;
        Assert.True(menu.NoSaves);
        Assert.True(layer.Content.Find("none")!.Visible);
        Assert.False(layer.Content.Find("list")!.Visible);

        stack.Update(ClickOn(layer, layer.Content.Find("save")!));
        Assert.Equal("@sandbox.menu.saved", menu.Message);
        Assert.Equal("save1", menu.MessageSlot);
        Assert.Equal("Saved as save1.", ((Label)layer.Content.Find("message")!).Text);
        Assert.Equal("save1", Assert.Single(menu.Slots).Name);
        Assert.True(stack.IsOpen);                                           // saving leaves the menu up

        // A save from a newer build: listed, greyed, and not loaded.
        var future = Path.Combine(app.Engine.Saves.Root, "future");
        Directory.CreateDirectory(future);
        File.WriteAllText(Path.Combine(future, "header.json"), $"{{ \"formatVersion\": {SaveSystem.FormatVersion + 1} }}");
        app.Engine.Saves.Rescan();
        stack.Update(UiInput.Wait(0f));
        var unreadable = Assert.Single(menu.Slots, s => s.Name == "future");
        Assert.False(unreadable.CanLoad);
        var slots = (ItemList)layer.Content.Find("slots")!;
        var row = Enumerable.Range(0, slots.ChildCount).Select(slots.Child).Single(r => r.Data == unreadable);
        Assert.True(row.Find("unreadable")!.Visible);
        stack.Update(UiInput.Wait(0f));
        stack.Update(ClickOn(layer, row));
        Assert.Equal("@sandbox.menu.cannot_load", menu.Message);
        Assert.True(stack.IsOpen);

        stack.Update(UiInput.Wait(0f));                                      // laid out again round the longer message
        stack.Update(ClickOn(layer, layer.Content.Find("resume")!));
        Assert.False(stack.IsOpen);
    }

    // SaveSystem.Slots: every slot's header, newest first, read once and again only after a save, a new
    // Root or Rescan; a save interrupted mid-write is not a slot.
    [Fact]
    public void SaveSlotsListTheHeadersNewestFirstAndAreReadAgainOnlyWhenTheyChange()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        Assert.Empty(saves.Slots);
        int version = saves.SlotsVersion;
        Assert.Equal(version, saves.SlotsVersion);                           // read once, kept

        Assert.True(saves.Save("first"));
        Thread.Sleep(20);
        Assert.True(saves.Save("second"));
        Assert.Equal(new[] { "second", "first" }, saves.Slots.Select(s => s.Name));
        Assert.True(saves.SlotsVersion > version);
        var slot = saves.Slots[0];
        Assert.Equal(SaveSystem.FormatVersion, slot.FormatVersion);
        Assert.True(slot.CanLoad);
        Assert.Equal(DateTimeKind.Utc, slot.SavedUtc.Kind);
        Assert.InRange(slot.SavedUtc, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(1));

        Directory.CreateDirectory(Path.Combine(saves.Root, "third.writing"));
        File.WriteAllText(Path.Combine(saves.Root, "third.writing", "header.json"), "{ \"formatVersion\": 2 }");
        version = saves.SlotsVersion;
        Assert.Equal(2, saves.Slots.Count);                                  // not scanned again: nothing said so
        saves.Rescan();
        Assert.Equal(2, saves.Slots.Count);                                  // and a staging folder is not a slot
        Assert.True(saves.SlotsVersion > version);
    }

    // The journal (the kit's view-model in the Sandbox's layout): a line per quest, its stage and its
    // objective with progress, and the quest finished shows as done without them.
    [Fact]
    public void TheJournalShowsEachQuestItsStageAndItsObjectives()
    {
        using var app = Boot();
        var world = app.World;
        CameraRigTests.Step(world);
        var stack = Stack(world);
        var layer = stack.Open(new RecordId("sandbox", "journal"), new UiBindContext(world, Player(world)));
        stack.Update(UiInput.Wait(0f));
        var journal = Assert.IsType<JournalView>(layer.Screen!.ViewModel);
        Assert.True(journal.Empty);
        Assert.True(layer.Content.Find("empty")!.Visible);

        Assert.True(Quests.Start(world, Thin));
        stack.Update(UiInput.Wait(0f));
        Assert.Equal(new[] { "Thin the Wood", "The hermit asked you to thin the watchers below." },
                     journal.Lines.Take(2).Select(l => l.Text));
        Assert.True(journal.Lines[0].Quest);
        Assert.True(journal.Lines[1].Stage);
        var objective = journal.Lines[2];
        Assert.True(objective.Objective);
        Assert.Equal("0/2", objective.Progress);
        Assert.False(layer.Content.Find("empty")!.Visible);
        var lines = (ItemList)layer.Content.Find("lines")!;
        Assert.Equal(3, lines.ChildCount);
        Assert.Equal("0/2", ((Label)lines.Child(2).Find("progress")!).Text);
        Assert.False(lines.Child(0).Find("stage")!.Visible);                 // a quest's line shows the quest label only
        Assert.Equal("1 quest on the go, 0 done", ((Label)layer.Content.Find("summary")!).Text);

        var before = journal.Lines[0];
        stack.Update(UiInput.Wait(0f));
        Assert.Same(before, journal.Lines[0]);                               // nothing changed: nothing rebuilt

        Assert.True(Quests.Finish(world, Thin));
        stack.Update(UiInput.Wait(0f));
        var done = Assert.Single(journal.Lines);
        Assert.True(done.Done);
        Assert.True(lines.Child(0).Find("done")!.Visible);
        Assert.Equal(1, journal.Finished);
    }

    // The map: markers only, from `rpg:map_marker`, placed round the player — east is right, north (−Z) up.
    [Fact]
    public void TheMapPlacesEachMarkerRoundThePlayerNorthUp()
    {
        using var app = Boot();
        var world = app.World;
        CameraRigTests.Step(world);
        var player = Player(world);
        var stack = Stack(world);
        var layer = stack.Open(new RecordId("sandbox", "map"), new UiBindContext(world, player));
        stack.Update(UiInput.Wait(0f));
        var map = Assert.IsType<MapView>(layer.Screen!.ViewModel);

        Assert.Contains(map.Markers, m => m.Label == "@sandbox.map.stranger");
        var fire = Assert.Single(map.Markers, m => m.Label == "@sandbox.map.campfire");
        Assert.Equal("sandbox:map_place", fire.Style);
        var here = world.Get<Transform>(player).LocalPosition;
        var there = world.Get<Transform>(fire.Entity).LocalPosition;
        Assert.Equal(there.X > here.X, fire.X > 0.5f);                       // the campfire is east of the start...
        Assert.Equal(there.Z < here.Z, fire.Y < 0.5f);                       // ...and north of it
        Assert.InRange(map.Range, (int)MapView.MinimumRange, 1000);
        Assert.True(map.Markers.All(m => m.X is >= 0f and <= 1f && m.Y is >= 0f and <= 1f));

        // Each marker is a row in the map's box, at its fraction of it (the `x` and `y` bindings).
        var markers = (Box)layer.Content.Find("markers")!;
        Assert.Equal(map.Count, markers.ChildCount);
        var row = Enumerable.Range(0, markers.ChildCount).Select(markers.Child).Single(r => r.Data == fire);
        Assert.Equal(new Anchors(fire.X, fire.Y, fire.X, fire.Y), row.Anchors);
        Assert.Equal("campfire", ((Label)row.Find("name")!).Text);
        Assert.Equal("sandbox:map_place", row.Find("dot")!.Style);
        Assert.Contains("within", ((Label)layer.Content.Find("range")!).Text);
    }
}

// The HUD as a layer (issue #99): drawn, never taking input, and a frame of it — its view-model read,
// its bindings refreshed and its render plan brought up to date — allocates nothing (02 §4.6).
[Collection(MeasurementsCollection.Name)]
public class SandboxHudAllocationTests
{
    public SandboxHudAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void TheHudIsAScreenRecordAndAFrameOfItAllocatesNothing()
    {
        using var app = SandboxScreensTests.Boot();
        var world = app.World;
        var stack = SandboxScreensTests.Stack(world);
        var styles = world.Resources.Get<UiStyles>();
        var text = world.Resources.Get<Localisation>();
        var hud = stack.OpenHud(new RecordId("sandbox", "hud"), new UiBindContext(world));
        Assert.False(hud.Modal);
        Assert.False(stack.IsOpen);                                          // a HUD is not a screen that has the input
        Assert.Null(stack.Top);

        // The Bepu step allocates 40 bytes a tick of its own even when idle: physics' to answer for.
        Assert.True(world.Systems.Disable("sage.physics.step"));
        world.Say("A message for the log.");
        world.Say("A good one.", MessageKind.Good);
        // A HUD frame: the stack's update (the view-model's Refresh, the bindings, layout) and the plan.
        // The simulation ticks between them, outside the measurement — the Sandbox's own fights are not
        // the HUD's to answer for — so what the HUD reads keeps moving (messages age out, the log fades).
        void Frame()
        {
            stack.Update(UiInput.Wait(1f / 60f));   // HudView.Refresh, the bindings, the layout
            hud.Plan.Update(hud.Root, styles, hud.Pressed, hud.PreviousFocus, text.Version);
        }
        for (int i = 0; i < 120; i++)
        {
            world.RunFixed(1f / 60f);
            world.RunFrame(1f / 60f, 1f);
            Frame();
        }

        var view = Assert.IsType<HudView>(hud.Screen!.ViewModel);
        Assert.True(view.HasPlayer);
        Assert.Matches(@"^\d+ / \d+$", view.HealthText);
        Assert.Equal(2, view.Messages.Count);
        Assert.Equal(HudView.GoodStyle, view.Messages[1].Style);
        Assert.Equal(view.HealthText, ((Label)hud.Content.Find("healthText")!).Text);
        var messages = (Stack)hud.Content.Find("messages")!;
        Assert.Equal("A good one.", ((Label)messages.Child(1)).Text);

        // An input the HUD would take if it could: nothing happens to it.
        stack.Update(UiInput.Nav(UiNavigation.Down));
        Assert.Null(hud.Root.Focused);

        AllocationProbe.AssertNone(300, Frame);

        // A HUD frame while the log ages under it (World.RunFrame's part, alone): the messages fade into
        // their last second's style and leave, and the rows go with them — nothing allocated.
        world.Say("Third.");
        world.Say("Fourth.", MessageKind.Bad);
        world.Say("Fifth.");
        world.Say("Sixth.");
        for (int i = 0; i < 10; i++) { world.RunFixed(1f / 60f); world.RunFrame(1f / 60f, 1f); Frame(); }
        Assert.Equal(HudView.MessageLines, view.Messages.Count);            // every row there can be is made
        AllocationProbe.AssertNone(330, () => { world.Messages().Advance(1f / 60f); Frame(); });
        Assert.Empty(view.Messages);                                         // all of them aged out while measured
    }
}
