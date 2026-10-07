#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sage.Gameplay;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A record with a keyed list, as a trader's stock is: entries told apart by the item they sell.
[Record("test_shop")]
public sealed class TestShop
{
    public string Name = "";
    public TestShopFloor Floor = new();
    [ListKey("item")] public List<TestStock> Stock = new();
    public List<TestStock> Unkeyed = new();
}

public sealed class TestShopFloor
{
    public int Size;
    [ListKey("item")] public List<TestStock> Shelves = new();
}

public sealed class TestStock
{
    [RecordRef("test_item")] public RecordId Item;
    public int Price;
    public int Count = 1;
}

// Keyed list merge, `"$remove"` and `"replace": true` in record patches (issue #399, 9-4): a patch edits
// one entry of a `[ListKey]` list in place, removes one by key, or replaces a whole object, and the
// content report says which key two mods both wrote.
public class KeyedListMergeTests
{
    public KeyedListMergeTests() { _ = TestEnv.UserRoot; }

    private const string Shop = """
        [{ "type": "test_item", "id": "lantern" }, { "type": "test_item", "id": "bread" }, { "type": "test_item", "id": "rope" },
         { "type": "test_shop", "id": "shop", "name": "Village shop",
           "floor": { "size": 4, "shelves": [ { "item": "rope", "price": 2 } ] },
           "stock": [ { "item": "lantern", "price": 8 }, { "item": "bread", "price": 1, "count": 5 } ],
           "unkeyed": [ { "item": "lantern", "price": 8 } ] }]
        """;

    private static readonly RecordId ShopId = new("village", "shop");
    private static readonly RecordId Lantern = new("village", "lantern"), Bread = new("village", "bread"), Rope = new("village", "rope");

    private static (RecordStore Store, ContentReport Report) Load(MountFixture fx)
    {
        var store = new RecordStore();
        store.Register<TestItem>();
        store.Register<TestShop>();
        store.Load(fx.Vfs);
        return (store, ContentReport.Build(store, fx.Vfs));
    }

    private static MountFixture Village(params (string Mod, string Json)[] mods)
    {
        var fx = new MountFixture();
        fx.Write("game", "data/shop.json", Shop);
        fx.Mount("game", "village");
        foreach (var (mod, json) in mods)
        {
            fx.Write($"mods/{mod}", "data/patch.json", json);
            fx.Mount($"mods/{mod}", mod);
        }
        return fx;
    }

    private static string Patch(string fields) => $$"""[{ "type": "test_shop", "id": "village:shop", "patch": true, {{fields}} }]""";

    [Fact]
    public void APatchChangesOneEntryOfAKeyedListByKey_WithoutRestatingTheList()
    {
        var fx = Village(("cheap_lanterns", Patch("""
            "stock": [ { "item": "village:lantern", "price": 5 } ],
            "unkeyed": [ { "item": "village:lantern", "price": 5 } ]
            """)));
        var (store, report) = Load(fx);
        Assert.Equal(0, store.ErrorCount);

        var shop = store.Get<TestShop>(ShopId);
        // The lantern's price changed in place; the bread, which the patch doesn't name, is untouched.
        Assert.Equal(new[] { (Lantern, 5, 1), (Bread, 1, 5) }, shop.Stock.Select(s => (s.Item, s.Price, s.Count)));
        // A list without a key is replaced, as before.
        Assert.Equal((Lantern, 5), (Assert.Single(shop.Unkeyed).Item, shop.Unkeyed[0].Price));

        var writes = store.Writes("test_shop", ShopId).Where(w => w.Op != RecordWriteOp.Define).Select(w => (w.Op, w.Path)).ToList();
        Assert.Contains((RecordWriteOp.Set, "stock[village:lantern].price"), writes);
        // The key is how the entry is found, not a write.
        Assert.DoesNotContain(writes, w => w.Path.EndsWith(".item"));
        Assert.Contains("  patched test_shop village:shop (overrides game): set stock[village:lantern].price, set unkeyed", report.Lines("cheap_lanterns"));
    }

