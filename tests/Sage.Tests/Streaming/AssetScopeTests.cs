#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Issue #308: asset scopes (engine, game, sector, UI), ref-counted release, eviction on unload and the
// per-frame upload budget (docs/design/05 §3.3, §3.4; Sage.Simulation's Content/AssetScopes.cs). The
// client's GPU tables are `AssetTable`s and its safe point is `AssetReleases.Collect`; `Client` below drives
// the same two the way the renderer does (resolve what each live entity draws, collect at the start of the
// frame), so the bookkeeping is proved here without a graphics device. The renderer's own use of them is
// covered by the smoke run.
public class AssetScopeTests
{
    public AssetScopeTests() { _ = TestEnv.UserRoot; }

    private const float Sector = Terrain.SectorSize;
    private const int Sectors = 50;

    // Hills, and a prop in each of fifty sectors east of the player, each with its own model and its own
    // material whose texture is its own; a lamp in (0, 0) and (25, 0) shares one model and one texture.
    private static string Content()
    {
        var json = new StringBuilder("""
        [
          { "type": "terrain", "id": "hills", "generator": "Hills", "seed": 4, "height": 0, "amplitude": 20, "wavelength": 300 },
          { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
          { "type": "material", "id": "lamp_glass", "effect": "shaders/test.mgfxo", "params": { "Albedo": "textures/lamp.png" } },
          { "type": "prefab", "id": "lamp", "name": "lamp", "components": { "mesh_renderer": { "mesh": "models/lamp.glb", "material": "lamp_glass" } } },
        """);
        var place = new StringBuilder("""{ "prefab": "lamp", "at": [60, 0, 60] }, { "prefab": "lamp", "at": [""" + (25 * Sector + 60) + ", 0, 60] }");
        for (int i = 1; i <= Sectors; i++)
        {
            json.Append($$"""{ "type": "material", "id": "mat{{i}}", "effect": "shaders/test.mgfxo", "params": { "Albedo": "textures/prop{{i}}.png" } },""");
            json.Append($$"""{ "type": "prefab", "id": "prop{{i}}", "name": "prop{{i}}", "components": { "mesh_renderer": { "mesh": "models/prop{{i}}.glb", "material": "mat{{i}}" } } },""");
            place.Append($$""", { "prefab": "prop{{i}}", "at": [{{i * Sector + 100}}, 0, 100] }""");
        }
        json.Append($$"""
          { "type": "scene", "id": "valley", "streamed": true, "terrain": "hills",
            "player": { "prefab": "hero", "at": [20, 1, 20] }, "place": [ {{place}} ] }
        ]
        """);
        return json.ToString();
    }

