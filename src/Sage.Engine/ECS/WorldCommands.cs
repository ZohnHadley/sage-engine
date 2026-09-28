#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Friflo.Engine.ECS;

namespace sage_engine;

// Console commands over the engine's worlds (docs/design/03 §9).
public static class WorldCommands
{
    public static void Register(CVarRegistry cvars, Engine engine)
    {
        cvars.RegisterCommand("ent_list", CVarFlags.None, "ent_list [filter]: list entities in every world.", a =>
        {
            string filter = a.Count > 0 ? a[0] : "";
            foreach (var world in engine.Worlds)
            {
                int shown = 0;
                foreach (var e in world.QueryAll().Entities)
                {
                    string label = World.Describe(e);
                    if (filter.Length > 0 && !label.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                    Log.Info(LogCat.Console, $"  [{world.Name}] {label}: {e.Components.Count} components");
                    shown++;
                }
                Log.Info(LogCat.Console, $"'{world.Name}': {shown} of {world.EntityCount} entities");
            }
        });

        // Place a prefab (F31). No position means in front of whatever the camera is looking at, so
        // `ent_spawn goblin` puts one where you can see it.
        cvars.RegisterCommand("ent_spawn", CVarFlags.Cheat,
            "ent_spawn <prefab> [x y z] [yaw]: place a prefab, by default 3 m in front of the camera.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ent_spawn <prefab> [x y z] [yaw]"); return; }
            var world = engine.Worlds.Count > 0 ? engine.Worlds[0] : null;
            if (world == null) { Log.Warn(LogCat.Console, "ent_spawn: no world"); return; }

            var id = engine.Records.Resolve("prefab", a[0]);
            if (id.IsEmpty) { Log.Warn(LogCat.Console, $"ent_spawn: no prefab '{a[0]}'"); return; }

            Vector3 position;
            float yaw = 0f;
            if (a.Count >= 4 && float.TryParse(a[1], out float x) && float.TryParse(a[2], out float y) && float.TryParse(a[3], out float z))
            {
                position = new Vector3(x, y, z);
                if (a.Count >= 5 && float.TryParse(a[4], out float given)) yaw = given;
            }
            else if (!world.Resources.TryGet<ActiveCamera>(out var camera) || camera == null)
            {
                Log.Warn(LogCat.Console, "ent_spawn: this world has no camera to spawn in front of; give x y z");
                return;
            }
            else
            {
                position = camera.Position + SageMath.ForwardFromYaw(SageMath.YawOf(camera.Rotation)) * 3f;
                yaw = SageMath.YawOf(camera.Rotation) * 180f / MathF.PI + 180f;   // facing back at the camera
            }

            var entity = world.Spawn(id, position, yaw);
            Log.Info(LogCat.Console, entity.IsNull
                ? $"ent_spawn: {id} failed (see the log)"
                : $"ent_spawn: {World.Describe(entity)} at {position.X:F1}, {position.Y:F1}, {position.Z:F1}");
        });

        // What is actually on an entity: the answer to "why is this thing not moving" without a
        // debugger (engine review 2026-09-23, item 8).
        cvars.RegisterCommand("ent_destroy", CVarFlags.Cheat,
            "ent_destroy <name>: remove every entity with this name, to see what depends on it.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ent_destroy <name>"); return; }
            int destroyed = 0;
            foreach (var world in engine.Worlds)
                foreach (var entity in world.Query<Transform>().Entities.ToEntityList())
                {
                    if (!World.Describe(entity).Contains(a[0], StringComparison.OrdinalIgnoreCase)) continue;
                    world.Destroy(entity);
                    destroyed++;
                }
            foreach (var world in engine.Worlds) world.FlushCommands();
            Log.Info(LogCat.Console, destroyed == 0 ? $"nothing named '{a[0]}'" : $"destroyed {destroyed}");
        });

        cvars.RegisterCommand("ent_dump", CVarFlags.None,
            "ent_dump <id|name>: every component on an entity, with its field values.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ent_dump <id|name>"); return; }
            int found = 0;
            foreach (var world in engine.Worlds)
            {
                foreach (var e in world.QueryAll().Entities)
                {
                    bool matches = int.TryParse(a[0], out int id)
                        ? e.Id == id
                        : World.Describe(e).Contains(a[0], StringComparison.OrdinalIgnoreCase);
                    if (!matches) continue;

                    found++;
                    Log.Info(LogCat.Console, $"[{world.Name}] {World.Describe(e)}");
                    foreach (var (componentId, value) in engine.Components.ComponentsOf(e))
                        Log.Info(LogCat.Console, $"  {componentId,-28} {Describe(value)}");
                    string tags = string.Join(", ", engine.Components.TagsOf(e));
                    if (tags.Length > 0) Log.Info(LogCat.Console, $"  {"tags",-28} {tags}");
                    if (found >= 8) { Log.Info(LogCat.Console, "  ... (stopping at 8 matches)"); return; }
                }
            }
            if (found == 0) Log.Warn(LogCat.Console, $"ent_dump: nothing matching '{a[0]}'");
        });

        cvars.RegisterCommand("ent_types", CVarFlags.None,
            "ent_types [filter]: component and tag ids, as a prefab or a save spells them (issue #16).", a =>
        {
            string filter = a.Count > 0 ? a[0] : "";
            bool Show(string n) => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase);
            Log.Info(LogCat.Console, "components: " + string.Join(", ", engine.Components.ComponentIds.Where(Show).OrderBy(n => n, StringComparer.Ordinal)));
            Log.Info(LogCat.Console, "tags:       " + string.Join(", ", engine.Components.TagIds.Where(Show).OrderBy(n => n, StringComparer.Ordinal)));
            Log.Info(LogCat.Console, "prefab parts: " + string.Join(", ", engine.Prefabs.Names));
        });

        cvars.RegisterCommand("sys_list", CVarFlags.None, "List systems per world and phase, with average ms (profiler).", _ =>
        {
            foreach (var world in engine.Worlds)
            {
                Log.Info(LogCat.Console, $"'{world.Name}' (tick {world.Tick}{(world.Paused ? ", paused" : "")}):");
                foreach (var s in world.Systems.OrderBy(s => s.Phase))
                {
                    var p = Profiler.Find(s.ProfileName);
                    string ms = p == null ? "   -   " : $"{p.AverageMs,6:F3}";
                    string state = s.Enabled ? "" : " (disabled)";
                    Log.Info(LogCat.Console, $"  {s.Phase,-12} {ms} ms  {s.Name}{state}");
                }
            }
        });

        // What the bus is holding and who is behind (04 §9). The answer to "why is this queue growing"
        // is always a reader whose phase stopped running.
        cvars.RegisterCommand("ev_stats", CVarFlags.None, "Game event queues: size, readers and how long the oldest has waited.", _ =>
        {
            foreach (var world in engine.Worlds)
            {
                long tick = world.Tick;
                int shown = 0;
                foreach (var (name, schedule, count, readers, oldest) in world.Events.Stats())
                {
                    if (count == 0 && readers == 0) continue;
                    string age = count == 0 ? "-" : $"{tick - oldest} ticks";
                    Log.Info(LogCat.Console, $"  {name} ({schedule}): {count} queued, {readers} reader(s), oldest {age}");
                    shown++;
                }
                Log.Info(LogCat.Console, shown == 0
                    ? $"{world.Name}: no game events in use"
                    : $"{world.Name}: {shown} event type(s), ev_maxage {world.Events.MaxAge}");
            }
        });

        cvars.RegisterCommand("sys_toggle", CVarFlags.DevOnly, "sys_toggle <name>: enable/disable a system in every world.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "sys_toggle <name>"); return; }
            int found = 0;
            foreach (var s in engine.Worlds.SelectMany(w => w.Systems)
                         .Where(s => s.Name.Equals(a[0], StringComparison.OrdinalIgnoreCase)))
            {
                s.Enabled = !s.Enabled;
                found++;
                Log.Info(LogCat.Console, $"{s.Name}: {(s.Enabled ? "enabled" : "disabled")}");
            }
            if (found == 0) Log.Warn(LogCat.Console, $"sys_toggle: no system named {a[0]} (see sys_list)");
        });

        cvars.RegisterCommand("pause", CVarFlags.None, "Pause or resume the simulation (Frame systems keep running).", _ =>
        {
            bool pause = !engine.Worlds.Any(w => w.Paused);
            foreach (var world in engine.Worlds) world.Paused = pause;
            Log.Info(LogCat.Console, pause ? "Paused" : "Resumed");
        });
    }

    // Component values the way a prefab spells them: `field=value`, skipping what is at its default,
    // so a dump is the interesting part of an entity rather than a wall of zeroes.
    private static string Describe(object value)
    {
        var type = value.GetType();
        var parts = new List<string>();
        object? blank = null;
        try { blank = Activator.CreateInstance(type); } catch { /* no default ctor: show everything */ }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object? got = field.GetValue(value);
            if (blank != null && Equals(got, field.GetValue(blank))) continue;
            parts.Add($"{Lower(field.Name)}={Short(Format(got))}");
        }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
            object? got;
            try { got = property.GetValue(value); } catch { continue; }
            if (blank != null)
            {
                object? other = null;
                try { other = property.GetValue(blank); } catch { /* fall through and show it */ }
                if (Equals(got, other)) continue;
            }
            parts.Add($"{Lower(property.Name)}={Short(Format(got))}");
        }
        return parts.Count == 0 ? "(defaults)" : string.Join("  ", parts);
    }

    private static string Lower(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    // A dump is for reading. A derived matrix or a packed buffer says nothing at this width.
    private static string Short(string value) => value.Length <= 48 ? value : value[..45] + "...";

    private static string Format(object? value) => value switch
    {
        null => "-",
        Entity e => World.Describe(e),
        Vector3 v => $"[{v.X:F2}, {v.Y:F2}, {v.Z:F2}]",
        Vector2 v => $"[{v.X:F2}, {v.Y:F2}]",
        Quaternion q => $"yaw {SageMath.YawOf(q) * 180f / MathF.PI:F0}\u00b0",
        float f => f.ToString("F3"),
        System.Collections.ICollection c => $"{c.Count} item(s)",
        _ => value.ToString() ?? "-",
    };
}
