#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// The gameplay framework, one module per feature (docs/design/01 §3.1, 16, TODO R15).
//
// It used to be a single `GameplayModule`: nine record types, three cvars, seven console commands,
// six input actions and nine systems across six phases, all in one class. Adding anything meant
// editing the record registrations, the commands, the cvars and the system installs — four places,
// none of them near the code they were about (engine review 2026-09-23, item 5).
//
// These are *logical* modules, not assemblies (01 §3.1): a feature owns its records, its cvars, its
// commands, its prefab parts and its systems, and nothing else has to know. The two-level module
// system was designed for exactly this and had not been used for it.
//
// Dependencies are real, not decorative, and they set `OnWorldCreated` order: combat needs attributes
// to exist before it can damage one. What they do *not* control is system order within a phase — that
// is `before:`/`after:`, which names types and works across module boundaries.
public static class GameplayModules
{
    // **The** list of gameplay modules, in `OnWorldCreated` order.
    //
    // It is a list rather than a line of `Add` calls because there were two of them: this one, and the
    // host's own hand-written sequence in `Program.cs`. They agreed until they did not — `LightsModule`
    // was added here and not there, so every test registered the `light` prefab part and the shipped
    // game did not, and point lights silently did nothing in the only build anybody plays. The tests
    // could not catch it because the tests were the ones using the good list.
    //
    // So: one list, both callers walk it, and `ModuleSetTests` fails if a gameplay module is missing
    // from it. Adding a module is adding a line here and nowhere else.
    public static IModule[] All() => new IModule[]
    {
        new FactionsModule(),
        new AttributesModule(),
        new CharacterModule(),
        new AnimationModule(),
        new LightsModule(),
        new CombatModule(),
        new ItemsModule(),
        new AbilitiesModule(),
        new AIModule(),

        // Level logic written in data, and the geometry it moves (04 §3.4, F17). Both are gameplay in
        // the sense that matters here: a headless server runs them, and they decide things.
        new EntityIOModule(),
        new MoverModule(),
    };

    // Games add the lot with one call, because "gameplay" is the unit a game wants, not six lines.
    public static void AddGameplay(this ModuleManager modules)
    {
        foreach (var module in All()) modules.Add(module);
    }

    // Every cheat that acts on "the player" means the same thing by it (16 §3.1).
    //
    // The list is materialised first because these are console commands and console commands do
    // structural things: `drop` destroys an entity and creates a pickup, which inside a live query
    // throws (03 §3.2). It did, the moment saves gave a reason to drop something.
    internal static void ForEachPlayer(Engine engine, Action<World, Entity> act)
    {
        foreach (var world in engine.Worlds)
            foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList())
                act(world, entity);
    }
}

// Attributes, tags and effects (16 §3.3): the one path by which a number on an entity changes.
// Everything else that wants to change one — damage, a buff, a cheat — goes through here, which is
// why this module has no dependencies and everything else depends on it.
[Plugin("sage.gameplay.attributes", "0.1.0")]
public sealed class AttributesModule : IModule
{
    private RecordStore? _records;

    // Attribute and tag ids, shared by every world.
    public GameplayRegistries Registries { get; } = new();

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _records.Register<AttributeRecord>();
        _records.Register<TagRecord>();
        _records.Register<EffectRecord>();
        _records.Reloaded += () => Registries.Rebuild(_records);

        ctx.Engine.Prefabs.Register("attributes", PrefabParts.Attributes);
        ctx.Engine.Prefabs.Register("effects", PrefabParts.Effects);

        // `god`: the player stops taking damage. It is a tag, so effects block themselves with it
        // (16 §3.3) instead of every damage path checking a flag.
        ctx.Engine.CVars.RegisterCommand("god", CVarFlags.Cheat, "Toggle invulnerability for the local player.", _ =>
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
            {
                bool on = !world.HasTag(entity, TagRecord.Invulnerable);
                if (on) world.AddTag(entity, TagRecord.Invulnerable); else world.RemoveTag(entity, TagRecord.Invulnerable);
                Log.Info(LogCat.Console, $"god {(on ? "ON" : "off")} for {World.Describe(entity)}");
            }));
    }

    public void OnWorldCreated(World world)
    {
        // As a world resource, not a module service: everything that reads it has a world in hand.
        world.Resources.Add(Registries);
        Registries.Rebuild(_records!);
        world.AddSystem(new EffectSystem(world, _records!), Phase.Gameplay);
    }
}

