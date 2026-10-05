#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;

namespace Sage.Simulation;

// A world's systems, and the two things a plugin may do to one it did not add (REDESIGN §3.3, issue
// #17): replace it, or turn it off. Both take the stable id, so a mod swaps `sage.ai.think` without
// compiling against the engine's AIThinkSystem, and both are logged and recorded against the plugin
// whose code is running (the RegistrationLedger's owner), because "mod X replaced system Y" is the
// first line of any conflict report.
//
// Call them from OnWorldCreated, in a plugin that depends on the one whose system it changes, so
// that the system is there to change: plugins run in dependency order (01 §3.1).
public sealed class WorldSystems : IEnumerable<SystemInfo>
{
    private readonly World _world;
    private readonly SystemScheduler _scheduler;

    internal WorldSystems(World world, SystemScheduler scheduler)
    {
        _world = world;
        _scheduler = scheduler;
    }

    public SystemInfo? Find(string id) => _scheduler.Find(id);

    // Puts `replacement` where `id` was: the same id, phase, order and run condition, so whatever
    // ordered itself against the old one orders against this. The old system is retired as by
    // RemoveSystem — its event readers released, and disposed if it is IDisposable. False (with a
    // warning) when there is no such system in this world.
    public bool Replace(string id, ISystem replacement)
    {
        if (_scheduler.Find(id) is not { } old)
        {
            Log.Warn(LogCat.World, $"{Caller} asked to replace system '{id}', which is not in '{_world.Name}' " +
                                   "(is its plugin loaded, and does the caller depend on it?)");
            return false;
        }
        if (SystemDeclaration.Of(replacement.GetType()) is { } declared && declared.Phase != old.Phase)
            throw new InvalidOperationException($"{replacement.GetType().Name} is declared for {declared.Phase} and cannot " +
                                                $"replace '{id}', which runs in {old.Phase}");

        var info = _scheduler.Replace(old, replacement, Caller, _world.Declare(replacement));
        _world.Retire(old.System);
        _world.Engine?.Registrations.Record("system", id);
        Log.Info(LogCat.World, $"System '{id}' in '{_world.Name}': {old.Name} (from {old.Owner}) replaced by {info.Name} (from {Caller})");
        return true;
    }

    // Turns `id` off for good: it stays in its place, so constraints naming it still hold, but never
    // runs, and its event readers are released so the queues it read do not wait for it. `sys_toggle`
    // will not turn it back on. False (with a warning) when there is no such system in this world.
    public bool Disable(string id)
    {
        if (_scheduler.Find(id) is not { } info)
        {
            Log.Warn(LogCat.World, $"{Caller} asked to disable system '{id}', which is not in '{_world.Name}' " +
                                   "(is its plugin loaded, and does the caller depend on it?)");
            return false;
        }
        info.Enabled = false;
        info.DisabledBy = Caller;
        _world.Events.Release(info.System);
        _world.Engine?.Registrations.Record("disabled system", id);
        Log.Info(LogCat.World, $"System '{id}' ({info.Name}, from {info.Owner}) in '{_world.Name}' disabled by {Caller}");
        return true;
    }

    private string Caller => _world.Engine?.Registrations.Owner ?? "host";

    // ---- Running side by side (issue #288) ---------------------------------------------------------

    // Whether this world runs systems that declare their access (IDeclaresAccess) at the same time when
    // they do not conflict; null follows `sys_parallel` (on by default). Results are the same either way:
    // this is the switch to compare them, or to rule the scheduler out.
    public bool? Parallel { get; set; }

    // Worker threads for it; null follows `sys_threads`, and 0 there means one fewer than the cores (at
    // most 7). The world's own thread runs systems too.
    public int? Threads { get; set; }

    // Dev builds: 0 = no check, 1 = what a declared system touches is compared with its declaration,
    // 2 = and a component it declared only reading must not change. Null follows `sys_access_check` (2).
    public int? AccessCheckLevel { get; set; }

    private readonly object _reportLock = new();
    private readonly HashSet<(SystemInfo, string, Type?)> _reported = new();
    private readonly List<string> _reports = new();

    // How many different things declared systems touched without declaring them (dev builds): each is
    // reported once, as an error naming the system and the type. A test asserts this is zero.
    public int AccessViolations
    {
        get { lock (_reportLock) return _reports.Count; }
    }

    // The reports themselves, in the order they were found.
    public IReadOnlyList<string> AccessReports
    {
        get { lock (_reportLock) return _reports.ToArray(); }
    }

    // What the workers allocated running systems (bytes, since the world was made): the half of a
    // zero-allocation check the world's own thread cannot see.
    internal long WorkerAllocatedBytes;

    internal void ReportAccess(SystemInfo system, string what, Type? type)
    {
        string message;
        lock (_reportLock)
        {
            if (!_reported.Add((system, what, type))) return;
            message = $"System {system.Id ?? system.Name} in '{_world.Name}' {what}{(type == null ? "" : $" {type.Name}")}, " +
                      $"which its access declaration does not admit (declared: {system.Access}).";
            _reports.Add(message);
        }
        Log.Error(LogCat.World, message);
    }

    public IEnumerator<SystemInfo> GetEnumerator() => _scheduler.All.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
