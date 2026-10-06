#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Sage.Kits.Rpg;
using Sage.UI;
using Sandbox;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The menus round a playthrough (issue #342): a title before the world starts (game.json's `"title"`,
// Scenes.Title, Engine.BeginGame), the pause menu that stands the world still (a screen that `pauses`),
// and the save and load slot screens with their questions (UiScreenStack.Confirm, #343). Driven the way
// the client does, one UiScreenStack.Update a frame.
public class MenuScreenTests
{
    public MenuScreenTests() { _ = TestEnv.UserRoot; }

    private static HeadlessApp BootKit(HeadlessAppBuilder? builder = null)
    {
        var app = (builder ?? Kit()).Boot("menus");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static HeadlessAppBuilder Kit() => HeadlessApp.Gameplay().With(new UiModule(), new RpgKitModule());

    private static HeadlessApp BootSandbox(string savesRoot, bool atTitle)
    {
        var builder = HeadlessApp.ForGame(SandboxScreensTests.SandboxGame, new SandboxModule()).WithEngineContent();
        if (atTitle) builder.AtTitle();
        var app = builder.Boot();
        app.Engine.Saves.Root = savesRoot;
        return app;
    }

    private static UiScreenStack Stack(World world)
    {
        var stack = SandboxScreensTests.Stack(world);
        stack.Update(UiInput.Wait(1f));   // whatever opened with the world has faded in and been laid out
        return stack;
    }

    // The top layer's screen, by id, and its view-model.
    private static T Top<T>(UiScreenStack stack, RecordId screen) where T : class
    {
        var top = stack.Top;
        Assert.NotNull(top);
        Assert.Equal(screen, top!.Screen!.Id);
        return Assert.IsAssignableFrom<T>(top.Screen.ViewModel);
    }

    // Focus a widget of the top layer and press A on it, then an idle frame: what a pad does.
    private static void Press(UiScreenStack stack, Widget widget)
    {
        Assert.True(stack.Top!.Root.Focus(widget), $"'{widget.Name}' could not take focus");
        stack.Update(UiInput.Press);
        stack.Update(UiInput.Wait(0f));
    }

    private static void Press(UiScreenStack stack, string node) => Press(stack, stack.Top!.Content.Find(node)!);

    private static Widget RowButton(UiScreenStack stack, string slot, string button)
    {
        var rows = (ItemList)stack.Top!.Content.Find("slots")!;
        var row = Enumerable.Range(0, rows.ChildCount).Select(rows.Child)
            .Single(r => ((SaveSlotsView.Slot)r.Data!).Name == slot);
        return row.Find(button)!;
    }

    private static int Players(World world) =>
        world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList().Count;

    // The acceptance of #342, first half: the Sandbox, asked to show its title, boots to it — the world
    // furnished but waiting, paused, with no scene placed and nobody in it, the title on top of the HUD and
    // deaf to Back — and New game starts it in its scene with its player, the title gone.
    [Fact]
    public void TheSandboxBootsToItsTitleAndNewGameStartsTheWorld()
    {
        using var app = BootSandbox(Path.Combine(TestEnv.NewTempDir(), "saves"), atTitle: true);
        var world = app.World;
        var stack = Stack(world);
        Assert.True(Scenes.AtTitle(world));
        Assert.True(world.Paused);
        Assert.True(Scenes.Current(world).IsEmpty);
        Assert.Equal(0, Players(world));
        var title = Top<TitleView>(stack, RpgKitModule.TitleScreen);
        Assert.False(title.CanContinue);                                         // no saves yet
        Assert.False(stack.Top!.Content.Find("continue")!.Visible);
        Assert.False(app.Engine.Saves.Save("nothing"));                          // nothing to save at the title

        stack.Update(UiInput.Cancel);                                            // Back: the title stays
        stack.Update(UiInput.Wait(0f));
        Assert.Equal(RpgKitModule.TitleScreen, stack.Top!.Screen!.Id);

        Press(stack, RpgMenus.NewGameButton);
        Assert.False(Scenes.AtTitle(world));
        Assert.False(world.Paused);
        Assert.Equal(new RecordId("sandbox", "main"), Scenes.Current(world));
        Assert.Equal(1, Players(world));
        Assert.False(stack.IsOpen);                                              // only the HUD is left
        CameraRigTests.Step(world, 2);                                           // and the world runs
    }

    // The acceptance's second half: a save taken in one run, and the next run's title continues from it
    // through the load screen, whose row says when and where it was saved — and loading at the title asks
    // nothing (nothing would be lost). The world starts as the save left it.
    [Fact]
    public void TheTitleLoadsASaveSlotAndTheGameGoesOnFromIt()
    {
        string root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var sword = new RecordId("sandbox", "practice_sword");
        int swords;
        using (var first = BootSandbox(root, atTitle: false))
        {
            var player = SandboxScreensTests.Player(first.World);
            CameraRigTests.Step(first.World, 2);
            Assert.True(first.World.Give(player, sword));
            swords = first.World.CountOf(player, sword);
            Assert.True(first.Engine.Saves.Save("before the hut"));
        }

        using var app = BootSandbox(root, atTitle: true);
        var world = app.World;
        var stack = Stack(world);
        var title = Top<TitleView>(stack, RpgKitModule.TitleScreen);
        Assert.Equal("before the hut", title.ContinueSlot);
        Assert.True(stack.Top!.Content.Find("continue")!.Visible);

        Press(stack, RpgMenus.LoadButton);
        var load = Top<LoadGameView>(stack, RpgKitModule.LoadScreen);
        var slot = Assert.Single(load.Slots);
        Assert.Equal("main", slot.Location);                                     // where: the scene, from the header
        Assert.NotEqual("", slot.When);
        Assert.Equal("@rpg.saves.load", slot.Use);
        Assert.Equal("Load", ((Button)RowButton(stack, "before the hut", SaveSlotsView.UseButton)).Text);

        Press(stack, RowButton(stack, "before the hut", SaveSlotsView.UseButton));
        Assert.Null(load.Question);                                              // at the title: nothing to lose
        Assert.Equal("@rpg.saves.loaded", load.Message);
        Assert.False(Scenes.AtTitle(world));
        Assert.False(world.Paused);
        Assert.Equal(swords, world.CountOf(SandboxScreensTests.Player(world), sword));
        stack.Update(UiInput.Wait(1f));
        Assert.False(stack.IsOpen);                                              // the title and the load screen closed
        CameraRigTests.Step(world, 2);
    }

    // The pause menu stands the world still: no simulation step runs while it is open, the screens over it
    // (save, load) keep it so, and Resume lets it run. A world paused before it opened stays paused after.
    [Fact]
    public void ThePauseMenuStandsTheWorldStillUntilItCloses()
    {
        using var app = BootKit();
        var world = app.World;
        var stack = Stack(world);
        var time = WorldTime.Of(world);
        world.RunFixed(1f / 60f);
        double before = time.Scaled;
        Assert.True(before > 0);

        stack.Open(RpgKitModule.PauseScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(1f));
        Assert.True(stack.PausesWorld);
        Assert.True(stack.HoldsPause);
        Assert.True(world.Paused);
        for (int i = 0; i < 5; i++) world.RunFixed(1f / 60f);
        Assert.Equal(before, time.Scaled);                                       // simulation time stood still

        Press(stack, RpgMenus.SaveButton);                                       // the save screen, over it
        Top<SaveGameView>(stack, RpgKitModule.SaveScreen);
        stack.Update(UiInput.Cancel);                                            // back to the pause menu
        stack.Update(UiInput.Wait(1f));
        Assert.True(world.Paused);
        Top<PauseView>(stack, RpgKitModule.PauseScreen);

        Press(stack, RpgMenus.ResumeButton);
        Assert.False(stack.PausesWorld);
        Assert.False(world.Paused);
        world.RunFixed(1f / 60f);
        Assert.True(time.Scaled > before);

        // Paused by somebody else first (the `pause` command): the menu leaves it so.
        world.Paused = true;
        stack.Open(RpgKitModule.PauseScreen, new UiBindContext(world));
        Assert.False(stack.HoldsPause);
        stack.CloseAll();
        Assert.True(world.Paused);
    }

    // The save screen: a new slot, then saving over it only once the question is answered yes — and a save
    // taken from under a pause menu does not load paused. Delete asks too, and No keeps the slot.
    [Fact]
    public void TheSaveScreenAsksBeforeOverwritingOrDeleting()
    {
        using var app = BootKit();
        var world = app.World;
        var saves = app.Engine.Saves;
        var stack = Stack(world);
        world.RunFixed(1f / 60f);
        stack.Open(RpgKitModule.PauseScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(1f));
        Press(stack, RpgMenus.SaveButton);
        var view = Top<SaveGameView>(stack, RpgKitModule.SaveScreen);
        Assert.True(view.NoSaves);
        Assert.True(stack.Top!.Content.Find("new")!.Visible);

        Press(stack, SaveSlotsView.NewButton);
        Assert.Equal("@rpg.saves.saved", view.Message);
        Assert.Equal("save1", Assert.Single(view.Slots).Name);
        Assert.Equal("Saved as save1.", ((Label)stack.Top!.Content.Find("message")!).Text);
        Assert.True(world.Paused);                                               // still under the menu
        var written = saves.Slots.Single().SavedUtc;

        // Overwrite: a question first, over the save screen; No changes nothing, Yes saves over it.
        Thread.Sleep(20);
        Press(stack, RowButton(stack, "save1", SaveSlotsView.UseButton));
        var question = view.Question!;
        Assert.True(question.IsOpen);
        Assert.Same(question.Layer, stack.Top);
        Assert.Equal("save1 will be replaced by the game as it is now.", question.Message.Text);
        Assert.Equal("Save over", question.ConfirmButton.Text);
        question.Cancel();
        stack.Update(UiInput.Wait(1f));
        Assert.Equal(written, saves.Slots.Single().SavedUtc);
        Press(stack, RowButton(stack, "save1", SaveSlotsView.UseButton));
        view.Question!.Confirm();
        stack.Update(UiInput.Wait(1f));
        Assert.Equal("@rpg.saves.overwritten", view.Message);
        Assert.True(saves.Slots.Single().SavedUtc > written);

        // What was written was the running world, not the menu's pause.
        Assert.True(saves.Load("save1"));
        Assert.False(WorldTime.Of(world).Paused);

        // A quick-save is the engine's: listed, not saved over from here.
        saves.QuickSave();
        stack.Update(UiInput.Wait(0f));
        Assert.False(view.Slots.Single(s => s.Name == SaveSystem.QuickSlot).CanUse);
        Assert.Equal("@rpg.saves.kind_quick", view.Slots.Single(s => s.Name == SaveSystem.QuickSlot).Kind);

        // Delete: No keeps it, Yes removes it.
        Press(stack, RowButton(stack, "save1", SaveSlotsView.DeleteButton));
        view.Question!.Cancel();
        stack.Update(UiInput.Wait(1f));
        Assert.True(saves.Exists("save1"));
        Press(stack, RowButton(stack, "save1", SaveSlotsView.DeleteButton));
        Assert.Equal("save1 will be gone for good.", view.Question!.Message.Text);
        view.Question!.Confirm();
        stack.Update(UiInput.Wait(1f));
        Assert.Equal("@rpg.saves.deleted", view.Message);
        Assert.False(saves.Exists("save1"));
        Assert.DoesNotContain(view.Slots, s => s.Name == "save1");
    }

    // In a game, loading asks first (what was not saved would be lost); yes loads it and closes every menu,
    // and the world runs again. A slot this build cannot read is listed, and refused without a question.
    [Fact]
    public void TheLoadScreenAsksInAGameAndRefusesWhatItCannotRead()
    {
        using var app = BootKit();
        var world = app.World;
        var saves = app.Engine.Saves;
        world.RunFixed(1f / 60f);
        var thing = world.Create(Transform.At(Vector3.Zero), "keepsake");
        world.MakePersistent(thing);
        Assert.True(saves.Save("mine"));
        world.Destroy(world.FindByName("keepsake"));
        var future = Path.Combine(saves.Root, "future");
        Directory.CreateDirectory(future);
        File.WriteAllText(Path.Combine(future, "header.json"), $"{{ \"formatVersion\": {SaveSystem.FormatVersion + 1} }}");
        saves.Rescan();

        var stack = Stack(world);
        stack.Open(RpgKitModule.PauseScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(1f));
        Press(stack, RpgMenus.LoadButton);
        var view = Top<LoadGameView>(stack, RpgKitModule.LoadScreen);
        Assert.Equal(2, view.SlotCount);
        var unreadable = view.Slots.Single(s => s.Name == "future");
        Assert.True(unreadable.CannotLoad);
        Assert.False(unreadable.CanUse);

        view.Activate(RowButton(stack, "future", "slotTitle"), stack.Top!.Screen!.Context);   // the row itself
        Assert.Equal("@rpg.saves.cannot_load", view.Message);
        Assert.Null(view.Question);

        Press(stack, RowButton(stack, "mine", SaveSlotsView.UseButton));
        Assert.Equal("Anything since your last save will be lost when mine loads.", view.Question!.Message.Text);
        view.Question!.Confirm();
        stack.Update(UiInput.Wait(1f));
        Assert.Equal("@rpg.saves.loaded", view.Message);
        Assert.False(world.FindByName("keepsake").IsNull);
        Assert.False(stack.IsOpen);                                              // every menu closed
        Assert.False(world.Paused);                                              // and the world runs
    }

    // Quit from the pause menu asks first; No stays in the game, Yes runs the host's `quit`.
    [Fact]
    public void QuitFromThePauseMenuAsksFirst()
    {
        int quits = 0;
        using var app = BootKit(Kit().OnRegistered(a => a.CVars.RegisterCommand("quit", CVarFlags.None, "test quit", _ => quits++)));
        var world = app.World;
        var stack = Stack(world);
        stack.Open(RpgKitModule.PauseScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(1f));
        var pause = Top<PauseView>(stack, RpgKitModule.PauseScreen);
        Assert.True(pause.HasOptions);                                           // the kit's options screen (#339)
        Assert.True(stack.Top!.Content.Find("options")!.Visible);
        Assert.True(pause.HasMods);

        Press(stack, RpgMenus.QuitButton);
        Assert.Equal("Quit the game?", pause.QuitQuestion!.Title.Text);
        stack.Update(UiInput.Cancel);                                            // Back is No
        stack.Update(UiInput.Wait(1f));
        Assert.Equal(UiDialogResult.Cancelled, pause.QuitQuestion.Result);
        Assert.Equal(0, quits);

        Press(stack, RpgMenus.QuitButton);
        pause.QuitQuestion!.Confirm();
        Assert.Equal(1, quits);
    }

    // The title in the base: a world made with Scenes.Title waits, a save cannot be taken, and `new_game`
    // starts it — the rules start then, not before. A title naming no screen is a load error.
    [Fact]
    public void AWorldWaitsAtTheTitleUntilNewGame()
    {
        using var app = BootKit(Kit().AtTitle("rpg:title"));
        var world = app.World;
        Assert.Equal(RpgKitModule.TitleScreen, app.Engine.Scenes.Title);
        Assert.True(Scenes.AtTitle(world));
        Assert.Equal(RpgKitModule.TitleScreen, world.Resources.Get<UiScreenStack>().Top!.Screen!.Id);
        Assert.False(app.Engine.Saves.Save("x"));

        app.CVars.Execute("new_game");
        Assert.False(Scenes.AtTitle(world));
        Assert.False(world.Paused);
        Assert.False(app.Engine.BeginGame(world));                               // once only

        // Without AtTitle a game's title is checked but not shown: the world starts at once.
        using (var shown = BootKit(Kit()))
            Assert.False(Scenes.AtTitle(shown.World));
        var bad = Assert.Throws<InvalidDataException>(() => Kit().AtTitle("rpg:nope").Build().Dispose());
        Assert.Contains("rpg:nope", bad.Message);
    }
}