// A body that walks (10 §3, 16 §3.1): the capsule the engine moves, the intent its controller writes,
// and the first-person rig that sits in its head.
[Plugin("sage.gameplay.character", "0.1.0")]
public sealed class CharacterModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(PhysicsModule) };   // characters sweep the space

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;
        _records.Register<MovementProfileRecord>();
        ctx.Engine.Prefabs.Register("character", PrefabParts.Character);

        // Gameplay actions (08 §3.2): the simulation defines them, so a headless server has the same
        // ids and a PlayerCommand means the same thing on both sides.
        _actions.Register("Move", ActionKind.Axis2D);
        _actions.Register("Jump", ActionKind.Button);
        _actions.Register("Run", ActionKind.Button);
        _actions.Register("Crouch", ActionKind.Button);
    }

    public void OnWorldCreated(World world)
    {
        // What the Commands phase promises everything downstream: by the end of it, a pawn's intent
        // is what this tick will act on. Both controllers write it there and CharacterMovementSystem
        // consumes it in PrePhysics, so anything writing it later is acting a tick late — which is
        // exactly review #48, found by reading code rather than by the engine saying so (03 §3.5).
        world.Contracts.FinalAfter<PawnIntent>(Phase.Commands);

        world.AddSystem(new PlayerControlSystem(world), Phase.Commands);
        world.AddSystem(new CharacterMovementSystem(world, _records!, _actions!), Phase.PrePhysics,
            before: new[] { typeof(PhysicsSyncSystem) });
        world.AddSystem(new FirstPersonCameraSystem(world, _records!), Phase.FrameUpdate);
    }
}

// Sprite animation is simulation, not rendering (12 §3): it runs at the tick rate, and its frame
// events are what lands a sprite's blow. Its own module because skeletal animation lands here too.
[Plugin("sage.gameplay.animation", "0.1.0")]
public sealed class AnimationModule : IModule
{
    private RecordStore? _records;

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        ctx.Engine.Prefabs.Register("sprite", PrefabParts.Sprite);
    }

    public void OnWorldCreated(World world) =>
        world.AddSystem(new SpriteAnimationSystem(world, _records!), Phase.Animation);
}

// Lamps (06 §3.9, F2). One part and nothing else, which is what a module the size of a feature looks
// like when the feature is small — it lived in `AnimationModule` for an afternoon because that is where
// `sprite` was, and a light is not an animation.
//
// It is here and not in the client because a light is a fact about the world rather than about the
// screen: a headless server has lamps, a map places them by classname like anything else, and only
// `LightExtract` cares that one is ever drawn.
[Plugin("sage.gameplay.lights", "0.1.0")]
public sealed class LightsModule : IModule
{
    public void Init(ModuleContext ctx) => ctx.Engine.Prefabs.Register("light", PrefabParts.Light);
}

