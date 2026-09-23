#nullable enable
using System;
using System.Linq;

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
}
