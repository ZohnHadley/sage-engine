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
        Components = new ComponentSchema(Records.Json);
        Saves = new SaveSystem(this);
        // The engine's own saved state. A `[SavedResource]` attribute is decoration until something
        // registers the type — which is exactly how reputation, the journal and the weather were all
        // "saved" and none of them were (F24/F40, found by the second pass).
        Saves.RegisterResource<Weather>();
        Records.Register<PrefabRecord>();     // every game places things, so the engine owns the type
        Records.Register<PlacementsRecord>();  // and where it places them, so an editor can write it (15 §3)
        Modules = new ModuleManager(this);
    }

    // Input actions registered by modules in Init (docs/design/08 §3.2); bindings are client-side.
    public ActionRegistry Actions { get; } = new();

    // Components and tags by name, and the prefab parts modules have registered (05 §3.5, F31).
    public ComponentSchema Components { get; }
    public PrefabRegistry Prefabs { get; } = new();

    // Every entity input a level can wire to (04 §3.4, F17). Here rather than on a module because a
    // level's wiring may name an input from any of them, and because `ent_fire` has to check one list.
    public EntityInputs Inputs { get; } = new();

    // Save and load (09 §3.5, F27).
    public SaveSystem Saves { get; }

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

        // Engine modules run before the game's, so this is the first point where "did the game install
        // its own rules?" has a real answer (review #47).
        if (!world.Resources.TryGet<GameRules>(out _))
            world.Resources.Set<GameRules>(new DefaultGameRules());

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