// Hitting things (16 §3.2): one damage pipeline, `attack` records, and the melee both the player and
// the AI drive by pressing the same button.
[Plugin("sage.gameplay.combat", "0.1.0")]
public sealed class CombatModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;
    private CVar<bool>? _combatDebug;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(AttributesModule), typeof(CharacterModule) };

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;
        _records.Register<DamageTypeRecord>();
        _records.Register<AttackRecord>();
        ctx.Engine.Prefabs.Register("melee", PrefabParts.Melee);
        _actions.Register("Attack", ActionKind.Button);

        // Registered here, once, rather than in the systems: systems are per world (review #57).
        _combatDebug = ctx.Engine.CVars.Register("combat_debug", false, CVarFlags.DevOnly,
            "Draw every swing: its arc, what it swept and what it found (needs r_debugdraw 1).");

        // `hurt <amount> [type]`: run damage through the whole pipeline (16 §3.2) without needing
        // something to hit you, which is how resistances and the death seam get tested by hand.
        ctx.Engine.CVars.RegisterCommand("hurt", CVarFlags.Cheat, "hurt <amount> [damage type]: damage the local player.", a =>
        {
            float amount = a.Count > 0 && float.TryParse(a[0], out float parsed) ? parsed : 10f;
            var type = a.Count > 1 ? ctx.Engine.Records.Resolve("damage_type", a[1]) : DamageTypeRecord.Physical;
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
            {
                float applied = Combat.ApplyDamage(world, new DamageInfo(default, entity, type, amount,
                    world.Get<Transform>(entity).LocalPosition, Vector3.UnitY));
                Log.Info(LogCat.Console, $"{World.Describe(entity)} takes {applied:F0} {type.Name} damage: " +
                                         $"health {world.Attribute(entity, AttributeRecord.Health):F0}");
            });
        });
    }

    public void OnWorldCreated(World world) =>
        // Combat resolves before effects tick, so a blow struck this tick is felt this tick: the
        // health it costs, the tags it grants and the death it may cause all land together (16 §3.2).
        world.AddSystem(new MeleeCombatSystem(world, _records!, _actions!, _combatDebug!), Phase.Gameplay,
            before: new[] { typeof(EffectSystem) });
}

// Carrying, wielding and picking up (16 §3.2, F19).
[Plugin("sage.gameplay.items", "0.1.0")]
public sealed class ItemsModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;
    private CVar<float>? _interactRange;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(AttributesModule), typeof(CombatModule) };

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;
        _records.Register<ItemRecord>();
        ctx.Engine.Prefabs.Register("inventory", PrefabParts.Inventory);
        ctx.Engine.Prefabs.Register("pickup", PrefabParts.Pickup);
        _actions.Register("Use", ActionKind.Button);

        _interactRange = ctx.Engine.CVars.Register("g_interact_range", 2.5f, CVarFlags.None,
            "How far the Use action reaches, in metres.", 0.5f, 10f);

        // There is no inventory screen yet — 13 is a later phase — so these are how you handle
        // things: `give` puts one in your pack, `equip` puts it in your hand, and the combat log
        // shows the difference the moment you swing.
        ctx.Engine.CVars.RegisterCommand("give", CVarFlags.Cheat, "give <item> [count]: put an item in the local player's inventory.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "give <item> [count]"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            int count = a.Count > 1 && int.TryParse(a[1], out int parsed) ? parsed : 1;
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
            {
                if (world.Give(entity, item, count))
                    Log.Info(LogCat.Console, $"{World.Describe(entity)} receives {count}x {item.Name}");
            });
        });

        // Prints the inventory *panel* (13 §3): the same rows a screen will draw, so the console and
        // the screen cannot disagree about what you are carrying, and a `*` is what is in your hands.
        ctx.Engine.CVars.RegisterCommand("inv", CVarFlags.None, "What the local player is carrying and wearing.", _ =>
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
                GameplayPanels.Inventory(world, entity).Log(LogCat.Console)));

        ctx.Engine.CVars.RegisterCommand("equip", CVarFlags.Cheat, "equip <item>: wield or wear something the local player carries.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "equip <item>"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            if (item.IsEmpty) return;   // Resolve has already said there is no such item
            // Asks before doing (R17), so the refusal says *why* and `Equip` does not log a second
            // copy of it.
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
                Log.Info(LogCat.Console, world.CanEquip(entity, item, out string why) && world.Equip(entity, item)
                    ? $"{World.Describe(entity)} equips {item.Name}"
                    : $"{World.Describe(entity)} cannot equip {item.Name}: {why}"));
        });

        ctx.Engine.CVars.RegisterCommand("unequip", CVarFlags.Cheat, "unequip [main|off]: put away what the local player is holding.", a =>
        {
            var slot = a.Count > 0 && a[0].StartsWith("off", StringComparison.OrdinalIgnoreCase) ? EquipSlot.OffHand : EquipSlot.MainHand;
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) => { world.Unequip(entity, slot); Log.Info(LogCat.Console, $"{World.Describe(entity)} puts away its {slot}"); });
        });

        ctx.Engine.CVars.RegisterCommand("drop", CVarFlags.Cheat, "drop <item> [count]: put an item on the ground in front of the local player.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "drop <item> [count]"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            int count = a.Count > 1 && int.TryParse(a[1], out int parsed) ? parsed : 1;
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
                Log.Info(LogCat.Console, world.Drop(entity, item, count).IsNull
                    ? $"{World.Describe(entity)} has no {item.Name} to drop"
                    : $"{World.Describe(entity)} drops {count}x {item.Name}"));
        });
    }

    public void OnWorldCreated(World world)
    {
        world.Resources.Add(new InteractionState());
        world.AddSystem(new InteractionSystem(world, _records!, _actions!, _interactRange!), Phase.Gameplay,
            before: new[] { typeof(EffectSystem) });
    }
}

