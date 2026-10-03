#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Process-wide core services (docs/design/01-host-and-modules.md §4, glossary). Created by the host
// and passed in; never a static singleton. Lives in Sage.Simulation, so it holds no MonoGame types:
// client services (renderer, input devices, audio) are provided by client modules (ModuleContext).
//
// Today: cvars, the VFS, records, input actions, modules, the worlds and EngineSignals (#282). AssetServer
// and JobSystem join in later steps.
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
        Animations = new GltfAnimationReader(Vfs);
        Animations.UseEvents(Records);                        // anim_events records (issue #119)
        // Every registry records who registered what (issue #12).
        CVars.Ledger = Registrations;
        Records.Ledger = Registrations;
        Prefabs.Ledger = Registrations;
        Inputs.Ledger = Registrations;
        Outputs.Ledger = Registrations;
        Actions.Ledger = Registrations;
        Vocabularies.Ledger = Registrations;
        // Fields typed as a vocabulary (issue #28) read the entry their JSON names, from this engine's
        // registries; bare ids inside one are qualified as the entry's own type.
        Records.Json.Converters.Add(new VocabularyJsonConverterFactory(Vocabularies));
        Records.PolymorphicTypes = Vocabularies.ConcreteTypeOf;
        Records.PolymorphicShorthand = Vocabularies.Expand;   // `{ "has_item": "key" }` (issue #89)
        // Prefab bodies are checked as content loads (issue #22): components, parts, their fields and
        // what they name, at their lines, rather than at the first spawn.
        Records.AddCheck<PrefabRecord>((prefab, check) => PrefabChecks.Check(this, prefab, check));
        // And a patch of another namespace's prefab means its own namespace by a bare id inside a
        // component or a part, as it does in any other field (R11).
        Records.AddBodyTypes<PrefabRecord>(nameof(PrefabRecord.Components), (key, body, ns) =>
            Components.TryResolveComponent(key, ns, out var type, out _) ? type : null);
        Records.AddBodyTypes<PrefabRecord>(nameof(PrefabRecord.Parts), (key, body, _) => PrefabChecks.BodyType(Prefabs, key, body));
        // Sockets name a model and a joint (issue #120).
        Records.AddCheck<SkeletonSocketsRecord>(BoneAttachments.Check);
        // A ragdoll names a model and bodies with shapes, masses and limits (issue #243).
        Records.AddCheck<RagdollRecord>(Ragdolls.Check);
        // Clip events name a model, and each a time and a name (issue #119).
        Records.AddCheck<AnimEventsRecord>(AnimEvents.Check);
        // First-person arms name a model, and a weapon a socket (issue #121).
        Records.AddCheck<ViewmodelRecord>(ViewmodelRecord.Check);
        // The engine's own declarations (Plugin = RegistrationOwners.Core): prefabs and placements,
        // which every game uses, and the weather every world saves. Registered by generated code
        // (issue #16), because an attribute used to be decoration until someone also registered the
        // type — which is how reputation, the journal and the weather were all "saved" and none of
        // them were (F24/F40). Each plugin's own are registered just before its Init.
        Generated.Include(typeof(Engine).Assembly);
        SystemCatalog.Include(typeof(Engine).Assembly);
        Registrations.Owner = RegistrationOwners.Core;
        try
        {
            Generated.Register(RegistrationOwners.Core, new RegistrationBuilder(this));
            // And by hand, what has no declaration yet: the camera inputs and outputs (issue #80; the
            // engine's like the camera itself, so a data-only game can wire a cut — see CameraIO).
            CameraIO.Register(this);
            // Timers and tweens (issue #90): the engine's for the same reason; their outputs need the
            // I/O plugin to reach a wire, as every output does.
            Timers.Register(this);
            Tweens.Register(this);
            CalendarEvents.Register(this);                       // OnCalendarEvent (issue 4m-15)
            // Relays, counters, comparisons, branches and remaps (issue #91), the same way.
            LogicEntities.Register(this);
            // State machines (issue #92): the engine's too; SetState, OnStateChanged and the `on` names
            // content listens for.
            StateMachines.Register(this);
            // Animation graphs and animators (issue #118): SetAnimParam, AnimTrigger, anim_debug.
            Animators.Register(this);
            // Ragdolls (issue #246): the `Ragdoll` input.
            Ragdolls.Register(this);
            Ragdolls.RegisterGetUp(this);                         // and the `GetUp` input (issue #247)
            // Load doors (issue 4g-5): the `Travel` input, the engine's like scenes.
            Travel.Register(this);
            // Quick-save and quick-load (issue 4i-6): the engine's, bound to F5 and F9 in engine content.
            Actions.Register(SaveSystem.QuickSaveAction, ActionKind.Button);
            Actions.Register(SaveSystem.QuickLoadAction, ActionKind.Button);
        }
        finally { Registrations.Owner = "host"; }
        Modules = new ModuleManager(this);
        Scenes = new Scenes(this);
    }

    // Who registered each cvar, command, record type, prefab part, entity input and action (issue #12).
    public RegistrationLedger Registrations { get; } = new();

    // The generated registrations of the engine's and every loaded module's assemblies (issue #16).
    public GeneratedRegistrations Generated { get; } = new();

    // Every system id the engine's and the loaded modules' assemblies declare (issue #17): what tells a
    // mistyped ordering constraint from one naming a system whose plugin is turned off.
    public SystemCatalog SystemCatalog { get; } = new();

    // The open vocabularies (issue #28): AI conditions and schedule selectors, quest objectives,
    // dialogue conditions and actions, ability deliveries, effect executions, item uses, and any a
    // game declares. Entries are declared ([AICondition("is_night")] …) and registered by generated
    // code for their plugin; sealed when content loads.
    public Vocabularies Vocabularies { get; } = new();

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

    // Skinned models' skeletons and clips (issue #118), read through the VFS and cached by path, shared
    // by every world (each has it as a resource). The `animator` part loads its model when it is
    // applied (content time); AnimatorSystem only asks the cache. Hot reload: Forget a path, re-spawn.
    [System.Diagnostics.CodeAnalysis.Experimental(AnimationApi.Experimental, UrlFormat = AnimationApi.Url)]
    public GltfAnimationReader Animations { get; }

    // Save and load (09 §3.5, F27).
    public SaveSystem Saves { get; }

    // What each world starts with, and hot reload of it (issue #29).
    public Scenes Scenes { get; }

    // The mods this run loaded and the ones it refused (phase 4j). Empty until the app fills it at boot.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
    public ModLoadResult Mods { get; set; } = ModLoadResult.Empty;

    // What was found, and the player's choices for the next start: mod_enable / mod_disable / mod_move and
    // a mods screen go through it (4j-3). An app with no game has an empty one.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
    public ModManager ModManager { get; internal set; } = ModManager.None();

    public BuildConfig Config => BuildInfo.Config;
    public CVarRegistry CVars { get; }
    public CoreCVars Core { get; }
    public VirtualFileSystem Vfs { get; }
    public RecordStore Records { get; }
    public ModuleManager Modules { get; }
    public IReadOnlyList<World> Worlds => _worlds;

    // World created, scene loaded, paused: as C# events here and as `EngineSignal` in each world (#282).
    public EngineSignals Signals { get; } = new();

    // Creating a world initializes the ECS schema: every assembly that defines component types must
    // already be loaded (the host loads them first; see docs/design/03 §3.1).
    public World CreateWorld(string name) => CreateWorld(name, editing: false, Scenes.Start);

    // An **edit world** (phase 10a, issue #219): furnished like any world (every module's resources and
    // systems, the game's rules) and placed with `scene`, the game's start scene when that is empty, but
    // `World.Editing`, so none of its Fixed systems run, and its rules are never started: no player is
    // spawned, and nothing the rules do on starting happens. What the editor shows is the scene as its
    // files say, under a free camera. With no scene and no start scene, the world is empty.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor (phase 10a)
    public World CreateEditWorld(string name, RecordId scene = default) =>
        CreateWorld(name, editing: true, scene.IsEmpty ? Scenes.Start : scene);

    // A **play world** for play-in-editor (phase 10a, issue #226): a world like `CreateWorld`'s, with the
    // game's systems and its rules started (so it has its player), in `scene` (the start scene when that is
    // empty), but with `document` placed from `record` (the editor's copy in memory) wherever the scene
    // would place it from the record store, or beside the scene when the scene does not name it. Unsaved
    // edits play; the store and the file are not touched. Throw it away with DestroyWorld.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor (phase 10a)
    public World CreatePlayWorld(string name, RecordId scene, RecordId document, PlacementsRecord record)
    {
        if (scene.IsEmpty) scene = Scenes.Start;
        return CreateWorld(name, editing: false, scene, document.IsEmpty ? null : new PlayedDocument(scene, document, record));
    }

    private World CreateWorld(string name, bool editing, RecordId scene, PlayedDocument? played = null)
    {
        // Prefab parts and entity inputs are per engine, and a world's entities are built from them:
        // one added after the first world would exist in some worlds and not others.
        Prefabs.Seal.Seal("the first world was created");
        Inputs.Seal.Seal("the first world was created");
        Outputs.Seal.Seal("the first world was created");
        var world = new World(name, this) { Editing = editing };   // before any module sees it
        _worlds.Add(world);
        Log.Info(LogCat.World, $"World '{name}' created ({_worlds.Count} active){(editing ? ", for editing" : "")}");

        // Cameras are the engine's, like scenes (issue #76): every world resolves its camera entities
        // into CameraViews, whatever plugins it has. Added before any plugin's systems, which is what
        // puts the director ahead of the FrameUpdate systems that read ActiveCamera (CameraDirector).
        Registrations.Owner = RegistrationOwners.Core;
        try
        {
            world.Resources.Add(new CameraViews());
            world.Resources.Add(new Vars());                     // world variables (issue #89), saved
            world.Resources.Add(new WorldClock());               // time of day (issue 4h-2), saved
            world.AddSystem(new WorldClockSystem(world));
            world.AddSystem(new CalendarEventSystem(world));     // calendar events fire their wires (issue 4m-15)
            world.AddSystem(new SkySystem(world));               // lights the world by its `sky`, if it has one
            world.Resources.Add(new TravelLog());                // discovered travel points (issue 4g-5), saved
            world.AddSystem(new TravelPointSystem(world));
            world.AddSystem(new CameraDirector(world));
            world.AddSystem(new ScriptedCameraSystem(world));     // holds run out (issue #80)
            world.AddSystem(new CameraInputLockSystem(world));    // a cut may hold the player still
            world.AddSystem(new CameraBlendSystem(world));        // CameraOn with a blend time (issue #90)
            world.AddSystem(new LogicTimerSystem(world));         // sage:timer and sage:tween (issue #90)
            world.AddSystem(new TweenSystem(world));
            world.AddSystem(new StateMachineSystem(world));      // sage:state_machine (issue #92)
            world.AddSystem(new LogicSequenceSystem(world));     // what a `wait` put off (issue #275)
            world.AddSystem(new QuickSaveKeysSystem(world, this)); // F5 and F9 (issue 4i-6)
            // Skeletal poses and what follows them (issue #120): whoever animates a skeleton registers
            // its pose here; aim IK and bone attachments adjust and follow it in the Late phase, and the
            // registration passes through to SkinPoses, which skinned meshes are drawn from (#117).
            world.Resources.Add(new SkeletonPoses(world.Resources.GetOrAdd(() => new SkinPoses())));
            world.AddSystem(new AimIkSystem(world));
            world.AddSystem(new AttachmentSystem(world));
            world.Resources.Add(Animations);                     // skinned models' skeletons and clips (issue #118)
            world.AddSystem(new AnimatorSystem(world));          // sage:animator (issue #118)
            world.Resources.Add(new RagdollInstances());          // ragdolls' bodies (issue #246), transient
            world.AddSystem(new RagdollSystem(world));           // sage:ragdoll: the pose from the bodies
            world.AddSystem(new RagdollGetUpSystem(world));      // sage:ragdoll_get_up: getUpAfter (issue #247)
            world.AddSystem(new ViewmodelSystem(world));         // a camera's first-person arms (issue #121)
        }
        finally { Registrations.Owner = "host"; }

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

        // The start scene (issue #29), placed before the rules start: they find the world populated,
        // and their SpawnPlayer finds the scene's player start.
        if (played != null) world.Resources.Add(played);   // what the scene places for the editor's document (#226)
        Scenes.Enter(world, scene);

        // Now that every module has had its turn, the game's rules may populate the world (16 §3.1). Not
        // an edit world's: it shows the document, without a player or anything else the rules would add.
        if (!editing) rules.OnWorldStarted(world);
        Signals.RaiseWorldCreated(world);   // furnished, placed and started (#282)
        return world;
    }

    public void DestroyWorld(World world)
    {
        if (!_worlds.Remove(world))
        {
            Assert.Ensure(false, $"DestroyWorld: '{world.Name}' is not one of this engine's worlds");
            return;
        }
        Signals.RaiseWorldDestroying(world);
        world.Dispose();
    }

    public void Dispose()
    {
        for (int i = _worlds.Count - 1; i >= 0; i--)
            _worlds[i].Dispose();
        _worlds.Clear();
    }
}
