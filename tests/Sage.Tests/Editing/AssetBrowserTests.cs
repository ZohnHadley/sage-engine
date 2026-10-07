#nullable enable
#pragma warning disable SAGE0130 // the material's surface fields (normalMap), which a pick sets
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The asset browser (issue #366, docs/design/15 §11): the VFS's assets by kind, mount and mod; an asset
// picked into a record's field or a placement's as an undoable command, shown in the game at once (a
// material's texture: the issue's "done"); a drop in the viewport placing it; and a rename that moves the
// file and rewrites what names it.
public class AssetBrowserTests
{
    public AssetBrowserTests() { _ = TestEnv.UserRoot; }

    private static readonly RecordId Brick = new("sandbox", "brick");

    private const string GameRecords = """
        [
          // the wall's material: kept as a person wrote it
          { "type": "material", "id": "brick", "effect": "shaders/lit.mgfxo",
            "params": { "Albedo": "textures/brick.png", "AlbedoColor": [1, 1, 1, 1] } },
          { "type": "prefab", "id": "rock", "name": "rock", "components": { "mesh_renderer": { "mesh": "models/rock.glb", "material": "brick" } } },
          { "type": "placements", "id": "yard", "place": [ { "prefab": "rock", "at": [0, 0, 0], "name": "rock" } ] }
        ]
        """;

    // A kit (another mount, read-only to the game), the game, and a mod that replaces one of its textures.
    private static (HeadlessApp App, MountFixture Fixture) Boot(string? kitRecords = null)
    {
        var fixture = new MountFixture();
        fixture.Write("kit", "models/barrel.glb", "glb");
        fixture.Write("kit", "sounds/creak.wav", "wav");
        if (kitRecords != null) fixture.Write("kit", "data/kit.json", kitRecords);
        fixture.Write("game", "data/things.json", GameRecords);
        fixture.Write("game", "shaders/lit.mgfxo", "fx");
        fixture.Write("game", "textures/brick.png", "png");
        fixture.Write("game", "textures/moss.png", "png");
        fixture.Write("game", "textures/brick.png.sgtex", "cooked");
        fixture.Write("game", "models/rock.glb", "glb");
        fixture.Write("game", "models/stump.glb", "glb");
        fixture.Write("game", "sounds/door.ogg", "ogg");
        fixture.Write("game", "maps/hut.map", "{\n\"classname\" \"worldspawn\"\n\"wad\" \"textures/brick.png\"\n}\n");
        fixture.Write("game", "notes.txt", "not an asset");
        fixture.Write("mods/shiny", "textures/moss.png", "shinier moss");
        fixture.Mount("kit", "kit");
        fixture.Mount("game", "sandbox");
        fixture.Mount("mods/shiny", "shiny");
        var app = HeadlessApp.Simulation().WithEngineContent().Mount(fixture)
            .OnRegistered(a => a.Records.Register<MaterialRecord>())   // the client's record type
            .Build();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        return (app, fixture);
    }

    private static string GameFile(MountFixture fixture, string relative) =>
        Path.Combine(fixture.Dir("game"), relative.Replace('/', Path.DirectorySeparatorChar));

    private static AssetPath Albedo(HeadlessApp app) => app.Records.Get<MaterialRecord>(Brick).Params["Albedo"].Texture;

    [Fact]
    public void TheBrowserListsAssetsByKindMountAndModWithWhatEachShadows()
    {
        var (app, _) = Boot();
        using (app)
        using (var browser = new AssetBrowser(app.Engine))
        {
            var paths = browser.Entries().Select(e => e.Path.Value).ToList();
            Assert.Contains("textures/brick.png", paths);
            Assert.Contains("models/barrel.glb", paths);
            Assert.Contains("sounds/door.ogg", paths);
            Assert.Contains("maps/hut.map", paths);
            Assert.DoesNotContain("data/things.json", paths);           // records are the Records panel's
            Assert.DoesNotContain("textures/brick.png.sgtex", paths);   // a cooked file only stands in
            Assert.DoesNotContain("notes.txt", paths);
            Assert.Contains("textures/white.png", paths);               // the engine's content too

            var moss = browser.Find("textures/moss.png")!;
            Assert.Equal(AssetKinds.Texture, moss.Kind);
            Assert.True(moss.FromMod);
            Assert.Equal("shiny", moss.Origin);                         // a mod by its id
            Assert.Equal("game", Assert.Single(moss.Shadows).Name);     // the game's moss, replaced

            browser.Kind = AssetKinds.Mesh;
            Assert.Equal(new[] { "models/barrel.glb", "models/rock.glb", "models/stump.glb" },
                         browser.Filtered().Select(e => e.Path.Value).Where(p => !p.StartsWith("models/sage")).ToArray());
            browser.Mount = "kit";
            Assert.Equal("models/barrel.glb", Assert.Single(browser.Filtered()).Path.Value);
            browser.Kind = "";
            browser.Mount = "shiny";
            Assert.Equal("textures/moss.png", Assert.Single(browser.Filtered()).Path.Value);
            browser.Mount = "";
            browser.Search = "SOUNDS o";                                // every word, any case
            Assert.Equal(new[] { "sounds/creak.wav", "sounds/door.ogg" }, browser.Filtered().Select(e => e.Path.Value).ToArray());

            Assert.Contains(("sound", 2), browser.Kinds());
            Assert.Equal(new[] { "kit", "game", "shiny" }, browser.Mounts().Where(m => m is "kit" or "game" or "shiny").ToArray());

            Assert.True(AssetKinds.Fits("font", VirtualPath.Parse("fonts/grid.png")));   // a grid atlas is a font
            Assert.False(AssetKinds.Fits("texture", VirtualPath.Parse("sounds/door.ogg")));
        }
    }

    // The issue's "done": a texture picked from the browser for a material is what the game's material
    // has at once — before a save, with no file touched — and undo, redo and closing take it back.
    [Fact]
    public void ATexturePickedForAMaterialShowsInTheGameAtOnceAndIsUndoable()
    {
        var (app, fixture) = Boot();
        using (app)
        {
            string file = File.ReadAllText(GameFile(fixture, "data/things.json"));
            var previewed = new System.Collections.Generic.List<(string, RecordId)>();
#pragma warning disable SAGE0133
            app.Records.Previewed += (type, id) => previewed.Add((type, id));
#pragma warning restore SAGE0133
            var editor = new RecordEditor(app.Engine);   // the record browser: live by default
            Assert.True(editor.Live);
            Assert.True(editor.Open("material", Brick));
            var record = editor.Current!;
            var held = app.Records.Get<MaterialRecord>(Brick);   // the instance the renderer holds

            var slots = AssetPicking.Slots(record);
            Assert.Contains(slots, s => s.Path == "params.Albedo" && s.Current?.Value == "textures/brick.png");
            Assert.Contains(slots, s => s.Path == "normalMap" && s.Kind == AssetKinds.Texture && s.Current == null);

            Assert.True(AssetPicking.ToRecord(record, "params.Albedo", VirtualPath.Parse("textures/moss.png"), out string error), error);
            Assert.Equal(AssetPath.Intern("textures/moss.png"), Albedo(app));
            Assert.Same(held, app.Records.Get<MaterialRecord>(Brick));   // changed in place: what holds it sees it
            Assert.Contains(("material", Brick), previewed);
            Assert.Equal(file, File.ReadAllText(GameFile(fixture, "data/things.json")));   // nothing written
            Assert.True(record.Dirty);

            // A surface map, declared [AssetKind("texture")]: a sound does not fit it, a texture does.
            Assert.False(AssetPicking.ToRecord(record, "normalMap", VirtualPath.Parse("sounds/door.ogg"), out error));
            Assert.Contains("takes a texture", error);
            Assert.False(AssetPicking.ToRecord(record, "gloss", VirtualPath.Parse("textures/moss.png"), out error));   // a number
            Assert.Contains("not an asset", error);
            Assert.True(AssetPicking.ToRecord(record, "normalMap", VirtualPath.Parse("textures/brick.png"), out error), error);
            Assert.Equal(AssetPath.Intern("textures/brick.png"), held.NormalMap);

            // Two picks are two edits: undo takes back one at a time, live.
            Assert.True(record.Undo());
            Assert.True(held.NormalMap.IsEmpty);
            Assert.True(record.Undo());
            Assert.Equal(AssetPath.Intern("textures/brick.png"), Albedo(app));
            Assert.True(record.Redo());
            Assert.Equal(AssetPath.Intern("textures/moss.png"), Albedo(app));

            // Closed unsaved, the game goes back to what the file says.
            editor.Close();
            Assert.Equal(AssetPath.Intern("textures/brick.png"), Albedo(app));

            // Picked and saved: in the file, with its comment, and in the game after the reload.
            Assert.True(editor.Open("material", Brick));
            Assert.True(AssetPicking.ToRecord(editor.Current!, "params.Albedo", VirtualPath.Parse("textures/moss.png"), out error), error);
            Assert.True(editor.Current!.Save());
            string saved = File.ReadAllText(GameFile(fixture, "data/things.json"));
            Assert.Contains("\"textures/moss.png\"", saved);
            Assert.Contains("// the wall's material", saved);
            Assert.Equal(AssetPath.Intern("textures/moss.png"), Albedo(app));
        }
    }

    [Fact]
    public void APreviewThatDoesNotBuildLeavesTheRecordAsItWasAndAReloadTakesAPreviewBack()
    {
        var (app, _) = Boot();
        using (app)
        {
            var raw = app.Records.RawJson("material", Brick)!;
#pragma warning disable SAGE0133
            raw["specularr"] = 1;
            Assert.False(app.Records.Preview("material", Brick, raw, out string error));
            Assert.Contains("unknown field 'specularr'", error);
            raw.Remove("specularr");
            raw["gloss"] = "shiny";
            Assert.False(app.Records.Preview("material", Brick, raw, out _));
            Assert.Equal(0.5f, app.Records.Get<MaterialRecord>(Brick).Gloss);
            Assert.False(app.Records.Preview("material", new RecordId("sandbox", "nope"), raw, out _));

            raw["gloss"] = 0.9;
            Assert.True(app.Records.Preview("material", Brick, raw, out error), error);
#pragma warning restore SAGE0133
            Assert.Equal(0.9f, app.Records.Get<MaterialRecord>(Brick).Gloss);
            app.Records.Reload();
            Assert.Equal(0.5f, app.Records.Get<MaterialRecord>(Brick).Gloss);

            // A document opened on its own is not live until asked.
            var record = RecordDocument.Open(app.Engine, "material", Brick)!;
            Assert.False(record.Live);
            Assert.True(record.Set("gloss", JsonValue.Create(0.2)));
            Assert.Equal(0.5f, app.Records.Get<MaterialRecord>(Brick).Gloss);
            record.Live = true;
            Assert.Equal(0.2f, app.Records.Get<MaterialRecord>(Brick).Gloss);
            record.Live = false;
            Assert.Equal(0.5f, app.Records.Get<MaterialRecord>(Brick).Gloss);
        }
    }

    [Fact]
    public void AModelPickedIntoAPlacementsFieldIsAnOverrideAndDropsInTheViewportPlaceIt()
    {
        var (app, _) = Boot();
        using (app)
        {
            var document = new EditDocument(app.Engine.CreateWorld("edit"));
            Assert.True(document.Open(new RecordId("sandbox", "yard")));
            var rock = document.Find("rock")!;

            Assert.True(AssetPicking.ToPlacement(document, rock, "mesh_renderer.mesh", VirtualPath.Parse("models/stump.glb"), out string error), error);
            Assert.Equal(AssetPath.Intern("models/stump.glb"), document.World.Get<MeshRenderer>(document.EntityOf(rock)).Mesh);
            Assert.False(AssetPicking.ToPlacement(document, rock, "mesh_renderer.mesh", VirtualPath.Parse("textures/moss.png"), out error));
            Assert.Contains("takes a mesh", error);
            Assert.False(AssetPicking.ToPlacement(document, rock, "mesh_renderer.material", VirtualPath.Parse("models/stump.glb"), out _));
            Assert.True(document.Undo());
            Assert.Equal(AssetPath.Intern("models/rock.glb"), document.World.Get<MeshRenderer>(document.EntityOf(rock)).Mesh);

            // A model a prefab uses places that prefab; one no prefab uses places sage:static_mesh with it.
            Assert.Equal(new RecordId("sandbox", "rock"), AssetPicking.PrefabFor(app.Engine, VirtualPath.Parse("models/rock.glb")));
            var placed = AssetPicking.Place(document, VirtualPath.Parse("models/stump.glb"), new Vector3(2, 0, 0), 90f, out error)!;
            Assert.NotNull(placed);
            Assert.Equal(AssetPicking.StaticMesh, placed.Prefab.Id);
            Assert.Equal("stump", placed.Name);
            Assert.Equal(AssetPath.Intern("models/stump.glb"), document.World.Get<MeshRenderer>(document.EntityOf(placed)).Mesh);
            Assert.Equal(2, document.Placements.Count);

            var ray = new EditorRay(new Vector3(4, 10, 0), -Vector3.UnitY);   // straight down onto y = 0
            var second = AssetPicking.PlaceAt(document, VirtualPath.Parse("models/rock.glb"), ray, out error)!;
            Assert.Equal(new RecordId("sandbox", "rock"), second.Prefab.Id);
            Assert.Equal("rock_2", second.Name);
            Assert.True(Vector3.Distance(new Vector3(4, 0, 0), second.At) < 1e-3f);

            Assert.Null(AssetPicking.Place(document, VirtualPath.Parse("sounds/door.ogg"), Vector3.Zero, 0f, out error));
            Assert.Contains("no prefab names", error);

            Assert.True(document.Undo());
            Assert.True(document.Undo());   // one undo per placement, override and all
            Assert.Single(document.Placements);
        }
    }

    [Fact]
    public void RenamingAnAssetMovesItAndItsCookedFileAndRewritesWhatNamesIt()
    {
        var (app, fixture) = Boot();
        using (app)
        {
            var refs = AssetReferences.Find(app.Vfs, VirtualPath.Parse("textures/brick.png"));
            Assert.Contains(refs, r => r.File.Value == "data/things.json" && r.Line == 4);
            Assert.Contains(refs, r => r.File.Value == "maps/hut.map" && r.Line == 3);

            var document = new EditDocument(app.Engine.CreateWorld("edit"));
            Assert.True(document.Open(new RecordId("sandbox", "yard")));
            var records = new RecordEditor(app.Engine);
            Assert.True(records.Open("material", Brick));

            var plan = AssetRename.Plan(app.Engine, VirtualPath.Parse("textures/brick.png"), VirtualPath.Parse("textures/walls/red_brick.png"), document, records);
            Assert.True(plan.CanApply, string.Join("\n", plan.Problems));
            Assert.Equal(new[] { "data/things.json", "maps/hut.map" }, plan.Rewrites.Select(f => f.Value).OrderBy(f => f).ToArray());
            Assert.True(plan.Apply());

            Assert.False(File.Exists(GameFile(fixture, "textures/brick.png")));
            Assert.True(File.Exists(GameFile(fixture, "textures/walls/red_brick.png")));
            Assert.True(File.Exists(GameFile(fixture, "textures/walls/red_brick.png.sgtex")));
            string data = File.ReadAllText(GameFile(fixture, "data/things.json"));
            Assert.Contains("\"Albedo\": \"textures/walls/red_brick.png\"", data);
            Assert.Contains("// the wall's material: kept as a person wrote it", data);
            Assert.Contains("\"wad\" \"textures/walls/red_brick.png\"", File.ReadAllText(GameFile(fixture, "maps/hut.map")));
            Assert.Equal(AssetPath.Intern("textures/walls/red_brick.png"), Albedo(app));   // reloaded
            Assert.Equal("textures/walls/red_brick.png", (string?)records.Current!.Get("params.Albedo"));   // reopened
            Assert.Empty(AssetReferences.Find(app.Vfs, VirtualPath.Parse("textures/brick.png")));

            // And back, the way the log says.
            Assert.True(AssetRename.Plan(app.Engine, VirtualPath.Parse("textures/walls/red_brick.png"), VirtualPath.Parse("textures/brick.png")).Apply());
            Assert.Equal(AssetPath.Intern("textures/brick.png"), Albedo(app));
        }
    }

    [Fact]
    public void ARenameIsRefusedWhenTheAssetOrAReferenceIsNotTheGamesOrSomethingIsUnsaved()
    {
        var (app, fixture) = Boot(kitRecords: """[ { "type": "prefab", "id": "barrel", "components": { "mesh_renderer": { "mesh": "models/rock.glb" } } } ]""");
        using (app)
        {
            string Problems(string from, string to, EditDocument? document = null, RecordEditor? records = null)
            {
                var plan = AssetRename.Plan(app.Engine, VirtualPath.Parse(from), VirtualPath.Parse(to), document, records);
                Assert.False(plan.Apply());
                return string.Join("\n", plan.Problems);
            }

            Assert.Contains("is kit's, not the game's", Problems("models/barrel.glb", "models/cask.glb"));
            Assert.Contains("kit:data/kit.json:1", Problems("models/rock.glb", "models/boulder.glb"));   // the kit names it
            Assert.Contains("already taken", Problems("textures/brick.png", "textures/moss.png"));
            Assert.Contains("keep the extension", Problems("textures/brick.png", "textures/brick.jpg"));
            Assert.Contains("in no mount", Problems("textures/none.png", "textures/other.png"));

            var records = new RecordEditor(app.Engine);
            Assert.True(records.Open("material", Brick));
            Assert.True(records.Current!.Set("gloss", JsonValue.Create(0.1)));
            Assert.Contains("unsaved edits", Problems("textures/brick.png", "textures/b2.png", records: records));
            Assert.True(File.Exists(GameFile(fixture, "textures/brick.png")));   // nothing moved
        }
    }

    [Fact]
    public void TheConsoleListsPicksPlacesAndRenamesAssets()
    {
        var (app, fixture) = Boot();
        using (app)
        {
            var document = new EditDocument(app.Engine.CreateWorld("edit"));
            var records = new RecordEditor(app.Engine);
            using var browser = new AssetBrowser(app.Engine);
            AssetCommands.Register(app.CVars, () => browser, () => document, () => records);
            records.Register(app.CVars);
            EditorCommands.Register(app.CVars, () => document);
            Assert.True(document.Open(new RecordId("sandbox", "yard")));

            using var capture = new CaptureSink();
            app.CVars.Execute("ed_assets texture mount=shiny", ExecSource.Console);
            Assert.Contains(capture.Entries, e => e.Message.Contains("textures/moss.png") && e.Message.Contains("shadows game"));
            app.CVars.Execute("ed_asset_refs textures/brick.png", ExecSource.Console);
            Assert.Contains(capture.Entries, e => e.Message.Contains("named 2 time(s)"));

            app.CVars.Execute("ed_rec_open material brick", ExecSource.Console);
            app.CVars.Execute("ed_asset_pick textures/moss.png params.Albedo", ExecSource.Console);
            Assert.Equal(AssetPath.Intern("textures/moss.png"), Albedo(app));
            app.CVars.Execute("ed_rec_undo", ExecSource.Console);
            Assert.Equal(AssetPath.Intern("textures/brick.png"), Albedo(app));

            app.CVars.Execute("ed_asset_pick models/stump.glb rock mesh_renderer.mesh", ExecSource.Console);
            Assert.Equal(AssetPath.Intern("models/stump.glb"), document.World.Get<MeshRenderer>(document.EntityOf(document.Find("rock")!)).Mesh);
            app.CVars.Execute("ed_asset_place models/stump.glb 1 0 1", ExecSource.Console);
            Assert.NotNull(document.Find("stump"));
            app.CVars.Execute("ed_undo", ExecSource.Console);
            app.CVars.Execute("ed_undo", ExecSource.Console);
            Assert.False(document.Dirty);

            app.CVars.Execute("ed_rec_close", ExecSource.Console);
            app.CVars.Execute("ed_asset_rename sounds/door.ogg sounds/gate.ogg", ExecSource.Console);
            Assert.True(File.Exists(GameFile(fixture, "sounds/gate.ogg")));
            Assert.Equal("sounds/gate.ogg", browser.Selected?.Path.Value);
        }
    }
}