// Spells and everything else that is cast (16 §3.3, F21). Almost all of what an ability *does* is
// effects, which Attributes already owns; what lives here is the gating — cost, cooldown, tags — and
// choosing what it lands on.
[Plugin("sage.gameplay.abilities", "0.1.0")]
public sealed class AbilitiesModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;
    private CVar<bool>? _debugCasts;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(AttributesModule), typeof(CharacterModule) };

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;
        _records.Register<AbilityRecord>();
        _records.Register<CueRecord>();
        // The player's own spells are data in the save, and the records are made from it on load
        // (F21's spellmaker, 09 §3.1).
        ctx.Engine.Saves.RegisterResource<Spellbook>();
        Spellmaker.RegisterCommands(ctx.Engine);
        ctx.Engine.Prefabs.Register("abilities", PrefabParts.Abilities);
        _actions.Register("Cast", ActionKind.Button);

        _debugCasts = ctx.Engine.CVars.Register("cast_debug", false, CVarFlags.DevOnly,
            "Draw every cast: where it reached and what it caught (needs r_debugdraw 1).");

        ctx.Engine.CVars.RegisterCommand("cast", CVarFlags.Cheat, "cast <ability>: cast it as the local player.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "cast <ability>"); return; }
            var ability = ctx.Engine.Records.Resolve("ability", a[0]);
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
                Log.Info(LogCat.Console, world.Cast(entity, ability)
                    ? $"{World.Describe(entity)} casts {ability.Name}"
                    : $"{World.Describe(entity)} knows no magic at all"));
        });

        ctx.Engine.CVars.RegisterCommand("learn", CVarFlags.Cheat, "learn <ability>: teach the local player an ability.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "learn <ability>"); return; }
            var ability = ctx.Engine.Records.Resolve("ability", a[0]);
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
            {
                world.Teach(entity, ability);
                Log.Info(LogCat.Console, $"{World.Describe(entity)} learns {ability.Name}");
            });
        });

        // The spellbook panel, printed (13 §3). A `*` is the readied spell and a greyed row says why it
        // cannot be cast right now — the same words a screen will show, from the same rules the cast
        // system applies (R17).
        ctx.Engine.CVars.RegisterCommand("spells", CVarFlags.None, "What the local player can cast, and what is ready.", _ =>
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
                GameplayPanels.Spellbook(world, entity).Log(LogCat.Console)));

        ctx.Engine.CVars.RegisterCommand("ready", CVarFlags.None, "ready <ability>: make it the spell the Cast button fires.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ready <ability>"); return; }
            var ability = ctx.Engine.Records.Resolve("ability", a[0]);
            if (ability.IsEmpty) return;
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
                Log.Info(LogCat.Console, world.Ready(entity, ability)
                    ? $"{World.Describe(entity)} readies {ability.Name}"
                    : $"{World.Describe(entity)} does not know {ability.Name}"));
        });
    }

    public void OnWorldCreated(World world)
    {
        world.AddSystem(new AbilitySystem(world, _records!, _actions!, _debugCasts!), Phase.Gameplay,
            before: new[] { typeof(EffectSystem) });

        // Flight resolves before effects tick too, so a spell that arrives this tick is felt this
        // tick — and after the cast system, so one thrown *this* tick starts moving next (16 §3.2).
        world.AddSystem(new ProjectileSystem(world, _records!, _debugCasts!), Phase.Gameplay,
            after: new[] { typeof(AbilitySystem) }, before: new[] { typeof(EffectSystem) });
    }
}

