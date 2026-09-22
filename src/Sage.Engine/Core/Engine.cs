#nullable enable
using System;
using System.Collections.Generic;

namespace sage_engine;

// Process-wide core services (docs/design/01-host-and-modules.md §4, glossary). Created by the host
// and passed in; never a static singleton. Lives in Sage.Engine, so it holds no MonoGame types:
// client services (renderer, input devices, audio) are provided by client modules (ModuleContext).
//
// Today: cvars, the VFS, records, input actions, modules and the worlds. AssetServer, JobSystem and EngineSignals
// join in later steps.
public sealed class Engine : IDisposable
{
    private readonly List<World> _worlds = new();

    public Engine(CVarRegistry cvars, CoreCVars core, VirtualFileSystem? vfs = null, RecordStore? records = null)
    {
        CVars = cvars;
        Core = core;
        Vfs = vfs ?? new VirtualFileSystem();
        Records = records ?? new RecordStore();
        Modules = new ModuleManager(this);
    }

    // Input actions registered by modules in Init (docs/design/08 §3.2); bindings are client-side.
    public ActionRegistry Actions { get; } = new();

    public BuildConfig Config => BuildInfo.Config;
    public CVarRegistry CVars { get; }
    public CoreCVars Core { get; }
    public VirtualFileSystem Vfs { get; }
    public RecordStore Records { get; }
    public ModuleManager Modules { get; }
    public IReadOnlyList<World> Worlds => _worlds;

    // Creating a world initializes the ECS schema: every assembly that defines component types must
    // already be loaded (the host loads them first; see docs/design/03 §3.1).
    public World CreateWorld(string name)
    {
        var world = new World(name, this);
        _worlds.Add(world);
        Log.Info(LogCat.World, $"World '{name}' created ({_worlds.Count} active)");
        Modules.NotifyWorldCreated(world);   // modules install their resources and systems

        // Now that every module has had its turn, the game's rules may populate the world (16 §3.1).
        if (world.Resources.TryGet<GameRules>(out var rules) && rules != null)
            rules.OnWorldStarted(world);
        return world;
    }

    public void DestroyWorld(World world)
    {
        if (!_worlds.Remove(world))
        {
            Assert.Ensure(false, $"DestroyWorld: '{world.Name}' is not one of this engine's worlds");
            return;
        }
        world.Dispose();
    }

    public void Dispose()
    {
        for (int i = _worlds.Count - 1; i >= 0; i--)
            _worlds[i].Dispose();
        _worlds.Clear();
    }
}