    [Fact]
    public void ABareKeyIsTheRecordsNamespace_AndAnEntryWithANewKeyIsAdded()
    {
        var fx = Village();
        // The game's own patch: "lantern" is village:lantern, as the definition wrote it.
        fx.Write("game", "data/sale.json", Patch("""
            "stock": [ { "item": "lantern", "price": 6 }, { "item": "rope", "price": 3 } ],
            "floor": { "shelves": [ { "item": "rope", "count": 9 } ] }
            """));
        var (store, _) = Load(fx);
        Assert.Equal(0, store.ErrorCount);

        var shop = store.Get<TestShop>(ShopId);
        Assert.Equal(new[] { (Lantern, 6, 1), (Bread, 1, 5), (Rope, 3, 1) }, shop.Stock.Select(s => (s.Item, s.Price, s.Count)));
        // At any depth: the shelf's rope keeps its price and gets a count.
        Assert.Equal((Rope, 2, 9), (shop.Floor.Shelves[0].Item, shop.Floor.Shelves[0].Price, shop.Floor.Shelves[0].Count));
        Assert.Equal(4, shop.Floor.Size);
    }

    [Fact]
    public void TwoModsEditingDifferentKeysOfOneList_DoNotConflict_AndTheSameKeyIsAConflictAboutThatKey()
    {
        var fx = Village(
            ("cheap_lanterns", Patch("""  "stock": [ { "item": "village:lantern", "price": 5 } ]  """)),
            ("more_bread", Patch("""  "stock": [ { "item": "village:bread", "count": 20 } ]  """)));
        var (store, report) = Load(fx);
        Assert.Equal(0, store.ErrorCount);
        Assert.Equal(new[] { (Lantern, 5, 1), (Bread, 1, 20) }, store.Get<TestShop>(ShopId).Stock.Select(s => (s.Item, s.Price, s.Count)));
        Assert.Empty(report.Conflicts);

        fx = Village(
            ("cheap_lanterns", Patch("""  "stock": [ { "item": "village:lantern", "price": 5 } ]  """)),
            ("dear_lanterns", Patch("""  "stock": [ { "item": "village:lantern", "price": 12 }, { "item": "village:bread", "count": 2 } ]  """)));
        (store, report) = Load(fx);
        Assert.Equal(12, store.Get<TestShop>(ShopId).Stock[0].Price);
        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal("stock[village:lantern].price", conflict.Path);
        Assert.Equal("test_shop village:shop stock[village:lantern].price: cheap_lanterns, dear_lanterns; dear_lanterns won", conflict.Line);
    }

    [Fact]
    public void AnAddToAKeyedListAndAnEditOfAnotherEntry_DoNotConflict()
    {
        var fx = Village(
            ("cheap_lanterns", Patch("""  "stock": [ { "item": "village:lantern", "price": 5 } ]  """)),
            ("rope_seller", Patch("""  "stock+": [ { "item": "village:rope", "price": 2 } ]  """)));
        var (store, report) = Load(fx);
        Assert.Equal(new[] { Lantern, Bread, Rope }, store.Get<TestShop>(ShopId).Stock.Select(s => s.Item));
        Assert.Empty(report.Conflicts);
    }

    [Fact]
    public void DollarRemoveTakesAnEntryOutByKey_AndAgainstAnotherModsEditOfItIsAConflict()
    {
        var fx = Village(("no_bread", Patch("""  "stock": [ { "item": "village:bread", "$remove": true } ]  """)));
        var (store, report) = Load(fx);
        Assert.Equal(0, store.ErrorCount);
        Assert.Equal(new[] { Lantern }, store.Get<TestShop>(ShopId).Stock.Select(s => s.Item));
        Assert.Contains((RecordWriteOp.Remove, "stock[village:bread]"), store.Writes("test_shop", ShopId).Select(w => (w.Op, w.Path)));
        Assert.Empty(report.Conflicts);

        fx = Village(
            ("more_bread", Patch("""  "stock": [ { "item": "village:bread", "count": 20 } ]  """)),
            ("no_bread", Patch("""  "stock": [ { "item": "village:bread", "$remove": true } ]  """)));
        (store, report) = Load(fx);
        Assert.Equal(new[] { Lantern }, store.Get<TestShop>(ShopId).Stock.Select(s => s.Item));
        Assert.Equal("test_shop village:shop stock[village:bread].count: more_bread, no_bread; no_bread won", Assert.Single(report.Conflicts).Line);
    }

