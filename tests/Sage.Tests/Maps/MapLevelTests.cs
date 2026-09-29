#nullable enable
using System.Numerics;
using Friflo.Engine.ECS;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Loading a `.map` into a world (docs/design/15 §3, TODO F16): the half of the importer that is not
// arithmetic — records, the VFS, physics and the entities a level brings with it.
//
// The geometry itself is pinned next door in `MapImportTests`; these are about a level *arriving*.
public class MapLevelTests
{
    public MapLevelTests() { _ = TestEnv.UserRoot; }

    // A room four metres square with a metre-thick floor, in the canonical Quake form. Two brushes, so
    // "how many did it build" distinguishes one from both.
    private const string RoomMap = """
        {
        "classname" "worldspawn"
        {
        ( -64 -64 -32 ) ( -64 -63 -32 ) ( -64 -64 -31 ) floor 0 0 0 1 1
        ( -64 -64 -32 ) ( -64 -64 -31 ) ( -63 -64 -32 ) floor 0 0 0 1 1
        ( -64 -64 -32 ) ( -63 -64 -32 ) ( -64 -63 -32 ) floor 0 0 0 1 1
        ( 64 64 0 ) ( 64 65 0 ) ( 65 64 0 ) floor 0 0 0 1 1
        ( 64 64 0 ) ( 65 64 0 ) ( 64 64 1 ) floor 0 0 0 1 1
        ( 64 64 0 ) ( 64 64 1 ) ( 64 65 0 ) floor 0 0 0 1 1
        }
        {
        ( -64 48 0 ) ( -64 49 0 ) ( -64 48 1 ) wall 0 0 0 1 1
        ( -64 48 0 ) ( -64 48 1 ) ( -63 48 0 ) wall 0 0 0 1 1
        ( -64 48 0 ) ( -63 48 0 ) ( -64 49 0 ) wall 0 0 0 1 1
        ( 64 64 128 ) ( 64 65 128 ) ( 65 64 128 ) wall 0 0 0 1 1
        ( 64 64 128 ) ( 65 64 128 ) ( 64 64 129 ) wall 0 0 0 1 1
        ( 64 64 128 ) ( 64 64 129 ) ( 64 65 128 ) wall 0 0 0 1 1
        }
        }
        {
        "classname" "marker"
        "origin" "32 0 64"
        "angle" "90"
        "targetname" "the marker"
        }
        {
        "classname" "nothing_has_this"
        "origin" "0 0 0"
        }
        """;

    private const string Records = """
        [
          { "type": "map", "id": "room", "file": "maps/room.map", "at": [10, 0, -5] },
          { "type": "prefab", "id": "marker", "components": { "transform": {} } }
        ]
        """;

    private static Engine NewEngine(string map = RoomMap, string records = Records)
    {
        // The fixture's VFS goes *into* the engine: a map is read through `engine.Vfs` at load time, not
        // just at record-load time, so an engine with its own empty VFS finds the record and then cannot
        // find the file it names.
        var fixture = new MountFixture();
        fixture.Write("game", "maps/room.map", map);
        fixture.Write("game", "data/level.json", records);
        fixture.Mount("game", "sandbox");

        // The I/O modules too, because a level's wiring is checked against their inputs when it loads,
        // and because the FGD this generates advertises them.
        return HeadlessApp.Bare()
            .With(new PhysicsModule(), new MapModule(), new EntityIOModule(), new MoverModule())
            .Mount(fixture)
            .Build().Engine;
    }

    [Fact]
    public void ALevelLoadsItsBrushesAndStandsThemWhereTheRecordSays()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");

        var level = MapLoader.Load(world, new RecordId("sandbox", "room"));

        Assert.NotNull(level);
        Assert.Equal(2, level!.Brushes.Count);
        Assert.Equal(12, level.FaceCount);