    private static HeadlessApp Run()
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content());
        files.Write("game", "shaders/test.mgfxo", "");
        files.Write("game", "models/lamp.glb", "");
        files.Write("game", "textures/lamp.png", "");
        for (int i = 1; i <= Sectors; i++)
        {
            files.Write("game", $"models/prop{i}.glb", "");
            files.Write("game", $"textures/prop{i}.png", "");
        }
        files.Mount("game", "game");
        // `material` is the client's record type (sage.client); a headless world that names materials registers it.
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .OnRegistered(a => a.Records.Register<MaterialRecord>())
            .Mount(files).StartScene("game:valley").Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = TestEnv.NewTempDir();
        app.CVars.Execute("save_autosave 0");
        Tick(app.World);
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static void Walk(World world, float x, float z, int ticks = 3)
    {
        world.Get<Transform>(Hero(world)).LocalPosition = world.Origin().ToOrigin(new Vector3(x, 1, z));
        Tick(world, ticks);
    }

    // What the client does with the tables each frame, headless: at the safe point, free what the sectors
    // released that no world holds; then resolve what every live entity draws, at the sectors' scope.
    private sealed class Client
    {
        public readonly AssetTable<string> Meshes = new("(error mesh)");
        public readonly AssetTable<string> Textures = new("(checker)");
        public readonly AssetReleases Releases = new();
        public readonly UploadBudget Budget = new() { MillisecondsPerFrame = 0f };
        public readonly HashSet<AssetPath> EverLoaded = new();
        private readonly List<AssetKey> _keys = new();
        private readonly Func<AssetPath, (string?, long)> _load;
        private long _frame;

        public Client(World world)
        {
            Releases.Watch(world.Resources.Get<SectorAssets>());
            _load = path => { EverLoaded.Add(path); return (path.ToString(), 1024); };
        }

        public void Frame(World world)
        {
            _frame++;
            Budget.BeginFrame(_frame);
            Releases.Collect(new[] { world }, key => (key.Type == AssetType.Mesh ? Meshes : Textures).TryEvict(key.Path, out _));
            var records = world.Resources.Get<RecordStore>();
            foreach (var entity in world.Query<MeshRenderer>().Entities.ToEntityList())
            {
                _keys.Clear();
                AssetUse.Of(records, world.Get<MeshRenderer>(entity), _keys);
                foreach (var key in _keys)
                    (key.Type == AssetType.Mesh ? Meshes : Textures).TryResolve(key.Path, AssetScope.Sector, _frame, Budget, _load, out _);
            }
        }
    }

    // The done line of #308: walking out across fifty sectors and back loads each one's model and texture
    // and frees them as it unloads, so the counts come back to where they started — and the tables reuse
    // their slots rather than growing a slot per sector.
    [Xunit.Fact]
    public void LoadingAndUnloadingFiftySectorsReturnsTextureAndMeshCountsToBaseline()
    {
        using var app = Run();
        var world = app.World;
        var client = new Client(world);
        client.Frame(world);
        int meshes = client.Meshes.Count, textures = client.Textures.Count;
        var lamp = AssetPath.Intern("models/lamp.glb");
        Assert.True(client.Meshes.TryFind(lamp, out _));
        Assert.True(meshes >= 2 && textures >= 2, $"{meshes} meshes, {textures} textures at the start");

        int most = 0;
        for (int i = 1; i <= Sectors; i++)
        {
            Walk(world, i * Sector + 100, 100);
            client.Frame(world);
            most = Math.Max(most, client.Meshes.Count + client.Textures.Count);
            if (i == 25)
            {
                // The lamp in (25, 0) holds the shared model and texture again; the one at home is asleep.
                Assert.Equal(1, world.Resources.Get<SectorAssets>().RefCount(lamp));
                Assert.Equal(1, world.Resources.Get<SectorAssets>().RefCount(AssetKey.Texture(AssetPath.Intern("textures/lamp.png"))));
                Assert.True(client.Meshes.TryFind(lamp, out int id) && client.Meshes.IsLive(id));
            }
        }
        for (int i = Sectors - 1; i >= 0; i--)
        {
            Walk(world, i * Sector + 100, 100);
            client.Frame(world);
        }
        // The ring keeps a sector a little past its radius, so step west of home and back to stand where the
        // walk started with the same ring around it.
        Walk(world, -3 * Sector + 100, 100);
        client.Frame(world);
        Walk(world, 20, 20);
        client.Frame(world);

        // Every sector's model and texture was loaded on the way...
        for (int i = 1; i <= Sectors; i++)
        {
            Assert.Contains(AssetPath.Intern($"models/prop{i}.glb"), client.EverLoaded);
            Assert.Contains(AssetPath.Intern($"textures/prop{i}.png"), client.EverLoaded);
        }
        Assert.True(client.Releases.Evicted >= 2 * Sectors, $"{client.Releases.Evicted} evicted");
        // ...and only a ring's worth was ever loaded at once, in slots reused all the way.
        Assert.True(most < 30, $"{most} assets loaded at once");
        Assert.True(client.Meshes.Slots < 20 && client.Textures.Slots < 20, $"{client.Meshes.Slots} mesh slots, {client.Textures.Slots} texture slots");

        // Back home: the same counts as at the start.
        Assert.Equal(meshes, client.Meshes.Count);
        Assert.Equal(textures, client.Textures.Count);
        Assert.Equal(meshes * 1024L, client.Meshes.Bytes);
    }

    // A released asset that something took up again before the safe point (a sector came straight back,
    // or another entity draws it) is not freed; one released and still unheld is.
    [Xunit.Fact]
    public void AnAssetTakenUpAgainBeforeTheSafePointIsKept()
    {
        using var app = Run();
        var world = app.World;
        var client = new Client(world);
        client.Frame(world);
        var lamp = AssetPath.Intern("models/lamp.glb");
        Assert.True(client.Meshes.TryFind(lamp, out int id));

        Walk(world, 6 * Sector + 100, 100);   // home goes dormant: the lamp is released...
        Assert.True(client.Releases.Pending > 0);
        var torch = world.Create(Transform.At(Vector3.Zero), "torch");
        world.Add(torch, new MeshRenderer { Mesh = lamp });   // ...and drawn again before the frame
        client.Frame(world);
        Assert.True(client.Meshes.IsLive(id));
        Assert.Equal(0, client.Releases.Pending);

        // Destroyed outside a sector's unload: the game's to let go of, so it stays loaded.
        world.Destroy(torch);
        world.FlushCommands();
        client.Frame(world);
        Assert.True(client.Meshes.IsLive(id));
    }

    // The table itself: ids never shift and freed slots are reused; the placeholder, the engine's and the
    // game's are never evicted; the stronger of two scopes wins; a UI asset goes when no screen has drawn it
    // for UiKeepFrames.
    [Xunit.Fact]
    public void TheTableKeepsIdsStableAndEvictsByScope()
    {
        var table = new AssetTable<string>("placeholder");
        AssetPath P(string name) => AssetPath.Intern($"textures/scope_{name}.png");
        int a = table.Add(P("a"), "a", AssetScope.Sector, 10, 0);
        int b = table.Add(P("b"), "b", AssetScope.Sector, 10, 0);
        int engine = table.Add(P("engine"), "engine", AssetScope.Engine, 10, 0);
        int game = table.Add(P("game"), "game", AssetScope.Game, 10, 0);
        int ui = table.Add(P("ui"), "ui", AssetScope.Ui, 10, 0);
        Assert.Equal(5, table.Count);
        Assert.Equal(50, table.Bytes);

        Assert.True(table.TryEvict(P("a"), out var freed));
        Assert.Equal("a", freed);
        Assert.Equal("placeholder", table[a]);   // a freed id draws the placeholder until reused
        Assert.Equal("b", table[b]);             // and nobody else's id moved
        int c = table.Add(P("c"), "c", AssetScope.Sector, 10, 0);
        Assert.Equal(a, c);                      // the slot is reused
        Assert.False(table.TryEvict(P("engine"), out _));
        Assert.False(table.TryEvict(P("game"), out _));
        Assert.Equal("engine", table[engine]);
        Assert.Equal("game", table[game]);
        Assert.Null(table.Remove(0));            // the placeholder never goes

        // Asked for by the game too, a sector's asset is the game's now.
        table.Use(b, AssetScope.Game, 1);
        Assert.Equal(AssetScope.Game, table.EntryAt(b).Scope);
        Assert.False(table.TryEvict(P("b"), out _));

        // The UI's picture stays while a screen draws it, and goes after UiKeepFrames without.
        var evicted = new List<AssetPath>();
        table.Use(ui, AssetScope.Ui, 100);
        Assert.Equal(0, table.CollectUnusedUi(100 + AssetScopes.UiKeepFrames, AssetScopes.UiKeepFrames, (p, _) => evicted.Add(p)));
        Assert.Equal(1, table.CollectUnusedUi(101 + AssetScopes.UiKeepFrames, AssetScopes.UiKeepFrames, (p, _) => evicted.Add(p)));
        Assert.Equal(new[] { P("ui") }, evicted);
        Assert.False(table.IsLive(ui));

        // A failed load is remembered as the placeholder, and forgotten on release so it is tried again.
        Assert.True(table.TryResolve(P("broken"), AssetScope.Sector, 2, null, _ => (null, 0), out int broken));
        Assert.Equal(0, broken);
        Assert.True(table.TryFind(P("broken"), out _));
        Assert.True(table.TryEvict(P("broken"), out var none));
        Assert.Null(none);
        Assert.False(table.TryFind(P("broken"), out _));
    }

    // The upload budget (05 §3.4): streamed loads past the frame's milliseconds wait for the next frame,
    // the first load of a frame always starts, the game's loads are never put off, and 0 is no budget.
    [Xunit.Fact]
    public void TheUploadBudgetSpreadsStreamedLoadsOverFrames()
    {
        var table = new AssetTable<string>("placeholder");
        // Every load "takes" 3 ms of a 2 ms budget (on top of the real time it takes, which is tiny).
        var budget = new UploadBudget { MillisecondsPerFrame = 2f };
        var paths = Enumerable.Range(0, 4).Select(i => AssetPath.Intern($"models/budget_{i}.glb")).ToArray();
        Func<AssetPath, (string?, long)> load = p => { budget.Spend(3); return (p.ToString(), 1); };

        int frames = 0;
        while (table.Count < paths.Length)
        {
            budget.BeginFrame(++frames);
            foreach (var path in paths) table.TryResolve(path, AssetScope.Sector, frames, budget, load, out _);
            Assert.Equal(frames, table.Count);   // one new load a frame, the rest put off
            Assert.Equal(paths.Length - frames, budget.Deferred);
            Assert.True(frames <= paths.Length);
        }
        Assert.Equal(paths.Length, frames);
        Assert.Equal(6, budget.TotalDeferred);   // 3 + 2 + 1

        // The game's loads (the viewmodel, the HUD, a world that does not stream) are not budgeted.
        budget.BeginFrame(++frames);
        Assert.True(table.TryResolve(AssetPath.Intern("models/budget_game_0.glb"), AssetScope.Sector, frames, budget, load, out _));
        Assert.True(table.TryResolve(AssetPath.Intern("models/budget_game_1.glb"), AssetScope.Game, frames, budget, load, out _));
        Assert.False(table.TryResolve(AssetPath.Intern("models/budget_game_2.glb"), AssetScope.Sector, frames, budget, load, out _));

        // No budget: everything loads at once.
        budget.MillisecondsPerFrame = 0f;
        Assert.True(table.TryResolve(AssetPath.Intern("models/budget_game_2.glb"), AssetScope.Sector, frames, budget, load, out _));
        Assert.True(table.TryResolve(AssetPath.Intern("models/budget_game_3.glb"), AssetScope.Sector, frames, budget, load, out _));
    }
}