// Creatures that decide for themselves (16 §3.4): HL1-style schedules of tasks, writing the same
// `PawnIntent` the player's controller writes.
[Plugin("sage.gameplay.ai", "0.1.0")]
public sealed class AIModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;
    private CVar<bool>? _aiDebug;
    private CVar<bool>? _navEnabled;
    private CVar<bool>? _navDebug;
    private CVar<float>? _navCell;
    private CVar<int>? _navNodes;
    private CVar<int>? _navPlans;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(CharacterModule), typeof(CombatModule) };

    // Games add their own tasks to this before the first world is created (16 §3.4).
    public AITaskRegistry AITasks { get; } = new();

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;
        _records.Register<AIProfileRecord>();
        _records.Register<AIScheduleRecord>();

        _aiDebug = ctx.Engine.CVars.Register("ai_debug", false, CVarFlags.DevOnly,
            "Draw what each creature can see and what it is chasing (needs r_debugdraw 1).");

        // Navigation (16 §3.4, F23). `nav_enabled 0` is the A/B: creatures fall back to walking straight
        // at what they are chasing, which is what they did before there was any pathfinding.
        _navEnabled = ctx.Engine.CVars.Register("nav_enabled", true, CVarFlags.Cheat,
            "Let creatures plan a way round obstacles. Off, they walk straight at the target (16 §3.4).");
        _navDebug = ctx.Engine.CVars.Register("nav_debug", false, CVarFlags.DevOnly,
            "Draw the last grid searched and the corners each creature is walking (needs r_debugdraw 1).");
        _navCell = ctx.Engine.CVars.Register("nav_cellsize", 1f, CVarFlags.Cheat,
            "Metres per navigation cell: finer finds narrower gaps and searches more of them.", 0.25f, 4f);
        _navNodes = ctx.Engine.CVars.Register("nav_maxnodes", 4096, CVarFlags.Cheat,
            "Cells one search may look at before giving up, so a sealed room costs a known amount.", 64, 9216);
        _navPlans = ctx.Engine.CVars.Register("nav_plans", 4, CVarFlags.Cheat,
            "Plans allowed per tick across every creature; the rest wait a tick.", 1, 64);

        ctx.Engine.CVars.RegisterCommand("nav_stats", CVarFlags.None,
            "What navigation has been asked for and what it cost.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Navigation>(out var nav) || nav == null) continue;
                Log.Info(LogCat.Console,
                    $"'{world.Name}': {nav.Plans} plans, {nav.Refused} over budget, {nav.NoRoute} with no way " +
                    $"through; last grid {nav.Grid.Width}x{nav.Grid.Height} cells of {nav.Grid.CellSize:F2} m, " +
                    $"{nav.Grid.BlockedCells()} blocked, {nav.Grid.LastNodes} nodes searched");
                nav.ResetStats();
                nav.Grid.ResetStats();
            }
        });
    }

    public void Start(ModuleContext ctx) => ctx.Provide(AITasks);

    public void OnWorldCreated(World world)
    {
        // Both controllers write PawnIntent in the Commands phase, so movement (PrePhysics) acts on
        // it in the same tick it was decided. AI used to sit in Phase.AI, four phases *after* the
        // movement that reads it, which cost every creature a tick of lag (review #48) — and is now
        // a contract the engine checks rather than a comment (03 §3.5).
        world.AddSystem(new AIThinkSystem(world, _records!, AITasks, _actions!), Phase.Commands,
            after: new[] { typeof(PlayerControlSystem) });
        // One navigation per world, like the physics space: the grid holds origin-space positions, and
        // two worlds do not share an origin (R6, and the lesson of the audio mixer in 11 §3).
        world.Resources.Add(new Navigation
        {
            Enabled = _navEnabled!.Value,
            CellSize = _navCell!.Value,
            MaxNodes = _navNodes!.Value,
            PlansPerTick = _navPlans!.Value,
        });
        world.AddSystem(new AIDebugSystem(world, _records!, _aiDebug!), Phase.Late);   // 16 §11
        world.AddSystem(new NavDebugSystem(world, _navEnabled!, _navDebug!, _navCell!, _navNodes!, _navPlans!),
                        Phase.Late);
    }
}

