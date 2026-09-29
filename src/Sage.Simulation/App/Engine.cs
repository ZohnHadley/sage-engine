#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Process-wide core services (docs/design/01-host-and-modules.md §4, glossary). Created by the host
// and passed in; never a static singleton. Lives in Sage.Simulation, so it holds no MonoGame types:
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
        // Entity handles in record JSON read as the null entity (EntityJsonConverter): the record store
        // is the kernel's and knows no ECS, so the engine adds it, where the store's own list had it.
        Records.Json.Converters.Insert(3, new EntityJsonConverter());
        Components = new ComponentSchema(Records.Json);
        Saves = new SaveSystem(this);
        // Every registry records who registered what (issue #12).
        CVars.Ledger = Registrations;
        Records.Ledger = Registrations;
        Prefabs.Ledger = Registrations;
        Inputs.Ledger = Registrations;
        Outputs.Ledger = Registrations;
        Actions.Ledger = Registrations;
        // Prefab bodies are checked as content loads (issue #22): components, parts, their fields and
        // what they name, at their lines, rather than at the first spawn.
        Records.AddCheck<PrefabRecord>((prefab, check) => PrefabChecks.Check(this, prefab, check));
        // And a patch of another namespace's prefab means its own namespace by a bare id inside a
        // component or a part, as it does in any other field (R11).
        Records.AddBodyTypes<PrefabRecord>(nameof(PrefabRecord.Components), (key, body, ns) =>
            Components.TryResolveComponent(key, ns, out var type, out _) ? type : null);
        Records.AddBodyTypes<PrefabRecord>(nameof(PrefabRecord.Parts), (key, body, _) => PrefabChecks.BodyType(Prefabs, key, body));
        // The engine's own declarations (Plugin = RegistrationOwners.Core): prefabs and placements,
        // which every game uses, and the weather every world saves. Registered by generated code
        // (issue #16), because an attribute used to be decoration until someone also registered the
        // type — which is how reputation, the journal and the weather were all "saved" and none of
        // them were (F24/F40). Each plugin's own are registered just before its Init.
        Generated.Include(typeof(Engine).Assembly);
        SystemCatalog.Include(typeof(Engine).Assembly);
        Registrations.Owner = RegistrationOwners.Core;
        try { Generated.Register(RegistrationOwners.Core, new RegistrationBuilder(this)); }
        finally { Registrations.Owner = "host"; }
        Modules = new ModuleManager(this);
    }

    // Who registered each cvar, command, record type, prefab part, entity input and action (issue #12).
    public RegistrationLedger Registrations { get; } = new();

    // The generated registrations of the engine's and every loaded module's assemblies (issue #16).
    public GeneratedRegistrations Generated { get; } = new();

    // Every system id the engine's and the loaded modules' assemblies declare (issue #17): what tells a
    // mistyped ordering constraint from one naming a system whose plugin is turned off.
    public SystemCatalog SystemCatalog { get; } = new();

    // Input actions registered by modules in Init (docs/design/08 §3.2); bindings are client-side.
    public ActionRegistry Actions { get; } = new();

    // Components and tags by name, and the prefab parts plugins have declared (05 §3.5, F31, issue #17).
    public ComponentSchema Components { get; }
    public PrefabRegistry Prefabs { get; } = new();

    // Every entity input a level can wire to (04 §3.4, F17). Here rather than on a module because a
    // level's wiring may name an input from any of them, and because `ent_fire` has to check one list.
    public EntityInputs Inputs { get; } = new();

    // And the outputs a wire can listen for, with what each means (issue #18).
    public EntityOutputs Outputs { get; } = new();

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
        // Prefab parts and entity inputs are per engine, and a world's entities are built from them:
        // one added after the first world would exist in some worlds and not others.
        Prefabs.Seal.Seal("the first world was created");
        Inputs.Seal.Seal("the first world was created");
        Outputs.Seal.Seal("the first world was created");
        var world = new World(name, this);
        _worlds.Add(world);
        Log.Info(LogCat.World, $"World '{name}' created ({_worlds.Count} active)");
        Modules.NotifyWorldCreated(world);   // modules install their resources and systems

        // The game's rules, now that every module has furnished the world (16 §3.1, issue #13): the game
        // module's CreateRules if it has any, else rules a module installed in OnWorldCreated (the older
        // way, review #47), else the defaults.
        GameRules rules;
        if (Modules.Game?.CreateRules(world) is { } fromGame)
        {
            if (world.Resources.TryGet<GameRules>(out var installed) && installed != null)
                Log.Warn(LogCat.World, $"World '{name}': {Modules.Game.Name}.CreateRules returned {fromGame.GetType().Name}, " +
                                       $"replacing the {installed.GetType().Name} a module installed in OnWorldCreated");
            world.Resources.Replace<GameRules>(fromGame);
            rules = fromGame;
        }
        else if (world.Resources.TryGet<GameRules>(out var installed) && installed != null)
        {
            rules = installed;
        }
        else
        {
            rules = new DefaultGameRules();
            world.Resources.Add<GameRules>(rules);
        }
        Log.Info(LogCat.World, $"World '{name}': rules {rules.GetType().Name}");

        // Now that every module has had its turn, the game's rules may populate the world (16 §3.1).
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
