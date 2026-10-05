#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace Sage.Simulation;

// Console commands over the engine's worlds (docs/design/03 §9): each a ConsoleCommand, registered on
// the cvar registry. Not to be confused with EntityCommands (a world's deferred structural changes) or
// PlayerCommand (one tick of a player's input), the other two "commands" (issue #25).
internal static class WorldConsoleCommands
{
    public static void Register(CVarRegistry cvars, Engine engine)
    {
        engine.Scheduling = new SchedulingCVars(cvars);   // sys_parallel, sys_threads, sys_access_check (#288)

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
            else if (!world.TryGetMainView(out var camera))   // in front of the screen's view (#81)
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
                    if (!world.IsAlive(entity)) continue;   // a prefab's child, gone with its parent
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
                    // Fields a placement or a map key overrode are marked `*` (phase 4i), and part options,
                    // which are not a component's, are listed after as the overrides wrote them.
                    var overridden = e.TryGetComponent<PrefabOverridden>(out var o) ? o.Overrides : null;
                    string prefabNs = e.TryGetComponent<FromPrefab>(out var from) ? from.Prefab.Namespace : "sage";
                    foreach (var (componentId, value) in engine.Components.ComponentsOf(e))
                        Log.Info(LogCat.Console, $"  {componentId,-28} {Describe(value, OverriddenFields(engine, overridden, prefabNs, value.GetType()))}");
                    if (overridden?.Parts is { Count: > 0 } parts)
                        Log.Info(LogCat.Console, $"  {"overridden parts",-28} {string.Join("  ", parts.Select(p => $"{p.Key}*={Short(p.Value?.ToJsonString() ?? "null")}"))}");
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
            // Each part with its options and its plugin, in the order they apply (issue #17): a part's
            // public fields are what a prefab may write under it, so this is the part's whole manual.
            Log.Info(LogCat.Console, "prefab parts, in the order they apply:");
            // Fields from the metadata table (issue #18): name, type, unit and range, as content writes them.
            foreach (var part in engine.Prefabs.Parts.Where(p => Show(p.Id)))
            {
                var fields = Metadata.Of(part.Type).Fields
                    .Select(f => FieldSummary(f) + (f.Name == part.Shorthand ? " (shorthand)" : ""));
                Log.Info(LogCat.Console, $"  {part.Id,-12} {{ {string.Join(", ", fields)} }}  [{part.Owner}]" +
                                         (part.After.Count > 0 ? $" after {string.Join(", ", part.After)}" : ""));
            }
        });

        RegisterClock(cvars, engine);

        // Id, type and the plugin that added it (issue #17), plus who replaced or disabled it: the
        // question a mod conflict starts with is "whose is this, and who touched it".
        cvars.RegisterCommand("sys_list", CVarFlags.None,
            "List systems per world and phase: id, type, owning plugin and average ms (profiler).", _ =>
        {
            foreach (var world in engine.Worlds)
            {
                Log.Info(LogCat.Console, $"'{world.Name}' (tick {world.Tick}{(world.Paused ? ", paused" : "")}):");
                foreach (var s in world.Systems.OrderBy(s => s.Phase))
                {
                    var p = Profiler.Find(s.ProfileName);
                    string ms = p == null ? "   -   " : $"{p.AverageMs,6:F3}";
                    string state = s.DisabledBy != null ? $" (disabled by {s.DisabledBy})" : s.Enabled ? "" : " (disabled)";
                    string replaced = s.ReplacedBy != null ? $", replaced by {s.ReplacedBy}" : "";
                    string access = s.Access != null ? $" <{s.Access}>" : "";   // declared access (issue #288)
                    Log.Info(LogCat.Console, $"  {s.Phase,-12} {ms} ms  {s.Id ?? "(unnamed)",-32} {s.Name} [{s.Owner}{replaced}]{state}{access}");
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

        cvars.RegisterCommand("sys_toggle", CVarFlags.DevOnly, "sys_toggle <id or type name>: enable/disable a system in every world.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "sys_toggle <id or type name>"); return; }
            int found = 0;
            foreach (var s in engine.Worlds.SelectMany(w => w.Systems)
                         .Where(s => s.Name.Equals(a[0], StringComparison.OrdinalIgnoreCase) ||
                                     (s.Id != null && s.Id.Equals(a[0], StringComparison.OrdinalIgnoreCase))))
            {
                found++;
                if (s.DisabledBy != null)
                {
                    // A plugin turned it off for good and released its event readers; running it again
                    // would read queues nothing kept for it.
                    Log.Warn(LogCat.Console, $"{s.Id ?? s.Name}: disabled by {s.DisabledBy}; not toggled");
                    continue;
                }
                s.Enabled = !s.Enabled;
                Log.Info(LogCat.Console, $"{s.Id ?? s.Name}: {(s.Enabled ? "enabled" : "disabled")}");
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

    // Component values the way a prefab spells them: `field=value unit`, skipping what is at its
    // default, so a dump is the interesting part of an entity rather than a wall of zeroes. The fields,
    // their names, units and defaults come from the metadata table (issue #18).
    // The fields of `type` that an entity's overrides write, by JSON name; null when none.
    private static HashSet<string>? OverriddenFields(Engine engine, PrefabOverrides? overrides, string ns, Type type)
    {
        if (overrides?.Components is not { Count: > 0 } components) return null;
        HashSet<string>? fields = null;
        foreach (var (name, body) in components)
        {
            if (!engine.Components.TryResolveComponent(name, ns, out var resolved, out _) || resolved != type) continue;
            if (body is not System.Text.Json.Nodes.JsonObject written) continue;
            foreach (var (field, _) in written) (fields ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(field.Replace("_", ""));
        }
        return fields;
    }

    private static string Describe(object value, HashSet<string>? overridden = null)
    {
        var meta = Metadata.Of(value.GetType());
        var parts = new List<string>();
        foreach (var field in meta.Fields)
        {
            if (field.Get == null) continue;
            object? got;
            try { got = field.Get(value); } catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException) { continue; }
            bool isOverridden = overridden != null && (overridden.Contains(field.JsonName.Replace("_", "")) || overridden.Contains(field.Name));
            if (!isOverridden && Same(got, meta.DefaultOf(field))) continue;
            string unit = field.Unit != null && got is float or double or int or Vector3 or Vector2 ? " " + field.Unit : "";
            parts.Add($"{field.JsonName}{(isOverridden ? "*" : "")}={Short(Format(got))}{unit}");
        }
        return parts.Count == 0 ? "(defaults)" : string.Join("  ", parts);
    }

    private static bool Same(object? a, object? b) =>
        Equals(a, b) || a is System.Collections.ICollection { Count: 0 } && b is null or System.Collections.ICollection { Count: 0 };

    // `range: float m, 0..`, `shape: ColliderShape (Box|Sphere|Capsule|Mesh)`, `item: RecordId -> item`.
    internal static string FieldSummary(FieldMetadata f)
    {
        var text = new System.Text.StringBuilder($"{f.JsonName}: {f.TypeName}");
        if (f.Unit != null) text.Append(' ').Append(f.Unit);
        if (f.Min != null || f.Max != null)
            text.Append($" {f.Min?.ToString(System.Globalization.CultureInfo.InvariantCulture)}..{f.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (f.EnumValues.Count > 0) text.Append(" (").Append(string.Join("|", f.EnumValues)).Append(')');
        var target = f.Item ?? f;
        if (target.RecordType != null) text.Append(" -> ").Append(target.RecordType);
        if (target.AssetKind != null) text.Append(" -> ").Append(target.AssetKind).Append(" asset");
        return text.ToString();
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

    // ", speed x0.5, paused": the world's time (WorldTime), for `time` and `world_speed`.
    private static string Speed(World world)
    {
        var time = WorldTime.Of(world);
        return $" speed x{time.Scale:0.##}" + (time.HostScale != 1.0 ? $" (host x{time.HostScale:0.##})" : "")
             + (time.Paused ? ", paused" : "") + (time.HitStopLeft > 0 ? $", hit-stop {time.HitStopLeft:0.###} s" : "");
    }

    // `time`, `time_set` and `time_scale` (issue 4h-2): the world's clock, from the console; `world_speed`
    // and `hit_stop` (issue #283): the world's time.
    private static void RegisterClock(CVarRegistry cvars, Engine engine)
    {
        cvars.RegisterCommand("time", CVarFlags.None,
            "time: the time of day in every world, its speed and its sky.", _ =>
        {
            foreach (var world in engine.Worlds)
            {
                if (!world.Resources.TryGet<WorldClock>(out var clock) || clock == null) continue;
                Log.Info(LogCat.Console,
                    $"'{world.Name}': day {clock.Day} ({Calendars.Today(world)}) {WorldClock.Format(clock.Hour)} (x{clock.Scale:0.##} game seconds a second)" +
                    (clock.Sky.IsEmpty ? ", no sky" : $", sky {clock.Sky}") + "," + Speed(world));
            }
        });

        cvars.RegisterCommand("time_set", CVarFlags.Cheat,
            "time_set <hour|hh:mm>: set the time of day in every world (the day does not change).", a =>
        {
            if (a.Count == 0 || !WorldClock.TryParseHour(a[0], out double hour) || hour < 0 || hour > 24)
            {
                Log.Warn(LogCat.Console, "time_set <hour|hh:mm>, e.g. time_set 18:30 or time_set 18.5");
                return;
            }
            foreach (var world in engine.Worlds)
            {
                if (!world.Resources.TryGet<WorldClock>(out var clock) || clock == null) continue;
                clock.Hour = hour;
                Log.Info(LogCat.Console, $"'{world.Name}': {WorldClock.Format(clock.Hour)}");
            }
        });

        cvars.RegisterCommand("time_pass", CVarFlags.Cheat,
            "time_pass <hours>: pass game time at once, in every world (one skip at the next tick boundary, one TimePassed event; not `wait`, which pauses a script).", a =>
        {
            if (a.Count == 0 || !double.TryParse(a[0], System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out double hours)
                || double.IsNaN(hours) || double.IsInfinity(hours) || hours <= 0)
            {
                Log.Warn(LogCat.Console, "time_pass <hours>, hours > 0, e.g. time_pass 30");
                return;
            }
            foreach (var world in engine.Worlds)
                Time.Pass(world, hours, "console");
        });

        // The world's own speed (WorldTime, issue #283): how many simulation steps a real second runs.
        // Not `time_scale`, which is how fast the clock's hours go by in those steps.
        cvars.RegisterCommand("world_speed", CVarFlags.Cheat,
            "world_speed [n]: the simulation's speed in every world (1 normal, 0.5 bullet time, 0 stopped); no number: show it.", a =>
        {
            if (a.Count == 0)
            {
                foreach (var world in engine.Worlds) Log.Info(LogCat.Console, $"'{world.Name}':{Speed(world)}");
                return;
            }
            if (!double.TryParse(a[0], System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out double speed)
                || !double.IsFinite(speed) || speed < 0 || speed > WorldTime.MaxScale)
            {
                Log.Warn(LogCat.Console, $"world_speed <n>, 0 <= n <= {WorldTime.MaxScale}");
                return;
            }
            foreach (var world in engine.Worlds)
            {
                WorldTime.Of(world).Scale = speed;
                Log.Info(LogCat.Console, $"'{world.Name}':{Speed(world)}");
            }
        });

        cvars.RegisterCommand("hit_stop", CVarFlags.Cheat,
            "hit_stop <seconds>: freeze the simulation in every world for that many real seconds.", a =>
        {
            if (a.Count == 0 || !double.TryParse(a[0], System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out double seconds)
                || !double.IsFinite(seconds) || seconds <= 0)
            {
                Log.Warn(LogCat.Console, "hit_stop <seconds>, seconds > 0, e.g. hit_stop 0.1");
                return;
            }
            foreach (var world in engine.Worlds) WorldTime.Of(world).HitStop(seconds);
        });

        cvars.RegisterCommand("time_scale", CVarFlags.Cheat,
            "time_scale <n>: game seconds per real second, in every world (60: a minute a second; 0 stops the clock).", a =>
        {
            if (a.Count == 0 || !double.TryParse(a[0], System.Globalization.NumberStyles.Float,
                                                 System.Globalization.CultureInfo.InvariantCulture, out double scale)
                || scale < 0 || double.IsInfinity(scale) || double.IsNaN(scale))
            {
                Log.Warn(LogCat.Console, "time_scale <n>, n >= 0");
                return;
            }
            foreach (var world in engine.Worlds)
            {
                if (!world.Resources.TryGet<WorldClock>(out var clock) || clock == null) continue;
                clock.Scale = scale;
                Log.Info(LogCat.Console, $"'{world.Name}': x{scale:0.##}");
            }
        });
    }
}