// Keeps navigation's settings in step with the cvars, and draws what the last search saw (16 §3.4, F23).
//
// A system rather than `Changed` handlers because there is one `Navigation` per world and several cvars:
// copying four numbers once a tick is cheaper than four subscriptions per world that have to be undone
// when the world goes.
public sealed class NavDebugSystem : ISystem
{
    private readonly Navigation _nav;
    private readonly DebugDraw _draw;
    private readonly ArchetypeQuery<Transform, AIState> _agents;
    private readonly CVar<bool> _enabled, _debug;
    private readonly CVar<float> _cell;
    private readonly CVar<int> _nodes, _plans;
    private bool _wasEnabled;
    private float _wasCell;
    private int _wasNodes, _wasPlans;

    public NavDebugSystem(World world, CVar<bool> enabled, CVar<bool> debug,
                          CVar<float> cell, CVar<int> nodes, CVar<int> plans)
    {
        _nav = world.Resources.Get<Navigation>();
        _draw = world.Resources.Get<DebugDraw>();
        _agents = world.Query<Transform, AIState>();
        _enabled = enabled;
        _debug = debug;
        _cell = cell;
        _nodes = nodes;
        _plans = plans;
        _wasEnabled = enabled.Value;
        _wasCell = cell.Value;
        _wasNodes = nodes.Value;
        _wasPlans = plans.Value;
    }

    public void Run(in SystemContext ctx)
    {
        // **Only what changed.** Copying all four every tick would silently undo anything that set them
        // in code — a game tuning its own creatures, or a test asking for a budget of two — and the loser
        // of that argument is always the one who cannot see the assignment. A cvar wins when somebody
        // moves it, which is when it is expressing an intent.
        if (_enabled.Value != _wasEnabled) _nav.Enabled = _wasEnabled = _enabled.Value;
        if (_cell.Value != _wasCell) _nav.CellSize = _wasCell = _cell.Value;
        if (_nodes.Value != _wasNodes) _nav.MaxNodes = _wasNodes = _nodes.Value;
        if (_plans.Value != _wasPlans) _nav.PlansPerTick = _wasPlans = _plans.Value;
        if (!_debug.Value) return;

        _nav.DrawLastGrid(_draw);
        foreach (var (transforms, states, _) in _agents.Chunks)
        {
            var t = transforms.Span;
            var s = states.Span;
            for (int n = 0; n < t.Length; n++)
            {
                ref readonly var path = ref s[n].Path;
                if (path.Count == 0) continue;
                Vector3 from = t[n].LocalPosition + Vector3.UnitY * 0.5f;
                for (int i = path.Step; i < path.Count; i++)
                {
                    Vector3 to = path[i] + Vector3.UnitY * 0.5f;
                    _draw.Line(from, to, DebugColour.Green);
                    _draw.Cross(to, 0.2f, DebugColour.Green);
                    from = to;
                }
            }
        }
    }
}

