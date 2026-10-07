#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Several documents open at once, and saving each to the right place (issue #375).
//
// The editor's tabs are EditorWorkspace: each tab a document in an edit world of its own, with its own undo
// history, one of them active. While modding (`ed_mod <id>`) the mod's folder is the only one written: a
// mod's level saves into it, and the shipped game's own level, which a modder cannot change in place, saves
// as a patch there. Checked against the files, and against what the record loader makes of them.
public class EditorWorkspaceTests
{
    public EditorWorkspaceTests() { _ = TestEnv.UserRoot; }

    // The game: two scenes, each naming a placements document. The mod: a level of its own (a scene and its
    // document), placing the game's prefab.
    private const string GameRecords = """
    [
      { "type": "prefab", "id": "thing", "name": "thing", "components": { "transform": {} } },
      { "type": "placements", "id": "yard", "place": [
          { "prefab": "thing", "at": [0, 0, 0], "name": "gate" },
          { "prefab": "thing", "at": [5, 0, 0], "name": "door" } ] },
      { "type": "placements", "id": "cellar_placements", "place": [ { "prefab": "thing", "at": [1, -3, 1], "name": "barrel" } ] },
      { "type": "scene", "id": "main", "placements": ["yard"] },
      { "type": "scene", "id": "cellar", "placements": ["cellar_placements"] }
    ]
    """;

    private const string ModRecords = """
    [
      // The mod's own level.
      { "type": "placements", "id": "tower_placements", "place": [ { "prefab": "game:thing", "at": [0, 10, 0], "name": "bell" } ] },
      { "type": "scene", "id": "tower", "placements": ["tower_placements"] }
    ]
    """;

    private static readonly RecordId Yard = new("game", "yard");
    private static readonly RecordId Cellar = new("game", "cellar_placements");
    private static readonly RecordId Tower = new("tweaks", "tower_placements");
    private static readonly RecordId Thing = new("game", "thing");

    private static (HeadlessApp App, MountFixture Files) NewGame()
    {
        var files = new MountFixture();
        files.Write("game", "data/level.json", GameRecords);
        files.Write("mods/tweaks", "data/tower.json", ModRecords);
        files.Mount("game", "game");
        files.Mount("mods/tweaks", "tweaks");   // as ModManager mounts a mod: `mods/<id>`, in the mod's namespace
        var app = HeadlessApp.Bare().With(new PhysicsModule()).Mount(files).StartScene("game:main").Build();
        return (app, files);
    }

    // The host's edit world as the first tab, as DevTools makes it.
    private static (EditorWorkspace Workspace, EditDocument First) NewWorkspace(HeadlessApp app)
    {
        var workspace = new EditorWorkspace(app.Engine);
        var first = workspace.Add(app.App.CreateEditWorld("edit"));
        return (workspace, first);
    }

    private static int Placed(World world) => world.Query<FromPlacements>().Count;

    [Fact]
    public void EachTabIsADocumentInAWorldOfItsOwnWithItsOwnUndo()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var (workspace, first) = NewWorkspace(app);
            var switches = new List<(EditDocument? Old, EditDocument? New)>();
            workspace.ActiveChanged += (old, now) => switches.Add((old, now));

            // The first tab is empty and in the yard's scene, so the yard opens there rather than in a new world.
            Assert.Same(first, workspace.Open(Yard));
            Assert.Equal(1, workspace.Count);
            int worlds = app.Engine.Worlds.Count;

            // The cellar is another scene's: a tab of its own, in an edit world placed with that scene.
            var cellar = workspace.Open(Cellar)!;
            Assert.NotNull(cellar);
            Assert.Equal(2, workspace.Count);
            Assert.Equal(1, workspace.ActiveIndex);
            Assert.Same(cellar, workspace.Active);
            Assert.Equal(worlds + 1, app.Engine.Worlds.Count);
            Assert.NotSame(first.World, cellar.World);
            Assert.True(cellar.World.Editing);
            Assert.Equal(new RecordId("game", "cellar"), Scenes.Current(cellar.World));
            Assert.Equal(2, Placed(first.World));
            Assert.Equal(1, Placed(cellar.World));
            Assert.Equal((first, cellar), (switches[^1].Old, switches[^1].New));

            // An edit in one tab is that tab's: its world, its history; the other's are untouched.
            Assert.NotNull(Placing.Place(cellar, Thing, new Vector3(2, -3, 2), 0f, "crate"));
            Assert.Equal(2, Placed(cellar.World));
            Assert.Equal(2, Placed(first.World));
            Assert.True(cellar.Dirty);
            Assert.False(first.Dirty);
            Assert.False(first.History.CanUndo);

            // Opening an open document switches to its tab; undo there undoes that tab's edits only.
            Assert.Same(first, workspace.Open(Yard));
            Assert.Equal(0, workspace.ActiveIndex);
            Assert.Equal(worlds + 1, app.Engine.Worlds.Count);
            Assert.False(workspace.Active!.Undo());
            Assert.True(workspace.Activate(cellar));
            Assert.True(workspace.Active!.Undo());
            Assert.Equal(1, Placed(cellar.World));
            Assert.Equal(2, Placed(first.World));

