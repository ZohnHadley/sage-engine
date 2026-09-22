#nullable enable
using System;
using System.Collections.Generic;

namespace sage_engine;

// Process-wide core services (docs/design/01-host-and-modules.md §4, glossary). Created by the host
// and passed in; never a static singleton. Lives in Sage.Engine, so it holds no MonoGame types:
// client services (renderer, input devices, audio) are owned by client code (modules provide them
// from migration step 5).
//
// Today: cvars and the worlds. VFS, AssetServer, RecordStore, JobSystem and EngineSignals join in
// later migration steps.
public sealed class Engine : IDisposable
{
    private readonly List<World> _worlds = new();

    public Engine(CVarRegistry cvars, CoreCVars core)
    {
        CVars = cvars;
        Core = core;
    }

    public BuildConfig Config => BuildInfo.Config;
    public CVarRegistry CVars { get; }
    public CoreCVars Core { get; }
    public IReadOnlyList<World> Worlds => _worlds;

    // Creating a world initializes the ECS schema: every assembly that defines component types must
    // already be loaded (the host loads them first; see docs/design/03 §3.1).
    public World CreateWorld(string name)
    {
        var world = new World(name, this);
        _worlds.Add(world);
        Log.Info(LogCat.World, $"World '{name}' created ({_worlds.Count} active)");
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
