#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Sage.Kits.Rpg;

// A scene's map (issue #349): the picture the map screen lays under its markers, the ground it covers, and
// the fog that hides what the player has not been near. Its id is its scene's — `sandbox:main`'s map is the
// `area_map` `sandbox:main` — so a scene has one, found without a scan, and a mod patches it by that id:
//
//   { "type": "area_map", "id": "main", "picture": "textures/map_main.png",
//     "from": [448, 448], "to": [576, 576], "cell": 8, "reveal": 16 }
//
// `from` and `to` are absolute metres, X and Z: the picture's top left is `from` (west, north: north is −Z)
// and its bottom right `to`. A scene without one has a map of markers only, and no fog.
[Record("area_map", Plugin = RpgKitModule.Id)]
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]   // the RPG screens: SAGE0125, as Sage.UI
public sealed class AreaMapRecord
{
    [AssetKind("texture"), Property(Tooltip = "The picture of the ground the map shows under its markers; empty: none")]
    public AssetPath Picture;

    [Property(Unit = "m", Tooltip = "The picture's top left (north-west) corner: absolute X and Z")]
    public Vector2 From;

    [Property(Unit = "m", Tooltip = "The picture's bottom right (south-east) corner: absolute X and Z")]
    public Vector2 To;

    [Property(Tooltip = "Where the player has not been is hidden under fog until they come near it (default true)")]
    public bool Fog = true;

    [Property(Min = 1, Unit = "m", Tooltip = "The side of one square of fog")]
    public float Cell = 16f;

    [Property(Min = 0, Unit = "m", Tooltip = "How far round the player the fog lifts")]
    public float Reveal = 24f;

    // The scene's map, or null: the record with the scene's id.
    public static AreaMapRecord? Of(World world, RecordId scene) =>
        !scene.IsEmpty && world.Records().TryGet(scene, out AreaMapRecord map) ? map : null;

    internal static void Check(AreaMapRecord map, RecordCheck check)
    {
        if (!map.Picture.IsEmpty && (map.To.X <= map.From.X || map.To.Y <= map.From.Y))
            check.Error(nameof(To), $"the picture's bottom right ({map.To.X}, {map.To.Y}) must be east and south of its top left ({map.From.X}, {map.From.Y})");
        if (map.Cell < 1f) check.Error(nameof(Cell), "a square of fog is at least 1 m across");
        if (map.Reveal < 0f) check.Error(nameof(Reveal), "the fog cannot lift less than nothing round the player");
    }
}

// Where the player has been, by scene: the squares of each scene's map the fog has lifted from (issue
// #349). Saved, so exploring stays explored. A square is `cell` metres of absolute X and Z, as the scene's
// `area_map` said when it was lifted; a map whose `cell` changes starts its fog again.
[SavedResource("map_discovery", Plugin = RpgKitModule.Id)]
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = RpgKitModule.ExperimentalUrl)]
public sealed class MapDiscovery
{
    public sealed class Area
    {
        public RecordId Scene { get; set; }
        public float Cell { get; set; }

        // Each square lifted, as Key(x, z).
        public List<long> Cells { get; set; } = new();

        private HashSet<long>? _set;

        internal HashSet<long> Set
        {
            get
            {
                if (_set == null || _set.Count != Cells.Count) _set = new HashSet<long>(Cells);   // after a load
                return _set;
            }
        }
    }

    public List<Area> Areas { get; set; } = new();

    // Goes up with every square lifted: what a map compares to know its fog changed.
    [JsonIgnore]
    public int Version { get; private set; }

    // The world's (made when first asked for; a load replaces it).
    public static MapDiscovery Of(World world) => world.Resources.GetOrAdd(static () => new MapDiscovery());

    // A square's key: its X and Z indices, packed.
    public static long Key(int x, int z) => ((long)x << 32) | (uint)z;

    public static int IndexOf(float metres, float cell) => (int)MathF.Floor(metres / cell);

    // Whether the square holding this absolute place has been lifted, in a scene's fog of `cell` metres.
    public bool IsDiscovered(RecordId scene, float cell, Vector3 absolute)
    {
        var area = Find(scene, cell);
        return area != null && area.Set.Contains(Key(IndexOf(absolute.X, cell), IndexOf(absolute.Z, cell)));
    }

    public bool IsDiscovered(RecordId scene, float cell, int x, int z)
    {
        var area = Find(scene, cell);
        return area != null && area.Set.Contains(Key(x, z));
    }

    // Lifts the fog from every square with a part within `radius` of an absolute place (and the square it is
    // in); returns how many were new.
    public int Reveal(RecordId scene, float cell, Vector3 absolute, float radius)
    {
        if (cell < 1f) cell = 1f;
        var area = Find(scene, cell);
        if (area == null)
        {
            Areas.RemoveAll(a => a.Scene == scene);   // a map whose squares changed size starts again
            area = new Area { Scene = scene, Cell = cell };
            Areas.Add(area);
        }

        var set = area.Set;
        int x0 = IndexOf(absolute.X - radius, cell), x1 = IndexOf(absolute.X + radius, cell);
        int z0 = IndexOf(absolute.Z - radius, cell), z1 = IndexOf(absolute.Z + radius, cell);
        int cx = IndexOf(absolute.X, cell), cz = IndexOf(absolute.Z, cell);
        float r2 = radius * radius;
        int added = 0;
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                // The square's nearest point to the place.
                float nx = Math.Clamp(absolute.X, x * cell, (x + 1) * cell) - absolute.X;
                float nz = Math.Clamp(absolute.Z, z * cell, (z + 1) * cell) - absolute.Z;
                if (nx * nx + nz * nz > r2 && (x != cx || z != cz)) continue;
                long key = Key(x, z);
                if (!set.Add(key)) continue;
                area.Cells.Add(key);
                added++;
            }
        if (added > 0) Version++;
        return added;
    }

    private Area? Find(RecordId scene, float cell)
    {
        foreach (var area in Areas)
            if (area.Scene == scene && area.Cell == cell) return area;
        return null;
    }
}

// Gameplay phase: the player's surroundings come out of the fog as they walk (issue #349), in a scene whose
// `area_map` has fog. Only when the player moves into another square, so standing still costs a lookup.
[System("rpg.map_discovery", Phase.Gameplay)]
internal sealed class MapDiscoverySystem : ISystem
{
    private long _square = long.MinValue;
    private RecordId _scene;
    private MapDiscovery? _discovery;
    private AreaMapRecord? _map;

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var scene = Scenes.Current(world);
        if (AreaMapRecord.Of(world, scene) is not { Fog: true } map) return;
        var player = Scenes.Player(world);
        if (player.IsNull || !world.Has<Transform>(player)) return;
        var local = world.TryGet<GlobalTransform>(player, out var global) ? global.Current.Position : world.Get<Transform>(player).LocalPosition;
        var at = world.Origin().ToAbsolute(local);
        float cell = MathF.Max(map.Cell, 1f);
        long square = MapDiscovery.Key(MapDiscovery.IndexOf(at.X, cell), MapDiscovery.IndexOf(at.Z, cell));
        var discovery = MapDiscovery.Of(world);
        if (square == _square && scene == _scene && ReferenceEquals(discovery, _discovery) && ReferenceEquals(map, _map)) return;
        _square = square;
        _scene = scene;
        _discovery = discovery;
        _map = map;
        discovery.Reveal(scene, cell, at, map.Reveal);
    }
}
