#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Filling a world up, and writing down what it costs (docs/design/02 §4.6, 14; TODO R18).
//
// The readiness review (2026-09-24 §3) said the quiet part out loud: the tests prove correctness, not
// fitness. The largest test created two hundred entities and the Sandbox placed twenty-five, so every
// performance claim in these docs was an assumption. These two commands exist to replace the
// assumption with a number: **fill a world, then read the clock**.
//
// `scale_report` is the more important half. A profiler that nobody reads is a profiler that is
// wrong — this prints one table, in run order, with the phases that matter and the counts beside
// them, so a run of the game produces a record rather than an impression.
public static class ScaleConsoleCommands
{
    public static void Register(CVarRegistry cvars, Engine engine)
    {
        cvars.RegisterCommand("scale_spawn", CVarFlags.DevOnly | CVarFlags.Cheat,
            "scale_spawn <count> [prefab] [spacing]: fill the world around the player, to measure it (R18).", a =>
        {
            if (a.Count == 0 || !int.TryParse(a[0], out int count) || count <= 0)
            {
                Log.Warn(LogCat.Console, "scale_spawn <count> [prefab] [spacing]");
                return;
            }

            var world = engine.Worlds.Count > 0 ? engine.Worlds[0] : null;
            if (world == null) { Log.Warn(LogCat.Console, "scale_spawn: no world"); return; }

            var prefab = a.Count > 1 ? engine.Records.Resolve("prefab", a[1]) : default;
            if (a.Count > 1 && prefab.IsEmpty) return;   // Resolve has already said so
            float spacing = a.Count > 2 && float.TryParse(a[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float given) ? given : 6f;

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int made = Fill(world, count, prefab, spacing);
            Log.Info(LogCat.Console, $"scale_spawn: {made} of {count} in {watch.Elapsed.TotalMilliseconds:F0} ms " +
                                     $"({(prefab.IsEmpty ? "bare entities" : prefab.ToString())}); " +
                                     $"{world.EntityCount} entities in '{world.Name}'");
        });

        cvars.RegisterCommand("scale_report", CVarFlags.None,
            "What a frame costs right now: phases, systems over 0.1 ms, and what is in the world (R18).", _ =>
        {
            foreach (var world in engine.Worlds) ReportWorld(world);
            ReportProfiler();
        });
    }

    // Spread over a square grid around the player, on the ground where there is any. Rings would look
    // nicer and a grid is what a streaming test wants: predictable, and it crosses sector edges.
    private static int Fill(World world, int count, RecordId prefab, float spacing)
    {
        Vector3 centre = Vector3.Zero;
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
        {
            centre = world.Get<Transform>(entity).LocalPosition;
            break;
        }

        var terrain = world.Resources.TryGet<Terrain>(out var found) ? found : null;
        int side = (int)MathF.Ceiling(MathF.Sqrt(count));
        int made = 0;

        for (int i = 0; i < count; i++)
        {
            float x = centre.X + ((i % side) - side * 0.5f) * spacing;
            float z = centre.Z + ((i / side) - side * 0.5f) * spacing;
            float y = terrain?.HeightAt(x, z) ?? 0f;
            var at = new Vector3(x, y, z);

            var entity = prefab.IsEmpty ? world.Create(Transform.At(at), "scale") : world.Spawn(prefab, at);
            if (entity.IsNull) break;      // a bad prefab: stop rather than log the same failure N times
            made++;
        }
        world.FlushCommands();
        return made;
    }

    private static void ReportWorld(World world)
    {
        Log.Info(LogCat.Console, $"world '{world.Name}': {world.EntityCount} entities");

        if (world.Resources.TryGet<Terrain>(out var terrain) && terrain != null)
            Log.Info(LogCat.Console, $"  terrain   {terrain.Sectors.Count} sector(s), {world.Origin()}, " +
                                     $"{world.Origin().Rebases} rebase(s)");

        if (world.Resources.TryGet<IPhysicsWorld>(out var space) && space != null)
            Log.Info(LogCat.Console, $"  physics   {space.BodyCount} bodies, {space.StaticCount} statics, " +
                                     $"step {space.LastStepMilliseconds:F2} ms");
    }

    private static void ReportProfiler()
    {
        if (!Profiler.Enabled)
        {
            Log.Info(LogCat.Console, "  (the profiler is off: this is a Shipping build)");
            return;
        }

        Log.Info(LogCat.Console, "  what a frame costs (average over ~20 frames):");
        foreach (var entry in Profiler.All)
        {
            // Everything that costs a tenth of a millisecond, plus the phase totals whatever they cost:
            // a phase at zero is worth seeing, because it says where the time is *not* going.
            bool phase = entry.Name.StartsWith("Fixed.", StringComparison.Ordinal)
                      || entry.Name.StartsWith("Frame.", StringComparison.Ordinal);
            if (!phase && entry.AverageMs < 0.1) continue;
            Log.Info(LogCat.Console, $"    {entry.Name,-34} {entry.AverageMs,7:F3} ms" +
                                     (entry.LastCalls > 1 ? $"  ({entry.LastCalls} calls)" : ""));
        }
    }
}