        // `at` is absolute metres and the world has not moved, so origin space is the same numbers.
        world.RunFixed(1f / 60f);
        Assert.Equal(new Vector3(10, 0, -5), level.Position);
    }

    [Fact]
    public void EveryBrushBecomesOneStaticHull()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");
        var space = world.Resources.Get<PhysicsSpace>();
        int before = space.StaticCount;

        MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        // A hull rather than a triangle mesh, because a brush is convex and a mesh is a surface: a
        // player who ends up inside one falls through the world (10 §3).
        Assert.Equal(before + 2, space.StaticCount);
    }

    [Fact]
    public void TheWallIsSolidWhereItIsAndEmptyWhereItIsNot()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");
        var space = world.Resources.Get<PhysicsSpace>();

        MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        // The wall brush runs from map y 48..64 at x -64..64, z 0..128 — in metres, a slab north of the
        // level's middle. Map north is engine -Z, so it is at negative Z from the level's position.
        var results = new Entity[8];
        var inside = new Vector3(10, 2, -5) + new Vector3(0, 0, -1.75f);
        var outside = new Vector3(10, 2, -5) + new Vector3(0, 0, 2f);

        Assert.True(space.OverlapBox(inside, new Vector3(0.2f, 0.2f, 0.2f), results) > 0,
            "nothing solid where the wall is");
        Assert.Equal(0, space.OverlapBox(outside, new Vector3(0.2f, 0.2f, 0.2f), results));
    }

    [Fact]
    public void AClassnameWithAPrefabIsSpawnedAndOneWithoutIsNot()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");

        MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        // `marker` has a prefab, so it is in the world, named by its `targetname` and placed by its
        // `origin`; `nothing_has_this` does not, and is left to whatever reads the level for itself.
        var entity = default(Entity);
        foreach (var candidate in world.Query<Transform>().Entities)
            if (candidate.Name.value == "the marker") { entity = candidate; break; }
        Assert.False(entity.IsNull);

        // Map (32, 0, 64) units is (1, 2, 0) metres in engine axes, on top of the level's position.
        var at = entity.GetComponent<Transform>().LocalPosition;
        Assert.Equal(11f, at.X, 3);
        Assert.Equal(2f, at.Y, 3);
        Assert.Equal(-5f, at.Z, 3);
    }

    [Fact]
    public void UnloadingALevelTakesItsGeometryWithIt()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");
        var space = world.Resources.Get<PhysicsSpace>();
        int before = space.StaticCount;

        MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);
        Assert.Equal(before + 2, space.StaticCount);

        world.Resources.Get<MapLevels>().Clear();
        world.RunFixed(1f / 60f);

        // Reloading a level while the game runs is the whole point of importing one, so a level that
        // goes away has to take its statics (and its hull shapes) with it.
        Assert.Equal(before, space.StaticCount);
        Assert.Empty(world.Resources.Get<MapLevels>().Loaded);
    }

    [Fact]
    public void UnloadingLeavesTheClientsOwnEntitiesForTheClientToFree()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");

        MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        // Stand in for the client half, which is not in this assembly: an entity carrying the level's
        // geometry marker and something only the renderer knows about.
        var mesh = world.Create(Transform.At(Vector3.Zero), "pretend mesh");
        world.Add(mesh, new MapGeometry { Level = new RecordId("sandbox", "room") });
        world.Add(mesh, new MeshRenderer { Handle = new MeshHandle(7) });

        world.Resources.Get<MapLevels>().Clear();
        world.RunFixed(1f / 60f);

        // The simulation destroys the hulls and leaves that one alone. It must: both halves mark their
        // entities the same way, the simulation's handler runs first, and an entity destroyed here is a
        // GPU buffer the client never hears about again.
        Assert.False(mesh.IsNull);
        Assert.True(world.Query<MapGeometry, MeshRenderer>().Count > 0);
    }

    [Fact]
    public void ReloadingALevelDoesNotLeaveTheOldOneStandingInIt()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");
        var id = new RecordId("sandbox", "room");

        MapLoader.Load(world, id);
        world.RunFixed(1f / 60f);
        int after = world.Query<FromMap>().Count;
        Assert.Equal(1, after);                       // the `marker`; the other classname has no prefab

        world.Resources.Get<MapLevels>().Clear();
        world.RunFixed(1f / 60f);
        Assert.Equal(0, world.Query<FromMap>().Count);

        MapLoader.Load(world, id);
        world.RunFixed(1f / 60f);

        // Loading a level twice over must leave one of everything. The Sandbox reloads its maps whenever
        // a record file is saved, so "one more watcher per save" is what this costs when it is wrong.
        Assert.Equal(after, world.Query<FromMap>().Count);
    }

    [Fact]
    public void LoadingTheSameLevelTwiceIsRefusedRatherThanStacked()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");
        var id = new RecordId("sandbox", "room");

        Assert.NotNull(MapLoader.Load(world, id));

        // A level's position comes from its record, so a second copy lands exactly on the first: every
        // surface fighting with itself and two hulls in the same doorway.
        Assert.Null(MapLoader.Load(world, id));
        Assert.Single(world.Resources.Get<MapLevels>().Loaded);
    }

    [Fact]
    public void ALevelFollowsTheWorldWhenTheOriginMoves()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("level");

        var level = MapLoader.Load(world, new RecordId("sandbox", "room"))!;
        world.RunFixed(1f / 60f);
        var before = level.Position;

        var offset = world.Rebase(new SectorCoord(1, 0));

        // R6: a world position held outside the ECS follows the rebase, or everything built from it
        // afterwards lands a sector away from the walls it belongs to.
        Assert.Equal(before + offset, level.Position);
    }

    [Fact]
    public void ALevelThatCannotBeReadCostsItselfAndNotTheWorld()
    {
        using var engine = NewEngine(map: "{ this is not a map");
        var world = engine.CreateWorld("level");

        var level = MapLoader.Load(world, new RecordId("sandbox", "room"));

        Assert.Null(level);
        Assert.Empty(world.Resources.Get<MapLevels>().Loaded);
        world.RunFixed(1f / 60f);       // and the world still runs
    }

    [Fact]
    public void EverythingTheFgdAdvertisesActuallyExists()
    {
        using var engine = NewEngine();

        string fgd = FgdExport.Build(engine);

        // The rule R19 is about, applied to the one file this engine generates *for somebody else to
        // read*: a mapper opens it in TrenchBroom and believes every word. An input listed here that
        // nothing registers is a door that never opens, found weeks later by the person who drew it.
        var advertised = System.Text.RegularExpressions.Regex.Match(fgd, @"Inputs this game has: ([^.]+)\.");
        Assert.True(advertised.Success, "the FGD stopped saying what its inputs are");

        foreach (string name in advertised.Groups[1].Value.Split(','))
            Assert.True(engine.Inputs.Has(name.Trim()), $"the FGD offers input '{name.Trim()}', which is not registered");

        // And every classname it offers either spawns a prefab or is one the engine itself reads off the
        // level. The second list is spelled out rather than waved at: it is what stops "not a prefab"
        // from becoming an excuse, so a typo in a classname still fails here.
        string[] theEngineReads = { "info_player_start" };

        foreach (System.Text.RegularExpressions.Match entity in
                 System.Text.RegularExpressions.Regex.Matches(fgd, @"@PointClass[^=]*= ([a-z_0-9]+) :"))
        {
            string className = entity.Groups[1].Value;
            if (System.Array.IndexOf(theEngineReads, className) >= 0) continue;

            var id = new RecordId("sandbox", className);
            Assert.True(engine.Records.Exists(id), $"the FGD offers '{id}', which is neither a prefab nor read by the engine");
        }

        // The exemption is not a hole: what the engine reads, it must be able to *find*. A classname on
        // that list which `TryFindPoint` would never match is the same broken promise one step along.
        using var engineWithAStart = NewEngine(RoomWithAStart);
        var world = engineWithAStart.CreateWorld("fgd");
        var level = MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        foreach (string className in theEngineReads)
            Assert.True(level!.TryFindPoint(className, out _, out _),
                        $"the FGD offers '{className}' as something the engine reads, and it cannot read it");
    }

    [Fact]
    public void TheGeneratedFgdNamesEveryPrefabAndOnlyKeysTheImporterReads()
    {
        using var engine = NewEngine();

        string fgd = FgdExport.Build(engine);

        Assert.Contains("@PointClass", fgd);
        Assert.Contains("= marker :", fgd);
        Assert.Contains("@SolidClass = worldspawn", fgd);

        // The one point entity with no prefab behind it. A mapper who cannot place it from the editor
        // will not place it at all, and it is what `map_goto` and a game's own spawning look for.
        Assert.Contains("= info_player_start :", fgd);

        // The rule the exporter holds to: every key it offers is one `MapLoader` acts on. A key here
        // that the importer ignores is a mapper filling in a field that does nothing.
        Assert.Contains("angle(integer)", fgd);
        Assert.Contains("targetname(target_source)", fgd);
        Assert.DoesNotContain("health(", fgd);
    }

    // A level with the one piece of furniture every game wants: where the player starts.
    private const string RoomWithAStart = RoomMap + """

        {
        "classname" "info_player_start"
        "origin" "0 -64 8"
        "angle" "90"
        }
        """;

    // `info_player_start` has no prefab and never will: where a game puts the player is the game's
    // decision, not the engine's. What the engine owes a mapper is the *answer* — the place they marked,
    // in metres, in the world — because the two conversions between the two are not a game's to know.
    [Fact]
    public void AClassnameWithNoPrefabIsStillSomethingTheGameCanFind()
    {
        using var engine = NewEngine(RoomWithAStart);
        var world = engine.CreateWorld("level");

        var level = MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        Assert.True(level!.TryFindPoint("info_player_start", out var at, out float yaw));

        // Map (0, -64, 8) at 32 units to the metre is (0, -2, 0.25); map space becomes engine space as
        // (x, z, -y), so (0, 0.25, 2); and the level itself stands at (10, 0, -5).
        Assert.Equal(new Vector3(10, 0.25f, -3), at);

        // Quake's `angle` is counter-clockwise from east and the engine's yaw is not: 90 means north,
        // which is 0. Getting this wrong points the player at a wall, which is the sort of thing that
        // looks like a level bug for a day.
        Assert.Equal(0f, yaw);
    }

    [Fact]
    public void AskingForSomethingNobodyPlacedIsNoRatherThanTheOrigin()
    {
        using var engine = NewEngine(RoomWithAStart);
        var world = engine.CreateWorld("level");

        var level = MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        Assert.False(level!.TryFindPoint("info_player_end", out var at, out _));
        Assert.Equal(Vector3.Zero, at);
    }

    [Fact]
    public void ALevelThatDoesNotKnowWhereItIsYetSaysSoRatherThanGuessing()
    {
        using var engine = NewEngine(RoomWithAStart);
        var world = engine.CreateWorld("level");

        var level = MapLoader.Load(world, new RecordId("sandbox", "room"));

        // Loaded, but not placed until the first tick — and a level that stands on terrain waits longer
        // than that. Answering now would be the map's own coordinates dressed up as the world's.
        Assert.False(level!.Placed);
        Assert.False(level.TryFindPoint("info_player_start", out _, out _));
    }

    [Fact]
    public void TheWholeWorldCanBeAskedRatherThanEachLevelInTurn()
    {
        using var engine = NewEngine(RoomWithAStart);
        var world = engine.CreateWorld("level");

        // What a game actually wants to ask: is there a player start anywhere in this world? It does not
        // know which levels are loaded, and it should not have to.
        Assert.False(MapLoader.TryFindPoint(world, "info_player_start", out _, out _));

        MapLoader.Load(world, new RecordId("sandbox", "room"));
        world.RunFixed(1f / 60f);

        Assert.True(MapLoader.TryFindPoint(world, "info_player_start", out var at, out _));
        Assert.Equal(new Vector3(10, 0.25f, -3), at);
    }
}
