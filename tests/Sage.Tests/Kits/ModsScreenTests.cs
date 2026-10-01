#nullable enable
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Kits.Rpg;
using Sage.UI;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The mods screen (phase 4j, issue 4j-6): the kit's `rpg:mods` over Sage.UI's ModsView, a view of the
// engine's ModManager. Clicking a switch or a move button writes the player's `mods.json` for the next
// start and says so; a refused mod is listed with its reason and has no switch; the second tab lists the
// conflicts between mods.
public class ModsScreenTests
{
    public ModsScreenTests() { _ = TestEnv.UserRoot; }

    private static void Write(string folder, string relative, string text)
    {
        string file = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    // A game with a prefab `modtest:rock`; mods aaa and bbb both rename it (a conflict), `coder` asks for code.
    private static string Game()
    {
        string dir = TestEnv.NewTempDir();
        Write(dir, "game.json", """{ "name": "Mod test", "id": "modtest", "mounts": ["content"], "version": "1.0.0" }""");
        Write(Path.Combine(dir, "content"), "data/rock.json", """[ { "type": "prefab", "id": "rock", "name": "rock" } ]""");
        string mods = Path.Combine(dir, "mods");
        foreach (string id in new[] { "aaa", "bbb" })
        {
            Write(Path.Combine(mods, id), "mod.json", $$"""{ "id": "{{id}}", "name": "The {{id}}", "version": "1.0.0", "game": "modtest" }""");
            Write(Path.Combine(mods, id), "data/rock.json", $$"""[ { "type": "prefab", "id": "modtest:rock", "patch": true, "name": "{{id}}'s rock" } ]""");
        }
        Write(Path.Combine(mods, "coder"), "mod.json", """{ "id": "coder", "version": "1.0.0", "game": "modtest", "kind": "code" }""");
        return dir;
    }

    private static void Click(UiScreenStack stack, UiLayer layer, Widget widget)
    {
        var centre = new Vector2(widget.Rect.X + widget.Rect.Width * 0.5f, widget.Rect.Y + widget.Rect.Height * 0.5f);
        stack.Update(UiInput.Click(layer.Root.ToPixels(centre)));
        stack.Update(UiInput.Wait(0f));
    }

    // The button `node` in the row of mod `id`.
    private static Widget Button(UiLayer layer, ModsView view, string id, string node)
    {
        var row = view.Mods.Single(r => r.Id == id);
        return All(layer.Content).Single(w => w.Name == node && ReferenceEquals(UiScreen.RowOf(w), row));
    }

    private static System.Collections.Generic.IEnumerable<Widget> All(Widget root)
    {
        yield return root;
        for (int i = 0; i < root.ChildCount; i++)
            foreach (var w in All(root.Child(i))) yield return w;
    }

    private static string[] Ids(ModsView view) => view.Mods.Select(r => r.Id).ToArray();

    [Fact]
    public void TheModsScreenTogglesAndReordersAndWritesTheListForTheNextStart()
    {
        string game = Game();
        string list = Path.Combine(TestEnv.NewTempDir(), "mods.json");
        using var app = HeadlessApp.ForGame(game).With(new RpgKitModule()).WithUserMods(null, list).Boot();
        var world = app.World;
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var layer = stack.Open(RpgKitModule.ModsScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(0f));
        var view = Assert.IsType<ModsView>(layer.Screen!.ViewModel);

        // Found: the active in load order, then the refused with why.
        Assert.Equal(new[] { "aaa", "bbb", "coder" }, Ids(view));
        Assert.Equal(ModsView.StateActive, view.Mods[0].State);
        Assert.True(view.Mods[2].Refused);
        Assert.Contains("code", view.Mods[2].Reason);
        Assert.False(view.Mods[2].CanToggle);
        Assert.False(view.RestartNeeded);
        Assert.False(File.Exists(list));

        // Later: bbb above aaa in the player's order, written to the list.
        Click(stack, layer, Button(layer, view, "aaa", ModsView.LaterButton));
        Assert.Equal(new[] { "bbb", "aaa", "coder" }, Ids(view));
        Assert.True(view.RestartNeeded);
        Assert.Equal("Restart to apply: mods are loaded once, at start.", layer.Content.Find<Label>("restart")!.Text);
        Assert.Equal(new[] { "bbb", "aaa" }, ModList.Load(list, out _).Order.Take(2));
        Assert.Equal(new[] { "aaa", "bbb" }, app.Engine.Mods.Active.Select(m => m.Id));   // this run is as it was

        // Switch bbb off: it moves below the active, and the list says so.
        Click(stack, layer, Button(layer, view, "bbb", ModsView.ToggleButton));
        Assert.Equal(new[] { "aaa", "bbb", "coder" }, Ids(view));
        Assert.False(view.Mods[1].Active);
        Assert.Equal(ModsView.StateOff, view.Mods[1].State);
        Assert.Equal(new[] { "bbb" }, ModList.Load(list, out _).Disabled);
        Click(stack, layer, Button(layer, view, "bbb", ModsView.ToggleButton));
        Assert.Empty(ModList.Load(list, out _).Disabled);
        Assert.Equal(new[] { "bbb", "aaa", "coder" }, Ids(view));

        // The next start, from that list: bbb first, so aaa wins the rock.
        using var next = HeadlessApp.ForGame(game).WithUserMods(null, list).Boot();
        Assert.Equal(new[] { "bbb", "aaa" }, next.Engine.Mods.Active.Select(m => m.Id));
        Assert.Equal("aaa's rock", next.Records.Get<PrefabRecord>(new RecordId("modtest", "rock")).Name);
    }

    [Fact]
    public void TheConflictsTabListsWhatTwoModsBothWrite()
    {
        string game = Game();
        string list = Path.Combine(TestEnv.NewTempDir(), "mods.json");
        using var app = HeadlessApp.ForGame(game).With(new RpgKitModule()).WithUserMods(null, list).Boot();
        var world = app.World;
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var layer = stack.Open(RpgKitModule.ModsScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(0f));
        var view = Assert.IsType<ModsView>(layer.Screen!.ViewModel);

        Assert.Empty(view.Conflicts);
        Click(stack, layer, layer.Content.Find(ModsView.ConflictsTab)!);
        Assert.True(view.ShowConflicts);
        Assert.Equal("prefab modtest:rock name: aaa, bbb; bbb won", Assert.Single(view.Conflicts).Line);
        Assert.Equal("prefab modtest:rock name: aaa, bbb; bbb won", layer.Content.Find<Label>("conflict")!.Text);
        Click(stack, layer, layer.Content.Find(ModsView.ModsTab)!);
        Assert.True(view.ShowMods);
    }

    [Fact]
    public void WithNoUserFolderAChangeIsRefusedAndSaysSo()
    {
        string game = Game();
        using var app = HeadlessApp.ForGame(game).With(new RpgKitModule()).Boot();   // no mods.json to write
        var world = app.World;
        var stack = world.Resources.Get<UiScreenStack>();
        stack.SetViewport(new Vector2(1280f, 720f));
        stack.OpenSeconds = stack.CloseSeconds = 0f;
        var layer = stack.Open(RpgKitModule.ModsScreen, new UiBindContext(world));
        stack.Update(UiInput.Wait(0f));
        var view = Assert.IsType<ModsView>(layer.Screen!.ViewModel);

        Click(stack, layer, Button(layer, view, "aaa", ModsView.ToggleButton));
        Assert.Equal("@rpg.mods.cannot_save", view.Message);
        Assert.False(view.RestartNeeded);
        Assert.True(view.Mods[0].Active);
    }
}