    [Fact]
    public void ReplaceTrueReplacesAWholeObject_ItsKeyedListsIncluded_OrTheWholeRecord()
    {
        var fx = Village(("new_floor", Patch("""  "floor": { "replace": true, "shelves": [ { "item": "village:bread", "price": 4 } ] }  """)));
        var (store, _) = Load(fx);
        Assert.Equal(0, store.ErrorCount);
        var shop = store.Get<TestShop>(ShopId);
        // Not merged: the rope is gone, and so is the size the patch didn't restate.
        Assert.Equal((Bread, 4), (Assert.Single(shop.Floor.Shelves).Item, shop.Floor.Shelves[0].Price));
        Assert.Equal(0, shop.Floor.Size);
        Assert.Equal(2, shop.Stock.Count);
        Assert.Contains((RecordWriteOp.Set, "floor"), store.Writes("test_shop", ShopId).Select(w => (w.Op, w.Path)));

        fx = Village(
            ("cheap_lanterns", Patch("""  "stock": [ { "item": "village:lantern", "price": 5 } ]  """)),
            ("new_shop", Patch("""  "replace": true, "name": "Rope shop", "stock": [ { "item": "village:rope", "price": 1 } ]  """)));
        ContentReport report;
        (store, report) = Load(fx);
        Assert.Equal(0, store.ErrorCount);
        shop = store.Get<TestShop>(ShopId);
        Assert.Equal("Rope shop", shop.Name);
        Assert.Equal(new[] { Rope }, shop.Stock.Select(s => s.Item));
        Assert.Empty(shop.Unkeyed);
        Assert.Equal(0, shop.Floor.Size);
        // A whole record replaced is a write of all of it: another mod's edit of any of it is a conflict.
        Assert.Equal("test_shop village:shop (record): cheap_lanterns, new_shop; new_shop won", Assert.Single(report.Conflicts).Line);
    }

    // The issue's "done": a mod changes the trader's price for one stock item without restating his stock
    // (here a worn pistol, sold at half its price), and another mod changing another item of the same stock
    // is not a conflict. The trader game (tests/games/trader) with his goods given an `inventory` list, and
    // two mods named on the command line.
    [Fact]
    public void AModChangesTheTradersPriceForOneStockItem_AndAnotherModsEditOfAnotherItemIsNoConflict()
    {
        string game = TestEnv.NewTempDir();
        CopyDirectory(Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "trader"), game);
        File.WriteAllText(Path.Combine(game, "content", "data", "goods.json"), """
            [{ "type": "prefab", "id": "trader", "patch": true,
               "parts": { "inventory": { "items": [ { "item": "pistol" }, { "item": "flesh_eye", "count": 2 } ] } } }]
            """);
        string worn = WriteMod("worn_pistols", """
            [{ "type": "prefab", "id": "bunker:trader", "patch": true,
               "parts": { "inventory": { "items": [ { "item": "bunker:pistol", "instance": { "condition": 0.5 } } ] } } }]
            """);
        string eyes = WriteMod("more_eyes", """
            [{ "type": "prefab", "id": "bunker:trader", "patch": true,
               "parts": { "inventory": { "items": [ { "item": "bunker:flesh_eye", "count": 6 } ] } } }]
            """);

        int PistolPrice(params string[] mods)
        {
            using var app = HeadlessApp.ForGame(game).WithEngineContent().WithMods(mods).Boot();
            Assert.Equal(0, app.Records.ErrorCount);
            var world = app.World;
            var trader = Assert.Single(world.QueryAll().Entities.ToEntityList(), e => e.Name == "Sidorovich");
            var player = Assert.Single(world.QueryAll().Entities.ToEntityList(), e => e.Name == "stalker");
            var stock = world.Get<Inventory>(trader).Items;
            Assert.Equal(mods.Contains(eyes) ? 6 : 2, stock.Where(s => s.Item == new RecordId("bunker", "flesh_eye")).Sum(s => s.Count));
            Assert.Empty(ContentReport.Build(app.Records, app.Engine.Vfs).Conflicts);
            var pistol = stock.Single(s => s.Item == new RecordId("bunker", "pistol"));
            return Merchants.Price(world, player, trader, pistol, 1, buying: true);
        }

        Assert.Equal(450, PistolPrice());                // 300 times his markup of 1.5
        Assert.Equal(225, PistolPrice(worn));            // worn to half
        Assert.Equal(225, PistolPrice(worn, eyes));      // and the other mod's edit of his stock neither undoes it nor conflicts
    }

    private static string WriteMod(string id, string data)
    {
        string dir = Path.Combine(TestEnv.NewTempDir(), id);
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "mod.json"),
            $$"""{ "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "game": "bunker", "gameVersion": "^1.0", "sage": ">=0.1" }""");
        File.WriteAllText(Path.Combine(dir, "data", "patch.json"), data);
        return dir;
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
