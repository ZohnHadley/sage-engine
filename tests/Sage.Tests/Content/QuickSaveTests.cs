#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

[Component("test:quick_count")]
public struct QuickCount : IComponent
{
    public int Value;
}

// Quick-save and autosave (REDESIGN §4.5, phase 4i issue 4i-6): a save asked for during a tick runs when
// the tick ends; F5 and F9 (the QuickSave and QuickLoad actions) and the `quicksave` and `quickload`
// commands round-trip; autosaves rotate through their slots, reusing the oldest, and are taken on a
// scene change and on a timer; a slot says what kind of save it is; and a slot can be deleted.
public class QuickSaveTests
{
    public QuickSaveTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "prefab", "id": "crate", "name": "crate", "components": { "test:quick_count": { "value": 1 } } },
      { "type": "scene", "id": "main", "place": [ { "prefab": "crate", "at": [1, 0, 0], "name": "crate" } ] },
      { "type": "scene", "id": "cave", "place": [ { "prefab": "crate", "at": [5, 0, 0], "name": "crate" } ] }
    ]
    """;

    private const float Dt = 1f / 60f;

    private static HeadlessApp Boot()
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Mount("game", "game");
        var app = HeadlessApp.Gameplay().Mount(files).StartScene("game:main").Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        app.World.RunFixed(Dt);
        return app;
    }

    private static ref QuickCount Count(World world) =>
        ref world.Get<QuickCount>(Assert.Single(world.QueryAll().Entities.ToEntityList().Where(e => e.Name == "crate").ToArray()));

    private static void Step(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(Dt);
    }

    private static string[] SlotNames(SaveSystem saves) => saves.Slots.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // A system that does something once, in the phase it was added to.
    private sealed class Once : ISystem
    {
        private readonly Action<World> _act;
        private bool _done;
        public Once(Action<World> act) { _act = act; }
        public void Run(in SystemContext ctx)
        {
            if (_done) return;
            _done = true;
            _act(ctx.World);
        }
    }

    // Asked for in Gameplay, the save waits: a later phase of the same tick does not see it written, and
    // what that phase changes is in it, because it is taken once the whole tick has run.
    [Xunit.Fact]
    public void ASaveAskedForMidTickRunsAtTheTickBoundary()
    {
        using var app = Boot();
        var world = app.World;
        var saves = app.Engine.Saves;
        bool returned = false, writtenInGameplay = true, writtenInLate = true, pendingInLate = false;
        world.AddSystem(new Once(_ =>
        {
            returned = saves.Save("mid");
            writtenInGameplay = saves.Exists("mid");
        }), Phase.Gameplay);
        world.AddSystem(new Once(w =>
        {
            writtenInLate = saves.Exists("mid");
            pendingInLate = saves.HasPendingRequests;
            Count(w).Value = 5;   // after the request, in the same tick
        }), Phase.Late);

        world.RunFixed(Dt);

        Assert.True(returned);              // accepted, as a request
        Assert.False(writtenInGameplay);
        Assert.False(writtenInLate);
        Assert.True(pendingInLate);
        Assert.True(saves.Exists("mid"));   // written when the tick ended
        Assert.False(saves.HasPendingRequests);

        Count(world).Value = 9;
        Assert.True(saves.Load("mid"));
        Assert.Equal(5, Count(world).Value);   // the state at the end of the tick
    }

    // Several things asked for in one tick: the saves run first (two to one slot are one write, the later
    // kind winning), then the load; the saves record the tick that ran, not the state loaded.
    [Xunit.Fact]
    public void SavesAskedForInOneTickRunBeforeTheLoad()
    {
        using var app = Boot();
        var world = app.World;
        var saves = app.Engine.Saves;
        saves.QuickSave();                     // between ticks: at once
        Assert.True(saves.Exists(SaveSystem.QuickSlot));
        Count(world).Value = 2;

        world.AddSystem(new Once(_ =>
        {
            saves.QuickLoad();
            saves.RequestSave("after");
            saves.RequestSave("after", SaveKind.Quick);
        }), Phase.Gameplay);
        world.RunFixed(Dt);

        Assert.Equal(1, Count(world).Value);   // the load ran, last
        var after = Assert.Single(saves.Slots, s => s.Name == "after");
        Assert.Equal(SaveKind.Quick, after.Kind);
        Assert.True(saves.Load("after"));
        Assert.Equal(2, Count(world).Value);   // the save had the tick before the load
    }

    // F5 and F9 are the engine's QuickSave and QuickLoad actions, read from the tick's command; the
    // console's quicksave and quickload do the same.
    [Xunit.Fact]
    public void AQuickSaveThenAQuickLoadRoundTrips()
    {
        using var app = Boot();
        var world = app.World;
        var saves = app.Engine.Saves;
        var input = world.Resources.GetOrAdd(() => new PlayerInput());
        var actions = app.Engine.Actions;
        var quickSave = actions.Get(SaveSystem.QuickSaveAction);
        var quickLoad = actions.Get(SaveSystem.QuickLoadAction);
        Assert.True(quickSave.IsValid && quickLoad.IsValid);

        void Press(ActionId action)
        {
            input.HasCommand = true;
            input.Command = new PlayerCommand { Tick = world.Tick + 1, Pressed = new ActionMask().With(action) };
            world.RunFixed(Dt);
            input.Command = default;
        }

        Count(world).Value = 3;
        Press(quickSave);
        Assert.Equal(SaveKind.Quick, Assert.Single(saves.Slots).Kind);
        Count(world).Value = 8;
        Press(quickLoad);
        Assert.Equal(3, Count(world).Value);

        Count(world).Value = 4;
        app.CVars.Execute("quicksave");
        Count(world).Value = 6;
        app.CVars.Execute("quickload");
        Assert.Equal(4, Count(world).Value);
        Assert.Equal(new[] { SaveSystem.QuickSlot }, SlotNames(saves));
    }

    // Three autosave slots by default: the fourth autosave goes over the oldest, the first.
    [Xunit.Fact]
    public void AutosavesRotateAndReuseTheOldestSlot()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        for (int i = 1; i <= 3; i++)
        {
            Count(app.World).Value = i;
            saves.Autosave();
        }
        Assert.Equal(new[] { "autosave1", "autosave2", "autosave3" }, SlotNames(saves));
        Assert.Equal("autosave1", saves.NextAutosaveSlot());

        Count(app.World).Value = 4;
        saves.Autosave();
        Assert.Equal(new[] { "autosave1", "autosave2", "autosave3" }, SlotNames(saves));
        Assert.Equal("autosave1", saves.Slots[0].Name);   // newest first
        Assert.Equal("autosave2", saves.NextAutosaveSlot());
        Assert.True(saves.Load("autosave1"));
        Assert.Equal(4, Count(app.World).Value);

        app.CVars.Execute("save_autosave_slots 4");     // more slots: the new one is used first
        Assert.Equal("autosave4", saves.NextAutosaveSlot());
    }

    // A scene change autosaves at the end of the next tick, in the new scene; the timer autosaves after
    // save_autosave_interval seconds of play since the last save; save_autosave 0 stops both.
    [Xunit.Fact]
    public void AnAutosaveRunsOnASceneChangeAndOnTheTimer()
    {
        using var app = Boot();
        var world = app.World;
        var saves = app.Engine.Saves;
        app.CVars.Execute("save_autosave_interval 1");
        app.CVars.Execute("sv_cheats 1");
        app.CVars.Execute("scene_load cave");
        Assert.Empty(saves.Slots);                      // not yet: at the tick boundary
        world.RunFixed(Dt);
        var scene = Assert.Single(saves.Slots);
        Assert.Equal(("autosave1", SaveKind.Auto), (scene.Name, scene.Kind));
        var file = JsonNode.Parse(File.ReadAllText(Path.Combine(saves.Root, "autosave1", "world_main.json")))!;
        Assert.Equal("game:cave", (string?)file["scene"]);

        Step(world, 30);                                // half a second since that save
        Assert.Single(saves.Slots);
        Step(world, 40);
        Assert.Equal(new[] { "autosave1", "autosave2" }, SlotNames(saves));
        Assert.Equal(SaveKind.Auto, saves.Slots[0].Kind);

        app.CVars.Execute("save_autosave 0");
        Step(world, 90);
        app.CVars.Execute("scene_load main");
        Step(world, 2);
        Assert.Equal(2, saves.Slots.Count);
    }

    // Each slot says who asked for it; a save from before headers said is a manual one.
    [Xunit.Fact]
    public void SlotsReportTheirKind()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        Assert.True(saves.Save("mine"));
        saves.QuickSave();
        saves.Autosave();
        Assert.Equal(SaveKind.Manual, saves.Slots.Single(s => s.Name == "mine").Kind);
        Assert.Equal(SaveKind.Quick, saves.Slots.Single(s => s.Name == SaveSystem.QuickSlot).Kind);
        Assert.Equal(SaveKind.Auto, saves.Slots.Single(s => s.Name == "autosave1").Kind);
        var header = JsonNode.Parse(File.ReadAllText(Path.Combine(saves.Root, "autosave1", "header.json")))!;
        Assert.Equal("auto", (string?)header["kind"]);

        header["kind"] = null;
        var old = Path.Combine(saves.Root, "old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "header.json"), header.ToJsonString());
        saves.Rescan();
        Assert.Equal(SaveKind.Manual, saves.Slots.Single(s => s.Name == "old").Kind);
    }

    // Delete takes the slot off the disk and out of the listing.
    [Xunit.Fact]
    public void DeletingASlotRemovesIt()
    {
        using var app = Boot();
        var saves = app.Engine.Saves;
        Assert.True(saves.Save("a"));
        Assert.True(saves.Save("b"));
        Assert.True(saves.Delete("a"));
        Assert.False(saves.Exists("a"));
        Assert.False(Directory.Exists(Path.Combine(saves.Root, "a")));
        Assert.Equal(new[] { "b" }, SlotNames(saves));
        Assert.False(saves.Delete("a"));               // nothing left to delete
        app.CVars.Execute("save_delete b");
        Assert.Empty(saves.Slots);
    }
}