// Who fights whom, and what the world thinks of the player (16 §3.5, F24).
//
// First in the gameplay set, because combat, abilities and AI all ask it questions and a module may
// only depend on one that is already there.
[Plugin("sage.gameplay.factions", "0.1.0")]
public sealed class FactionsModule : IModule
{
    public void Init(ModuleContext ctx)
    {
        ctx.Engine.Records.Register<FactionRecord>();
        ctx.Engine.Records.Register<DialogueRecord>();
        ctx.Engine.Records.Register<QuestRecord>();

        // What a world thinks of you and what you are half way through: both are saved, and both need
        // saying so here — the attribute alone does nothing (09 §3.1).
        ctx.Engine.Saves.RegisterResource<Reputation>();
        ctx.Engine.Saves.RegisterResource<Journal>();

        ctx.Engine.Prefabs.Register("faction", PrefabParts.Faction);
        ctx.Engine.Prefabs.Register("dialogue", PrefabParts.DialoguePart);

        ctx.Engine.CVars.RegisterCommand("rep", CVarFlags.None,
            "What every faction thinks of you, and what that makes them.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Reputation>(out var reputation) || reputation == null) continue;
                foreach (var id in ctx.Engine.Records.Ids("faction"))
                {
                    if (!ctx.Engine.Records.TryGet(id, out FactionRecord record)) continue;
                    float standing = Factions.StandingWith(world, id);
                    string stance = standing <= record.HostileBelow ? "hostile"
                                  : standing >= record.FriendlyAbove ? "friendly" : "neutral";
                    Log.Info(LogCat.Console, $"  {id,-28} {standing,6:F1}  {stance}");
                }
            }
        });

        ctx.Engine.CVars.RegisterCommand("quests", CVarFlags.None,
            "What you are on, and how far.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Journal>(out var journal) || journal == null) continue;
                if (journal.Entries.Count == 0) Log.Info(LogCat.Console, "  (nothing in the journal)");
                foreach (var entry in journal.Entries)
                    Log.Info(LogCat.Console, $"  {entry.Quest,-28} {(entry.Finished ? "done" : entry.Stage)}");
            }
        });

        ctx.Engine.CVars.RegisterCommand("quest_start", CVarFlags.Cheat, "quest_start <quest>: put it in the journal.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "quest_start <quest>"); return; }
            var id = ctx.Engine.Records.Resolve("quest", a[0]);
            if (id.IsEmpty) return;
            foreach (var world in ctx.Engine.Worlds)
                if (world.Resources.TryGet<Journal>(out var journal) && journal != null)
                    Log.Info(LogCat.Console, Quests.Start(world, id) ? $"started {id}" : $"already on {id}");
        });

        ctx.Engine.CVars.RegisterCommand("quest_stage", CVarFlags.Cheat,
            "quest_stage <quest> <stage>: move a quest along by hand.", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "quest_stage <quest> <stage>"); return; }
            var id = ctx.Engine.Records.Resolve("quest", a[0]);
            if (id.IsEmpty) return;
            foreach (var world in ctx.Engine.Worlds)
                if (world.Resources.TryGet<Journal>(out var journal) && journal != null)
                    Log.Info(LogCat.Console, Quests.SetStage(world, id, a[1]) ? $"{id} -> {a[1]}" : $"{id} did not move");
        });

        ctx.Engine.CVars.RegisterCommand("rep_set", CVarFlags.Cheat,
            "rep_set <faction> <value>: set what a faction thinks of you (-100..100).", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "rep_set <faction> <value>"); return; }
            var id = ctx.Engine.Records.Resolve("faction", a[0]);
            if (id.IsEmpty) return;
            if (!float.TryParse(a[1], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float value))
            {
                Log.Warn(LogCat.Console, "rep_set <faction> <value>");
                return;
            }
            foreach (var world in ctx.Engine.Worlds)
                if (world.Resources.TryGet<Reputation>(out var reputation) && reputation != null)
                    Factions.Change(world, id, value - Factions.StandingWith(world, id));
            Log.Info(LogCat.Console, $"{id} now at {value:F1}");
        });
    }

    // A world remembers what it thinks of the player, and a save carries it (09 §3.1). The conversation
    // is not saved: a save taken mid-sentence resumes with the window closed, which is the honest
    // behaviour — what the conversation *did* is already in the world.
    public void OnWorldCreated(World world)
    {
        world.Resources.Add(new Reputation());
        world.Resources.Add(new Conversation());
        world.Resources.Add(new Journal());
    }
}
