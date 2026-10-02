#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// `sage validate`'s navmesh check (issue #264): a level's markers that no body can walk to.
//
// **A marker** is a point entity whose classname starts with `info_` — `info_player_start`, a load
// door's `info_target`, a teleport's destination: Quake's own word for "a place", and the places a
// player or a creature has to be able to stand. A torch on a wall or a light in the air is not one.
//
// The level's brushes alone are baked (no terrain, which is generated at run time, and no colliders,
// which its prefabs bring when it spawns), every tile of it, and each marker is looked for on the mesh:
// - standing on nothing walkable — in a wall, over a hole, on a ledge too narrow for a body — is
//   reported, except in a level that stands on terrain, where the ground under it is not known yet;
// - standing where nothing joins it to the level's `info_player_start` (or, without one, to its first
//   marker) is reported: a room sealed off by a wall a mapper forgot to cut a door in.
//
// Warnings, not errors: a sealed room is sometimes the point (a teleport's destination), and the level
// still loads. The check runs at every content load of a development build and under `sage validate`;
// a shipping game has shipped with its levels.
internal static class NavMeshChecks
{
    // Past this many tiles (a 256 m square level) the bake is more than a load should pay for, and the
    // check is skipped.
    private const int MaxTiles = 1024;

    public static void Map(RecordStore records, Engine engine, MapRecord record, RecordCheck check)
    {
        if (!BuildInfo.IsDevBuild && !records.MissingAssetsAreErrors) return;
        if (MapLevel.Read(engine, check.Id, record) is not { } level) return;

        var markers = new List<MapEntity>();
        foreach (var entity in level.PointEntities)
            if (entity.ClassName.StartsWith("info_", StringComparison.OrdinalIgnoreCase)) markers.Add(entity);
        if (markers.Count == 0 || level.Brushes.Count == 0) return;

        var geometry = new NavGeometry();
        geometry.AddLevel(level, Vector3.Zero);
        var mesh = new NavMesh(NavMeshAgent.Default) { MaxTiles = int.MaxValue };
        if (!BakeAll(mesh, geometry, level)) return;

        // Each marker on the mesh, or said not to be.
        var spans = new int[markers.Count];
        for (int i = 0; i < markers.Count; i++)
        {
            spans[i] = mesh.FindSpan(level.LocalPositionOf(markers[i]));
            if (spans[i] < 0 && !level.OnTerrain)
                check.Warn(null, $"{Where(level, markers[i])} stands on nothing a body can walk on " +
                                 "(in a wall, over a hole, or with less than 1.8 m above it): nothing can reach it (navmesh, #264)");
        }

        int reference = markers.FindIndex(m => m.ClassName.Equals("info_player_start", StringComparison.OrdinalIgnoreCase));
        if (reference < 0) reference = 0;
        if (spans[reference] < 0) return;

        var reached = new HashSet<int>();
        mesh.Flood(spans[reference], reached);
        for (int i = 0; i < markers.Count; i++)
            if (i != reference && spans[i] >= 0 && !reached.Contains(spans[i]))
                check.Warn(null, $"{Where(level, markers[i])} cannot be reached from {Where(level, markers[reference])}: " +
                                 "no walkable way joins them (navmesh, #264)");
    }

    private static string Where(MapLevel level, MapEntity entity)
    {
        string name = entity.Keys.TryGetValue("targetname", out var target) && !string.IsNullOrEmpty(target) ? $" '{target}'" : "";
        return $"{level.Source}:{entity.Line}: '{entity.ClassName}'{name}";
    }

    private static bool BakeAll(NavMesh mesh, NavGeometry geometry, MapLevel level)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var brush in level.Brushes)
        {
            min = Vector3.Min(min, brush.Min);
            max = Vector3.Max(max, brush.Max);
        }
        int x0 = NavMesh.TileOf(min.X), x1 = NavMesh.TileOf(max.X);
        int z0 = NavMesh.TileOf(min.Z), z1 = NavMesh.TileOf(max.Z);
        if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > MaxTiles)
        {
            Log.Debug(LogCat.AI, $"{level.Source}: too big to check its markers on the navmesh at load");
            return false;
        }
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
                mesh.TryBake(x, z, geometry, unbudgeted: true);
        return true;
    }
}