            // Closing: a document with unsaved changes stays unless forced; a tab the workspace made takes
            // its world with it.
            Assert.True(workspace.Active.Redo());
            Assert.False(workspace.Close(1));
            Assert.Equal(2, workspace.Count);
            Assert.True(workspace.Close(1, force: true));
            Assert.Equal(1, workspace.Count);
            Assert.DoesNotContain(cellar.World, app.Engine.Worlds);
            Assert.Same(first, workspace.Active);
            Assert.Equal((cellar, first), (switches[^1].Old, switches[^1].New));

            // The host's tab is never removed and its world never destroyed: closing it closes its document.
            Assert.True(workspace.Close(0));
            Assert.Equal(1, workspace.Count);
            Assert.False(first.IsOpen);
            Assert.Contains(first.World, app.Engine.Worlds);
            Assert.Equal(0, Placed(first.World));

            Assert.Null(workspace.Open(new RecordId("game", "nowhere")));
            Assert.Equal(1, workspace.Count);
        }
    }

    // The issue's "done": a modder opens a shipped game's level and a mod's level in tabs, and saves into the
    // mod's folder. The game's files are not written; the mod's level is saved in its own file; the game's
    // level is saved as a patch in the mod, which the record loader then applies.
    [Fact]
    public void AModderSavesBothTabsIntoTheModAndTheGamesLevelAsAPatch()
    {
        var (app, files) = NewGame();
        using (app)
        {
            string gameFile = Path.Combine(files.Dir("game"), "data", "level.json");
            string gameText = File.ReadAllText(gameFile);
            var (workspace, _) = NewWorkspace(app);
            Assert.True(workspace.SetTarget("tweaks", out string error), error);
            Assert.Equal("mods/tweaks", workspace.Target!.Name);

            var yard = workspace.Open(Yard)!;
            var tower = workspace.Open(Tower)!;
            Assert.Equal(2, workspace.Count);
            Assert.True(yard.SavesAsPatch);    // the shipped game's: read-only here
            Assert.False(tower.SavesAsPatch);  // the mod's own
            Assert.Contains("(patch)", yard.Title);

            Assert.NotNull(Placing.Place(yard, Thing, new Vector3(3, 0, 3), 0f, "statue"));
            var gate = yard.Find("gate")!;
            yard.Execute(new SetPlacement(yard, gate, PlacementFields.Of(gate) with { At = new Vector3(0, 0, -2) }));
            Assert.NotNull(Placing.Place(tower, Thing, new Vector3(0, 12, 0), 0f, "flag"));

            Assert.True(yard.Save());
            Assert.True(tower.Save());
            Assert.False(yard.Dirty);
            Assert.False(tower.Dirty);

            // Nothing of the game's was written.
            Assert.Equal(gameText, File.ReadAllText(gameFile));
            Assert.Equal(new[] { "level.json" }, Directory.GetFiles(Path.Combine(files.Dir("game"), "data")).Select(Path.GetFileName));

            // The mod's level, into its own file; the game's, as a patch in the mod's data/patches/.
            string modDir = files.Dir("mods/tweaks");
            Assert.Equal(Path.Combine(modDir, "data", "tower.json"), tower.Path);
            Assert.Contains("flag", File.ReadAllText(tower.Path));
            Assert.Contains("// The mod's own level.", File.ReadAllText(tower.Path));   // kept as written
            string patch = Path.Combine(modDir, "data", "patches", "placements_game_yard.json");
            Assert.Equal(patch, yard.SavedTo);
            Assert.Equal(gameFile, yard.Path);   // the document's own file is still where it is defined
            string patchText = File.ReadAllText(patch);
            Assert.Contains("\"patch\": true", patchText);
            Assert.Contains("\"game:yard\"", patchText);
            Assert.Contains("\"game:thing\"", patchText);   // in full: a bare id would mean the mod's namespace

            // What a game with the mod loads: the patch applied, the mod's level with its new placement.
            app.Engine.Records.Reload();
            Assert.Equal(0, app.Engine.Records.ErrorCount);
            Assert.True(app.Engine.Records.TryGet(Yard, out PlacementsRecord loaded));
            Assert.Equal(new[] { "gate", "door", "statue" }, loaded.Place.Select(p => p.Name));
            Assert.Equal(new Vector3(0, 0, -2), loaded.Place[0].At);
            Assert.All(loaded.Place, p => Assert.Equal(Thing, p.Prefab.Id));
            Assert.True(app.Engine.Records.TryGet(Tower, out PlacementsRecord towerLoaded));
            Assert.Equal(new[] { "bell", "flag" }, towerLoaded.Place.Select(p => p.Name));

            // A second save of the game's level changes the same patch file, not a second one.
            Assert.NotNull(Placing.Place(yard, Thing, new Vector3(4, 0, 4), 0f, "bench"));
            Assert.True(yard.Save());
            Assert.Single(Directory.GetFiles(Path.Combine(modDir, "data", "patches")));
            app.Engine.Records.Reload();
            Assert.True(app.Engine.Records.TryGet(Yard, out loaded));
            Assert.Equal(4, loaded.Place.Count);
            Assert.Equal(gameText, File.ReadAllText(gameFile));
        }
    }

    [Fact]
    public void WithoutAModTargetTheGamesLevelSavesInPlaceAsBefore()
    {
        var (app, files) = NewGame();
        using (app)
        {
            var (workspace, _) = NewWorkspace(app);
            var yard = workspace.Open(Yard)!;
            Assert.Null(workspace.Target);
            Assert.False(yard.SavesAsPatch);
            Assert.NotNull(Placing.Place(yard, Thing, new Vector3(3, 0, 3), 0f, "statue"));
            Assert.True(yard.Save());
            Assert.Equal(Path.Combine(files.Dir("game"), "data", "level.json"), yard.SavedTo);
            Assert.False(Directory.Exists(Path.Combine(files.Dir("mods/tweaks"), "data", "patches")));

            // And a target set later applies to every tab already open, and can be taken back.
            Assert.True(workspace.SetTarget("tweaks", out _));
            Assert.True(yard.SavesAsPatch);
            Assert.True(workspace.SetTarget(null, out _));
            Assert.False(yard.SavesAsPatch);
        }
    }

    [Fact]
    public void WhileModdingANewDocumentIsTheModsAndOneOfTheGamesNamespaceIsNotSaved()
    {
        var (app, files) = NewGame();
        using (app)
        {
            var (workspace, _) = NewWorkspace(app);
            Assert.True(workspace.SetTarget("tweaks", out _));
            var fresh = workspace.New();
            Assert.Equal("tweaks", fresh.Id.Namespace);
            Assert.NotNull(Placing.Place(fresh, Thing, Vector3.Zero, 0f, "rock"));
            Assert.True(fresh.Save());
            Assert.Equal(Path.Combine(files.Dir("mods/tweaks"), "data", "untitled.json"), fresh.Path);

            // A new record in the game's namespace would be a file in the game's folder: refused.
            fresh.New(new RecordId("game", "annex"));
            Assert.False(fresh.Save());
            Assert.False(File.Exists(Path.Combine(files.Dir("game"), "data", "annex.json")));
            Assert.False(File.Exists(Path.Combine(files.Dir("mods/tweaks"), "data", "annex.json")));

            Assert.False(workspace.SetTarget("nobody", out string error));
            Assert.Contains("tweaks", error);
            Assert.Equal("tweaks", workspace.Target!.RecordNamespace);   // unchanged
        }
    }

    // The tabs through the console, the way the editor's tab bar and a script press them.
    [Fact]
    public void TheTabCommandsOpenSwitchListCloseAndPickTheMod()
    {
        var (app, files) = NewGame();
        using (app)
        {
            var (workspace, first) = NewWorkspace(app);
            var cvars = app.Engine.CVars;
            WorkspaceCommands.Register(cvars, () => workspace);
            EditorCommands.Register(cvars, () => workspace.Active);   // doc_* act on the active tab, as in DevTools

            cvars.Execute("ed_mod tweaks", ExecSource.Console);
            Assert.Equal("tweaks", workspace.Target!.RecordNamespace);
            cvars.Execute("ed_tab_open yard", ExecSource.Console);
            cvars.Execute("ed_tab_open tower_placements", ExecSource.Console);
            Assert.Equal(2, workspace.Count);
            Assert.Equal(Tower, workspace.Active!.Id);

            cvars.Execute("ed_tab 1", ExecSource.Console);
            Assert.Same(first, workspace.Active);
            cvars.Execute("ed_tab tower_placements", ExecSource.Console);
            Assert.Equal(1, workspace.ActiveIndex);

            string tabs = WorkspaceCommands.Tabs(workspace);
            Assert.Contains("> 2. tweaks:tower_placements", tabs);
            Assert.Contains("1. game:yard (patch)", tabs);
            Assert.Contains("saves as a patch in mods/tweaks", tabs);

            Assert.NotNull(Placing.Place(workspace.Active, Thing, new Vector3(0, 12, 0), 0f, "flag"));
            cvars.Execute("ed_tab_close", ExecSource.Console);   // unsaved: stays
            Assert.Equal(2, workspace.Count);
            cvars.Execute("doc_save", ExecSource.Console);       // the active tab's document
            Assert.Contains("flag", File.ReadAllText(Path.Combine(files.Dir("mods/tweaks"), "data", "tower.json")));
            cvars.Execute("ed_tab_close", ExecSource.Console);
            Assert.Equal(1, workspace.Count);

            cvars.Execute("ed_tab_new", ExecSource.Console);
            Assert.Equal(2, workspace.Count);
            Assert.Equal("tweaks", workspace.Active!.Id.Namespace);
            cvars.Execute("ed_tab_close 2 !", ExecSource.Console);
            Assert.Equal(1, workspace.Count);

            cvars.Execute("ed_mod -", ExecSource.Console);
            Assert.Null(workspace.Target);
        }
    }
}
