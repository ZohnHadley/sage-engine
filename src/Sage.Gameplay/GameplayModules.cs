#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Sage.Physics3D;   // CharacterModule, in the list below and nowhere else (issue #30)

namespace Sage.Gameplay;

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
        new AttributesModule(),
        new FactionsModule(),
        new QuestsModule(),
        new DialogueModule(),
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
        _records.Reloaded += () => Registries.Rebuild(_records);

        // The game's words (issue #26): the gameplay_conventions record is this plugin's, because
        // everything that reads it depends on this one. Its action names must be actions somebody
        // registered, and by the time content loads everybody has.
        var actions = ctx.Engine.Actions;
        _records.AddCheck<GameplayConventionsRecord>((conventions, check) => GameplayConventions.CheckActions(actions, conventions, check));

        // ApplyEffect and OnDeath (issue #91): a level wires to effects and deaths like to a door.
        BridgeIO.RegisterAttributes(ctx.Engine);

        // Saves write attribute values and tags by name, not by the index records loaded in.
        ctx.Engine.Saves.AddConverter((world, records) => new AttributeSetSaveConverter(world, records));
        ctx.Engine.Saves.AddConverter((world, records) => new GameplayTagsSaveConverter(world, records));

        // `god`: the player stops taking damage. It is a tag, so effects block themselves with it
        // (16 §3.3) instead of every damage path checking a flag — the game's own, from its conventions.
        ctx.Engine.CVars.RegisterCommand("god", CVarFlags.Cheat, "Toggle invulnerability for the local player.", _ =>
            ctx.Engine.ForEachPlayer((world, entity) =>
            {
                var invulnerable = world.Conventions().Invulnerable;
                if (invulnerable.IsEmpty)
                {
                    Log.Warn(LogCat.Console, "god: this game's gameplay_conventions name no invulnerable tag");
                    return;
                }
                bool on = !world.HasTag(entity, invulnerable);
                if (on) world.AddTag(entity, invulnerable); else world.RemoveTag(entity, invulnerable);
                Log.Info(LogCat.Console, $"god {(on ? "ON" : "off")} for {World.Describe(entity)}");
            }));
    }

    public void OnWorldCreated(World world)
    {
        // As a world resource, not a module service: everything that reads it has a world in hand.
        world.Resources.Add(Registries);
        Registries.Rebuild(_records!);
        // The character controller reads its default profile and its actions from the conventions
        // too, through its own view of them (it sits below gameplay). Replace, not Add: this runs
        // before CharacterModule, which installs the engine's view only when nobody has.
        world.Resources.Replace<CharacterConventions>(new CharacterConventionsFromRecord(_records!));
        world.AddSystem(new EffectSystem(world, _records!));
        world.AddSystem(new EffectExecutionSystem(world));   // summons and dispels, after the tick (issue #28)
        world.AddSystem(new DeathRulesSystem(world));
        world.AddSystem(new DeathOutputSystem(world));       // OnDeath (issue #91)
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
    }

    public void OnWorldCreated(World world)
    {
        world.AddSystem(new SpriteAnimationSystem(world, _records!));
        world.AddSystem(new SpriteGraphSystem(world, _records!));   // sprites played by a graph (issue #119)
        world.AddSystem(new FootIkSystem(world));   // feet on the ground (issue #120), through IPhysicsWorld
        world.AddSystem(new RagdollTriggerSystem(world));   // deaths and hits send ragdolls down (issue #246)
        world.AddSystem(new FootstepSystem(world, _records!));   // a step makes its surface's noise (issue #270)
        // The animator's clip events become AnimationEvents (issue #119).
        world.Resources.Replace<IAnimationEventSink>(new AnimationEventBus());
        world.Resources.Replace<IAnimDebugSource>(new SpriteAnimDebug(_records!));   // anim_debug lists sprites too
    }
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
    internal const string TurnOn = "TurnOn";
    internal const string TurnOff = "TurnOff";
    internal const string Toggle = "Toggle";
    internal const string SetPattern = "SetPattern";

    // LightPart is declared ([PrefabPart], issue #17). The switch is entity I/O (issue 4h-7), routed to
    // lights so a branch's or a door's `Toggle` is untouched: a lamp lit at night is a state machine whose
    // `time_between` transition fires TurnOn at it, all in data (the Sandbox's `sandbox:lamp`).
    public void Init(ModuleContext ctx)
    {
        var inputs = ctx.Engine.Inputs;
        inputs.Register<PointLight>(TurnOn, static (World world, in IOContext io) => world.Get<PointLight>(io.Self).Off = false);
        inputs.Register<PointLight>(TurnOff, static (World world, in IOContext io) => world.Get<PointLight>(io.Self).Off = true);
        inputs.Register<PointLight>(Toggle, static (World world, in IOContext io) =>
        {
            ref var light = ref world.Get<PointLight>(io.Self);
            light.Off = !light.Off;
        });
        // A flicker (issue #314): the parameter is a pattern or a preset (LightStyles), empty for steady.
        // One that is neither is refused with a warning, and the light keeps what it had.
        inputs.Register<PointLight>(SetPattern, static (World world, in IOContext io) =>
        {
            string pattern = io.Parameter ?? "";
            if (!LightStyles.TryResolve(pattern, out _))
            {
                Log.Warn(LogCat.Gameplay, $"{World.Describe(io.Self)}: SetPattern \"{pattern}\" is neither letters a..z nor a preset ({string.Join(", ", LightStyles.PresetNames)})");
                return;
            }
            world.Get<PointLight>(io.Self).Pattern = pattern.Length == 0 ? null : pattern;
        });
    }
}

// Hitting things (16 §3.2): one damage pipeline, `attack` records, and the melee both the player and
// the AI drive by pressing the same button.
[Plugin("sage.gameplay.combat", "0.1.0")]
[RequiresPlugin("sage.gameplay.character")]   // by id: a character is whichever backend's (issue #30)
public sealed class CombatModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;
    private CVar<bool>? _combatDebug;
    private ModuleManager? _modules;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(AttributesModule) };

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _modules = ctx.Engine.Modules;
        _actions = ctx.Engine.Actions;
        _actions.Register("Attack", ActionKind.Button);   // the engine's name; gameplay_conventions picks which one swings
        _actions.Register("Reload", ActionKind.Button);   // plays the first-person arms' reload (issue #121)
        _actions.Register("Block", ActionKind.Button);    // holds a fighter's guard up (issue #359)
        BridgeIO.RegisterCombat(ctx.Engine);              // OnDamaged (issue #91)
        // An attack's `delivery` names a registered hit_delivery (issue #133), checked when content loads.
        var vocabularies = ctx.Engine.Vocabularies;
        _records.AddCheck<AttackRecord>((attack, check) => HitDeliveries.Check(vocabularies, attack, check));
        _records.AddCheck<HitboxesRecord>(Hitboxes.Check);   // hit locations (issue #137)

        // Registered here, once, rather than in the systems: systems are per world (review #57).
        _combatDebug = ctx.Engine.CVars.Register("combat_debug", false, CVarFlags.DevOnly,
            "Draw every swing: its arc, what it swept and what it found (needs r_debugdraw 1).");

        // `hurt <amount> [type]`: run damage through the whole pipeline (16 §3.2) without needing
        // something to hit you, which is how resistances and the death seam get tested by hand.
        ctx.Engine.CVars.RegisterCommand("hurt", CVarFlags.Cheat, "hurt <amount> [damage type]: damage the local player.", a =>
        {
            float amount = a.Count > 0 && float.TryParse(a[0], out float parsed) ? parsed : 10f;
            // No type: the game's default (Combat.ApplyDamage reads it from the conventions).
            var type = a.Count > 1 ? ctx.Engine.Records.Resolve("damage_type", a[1]) : default;
            ctx.Engine.ForEachPlayer((world, entity) =>
            {
                float applied = Combat.ApplyDamage(world, new DamageInfo(default, entity, type, amount,
                    world.Get<Transform>(entity).LocalPosition, Vector3.UnitY));
                var conventions = world.Conventions();
                var named = type.IsEmpty ? conventions.DamageType.Id : type;
                Log.Info(LogCat.Console, $"{World.Describe(entity)} takes {applied:F0} {named.Name} damage: " +
                                         $"{conventions.Health.Name} {world.Attribute(entity, conventions.Health):F0}");
            });
        });
    }

    public void OnWorldCreated(World world)
    {
        // Combat resolves before effects tick, so a blow struck this tick is felt this tick: the
        // health it costs, the tags it grants and the death it may cause all land together (16 §3.2).
        world.AddSystem(new ReloadSystem(world, _records!, _actions!));   // ammunition (issue #135)
        world.AddSystem(new RecoilSystem(world));   // spread and recoil (issue #136)
        world.AddSystem(new AttackStanceSystem(world, _actions!));   // directional swings and guards (issue #359)
        world.AddSystem(new MeleeCombatSystem(world, _records!, _actions!, _combatDebug!));
        world.AddSystem(new DamageOutputSystem(world));     // OnDamaged (issue #91)
        world.AddSystem(new HitboxCleanupSystem(world));    // a hitbox goes with its owner (issue #137)
        world.AddSystem(new HitboxBudgetSystem(world, _records!));   // far creatures' hitboxes off (issue #273)
        world.AddSystem(new RumbleSystem(world, _records!));   // pad rumble from cues and hits, in a world with a mixer (issue #331)
        world.AddSystem(new DecalSystem(world, _records!));   // marks that stay, in a world with a Decals pool (issue #306)
        // The first-person arms follow the attack in hand and hear the swing and Reload (issue #121);
        // their clip events are the animator's, like everyone's (issue #119).
        world.AddSystem(new ViewmodelCombatSystem(world, _records!, _actions!));
        // A `projectile` attack flies on the carrier abilities throw (issue #134). The abilities plugin
        // adds its system; a game with combat and no magic (issue #138) gets it here, or its bolts would
        // hang in the air where they were fired.
        if (!_modules!.Modules.OfType<AbilitiesModule>().Any())
            world.AddSystem(new ProjectileSystem(world, _records!, _combatDebug!));
    }
}

// Carrying, wielding and picking up (16 §3.2, F19).
[Plugin("sage.gameplay.items", "0.1.0")]
public sealed class ItemsModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;
    private CVar<float>? _interactRange;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(AttributesModule), typeof(CombatModule) };

    // Where things are worn (issue #27): none in the base. A kit or a game registers its own in Init
    // (the RPG kit's two hands), and items are checked against them as content loads.
    public EquipSlots Slots { get; } = new();

    public void Init(ModuleContext ctx)
    {
        BridgeIO.RegisterItems(ctx.Engine);         // GiveItem, OnPickedUp (issue #91)
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;
        _actions.Register("Use", ActionKind.Button);
        ctx.Provide(Slots);
        _records.AddCheck<ItemRecord>((item, check) =>
        {
            if (item.Slot.Length == 0 || Slots.Find(item.Slot) != null) return;
            check.Error(nameof(ItemRecord.Slot), $"no equipment slot '{item.Slot}'" + (Slots.Names.Count == 0
                ? " (this game registers none: a kit or the game's module registers them in Init, EquipSlots.Register)"
                : Spelling.Suggest(item.Slot, Slots.Names)));
        });
        ctx.Engine.Outputs.Declare("OnUse", "Something used this entity (the Use action).");

        _interactRange = ctx.Engine.CVars.Register("g_interact_range", 2.5f, CVarFlags.None,
            "How far the Use action reaches, in metres.", 0.5f, 10f);
        ItemUses.RegisterCommands(ctx.Engine);   // use_item (issue #28)

        // The console's way to handle things: `give` puts one in your pack, `equip` puts it in your
        // hand, and the combat log shows the difference the moment you swing. A bag screen, and the
        // `inv` command that prints its panel, are a kit's (Sage.Kits.Rpg, issue #27).
        ctx.Engine.CVars.RegisterCommand("give", CVarFlags.Cheat, "give <item> [count]: put an item in the local player's inventory.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "give <item> [count]"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            int count = a.Count > 1 && int.TryParse(a[1], out int parsed) ? parsed : 1;
            ctx.Engine.ForEachPlayer((world, entity) =>
            {
                if (world.Give(entity, item, count))
                    Log.Info(LogCat.Console, $"{World.Describe(entity)} receives {count}x {item.Name}");
            });
        });

        ctx.Engine.CVars.RegisterCommand("equip", CVarFlags.Cheat, "equip <item>: wield or wear something the local player carries.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "equip <item>"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            if (item.IsEmpty) return;   // Resolve has already said there is no such item
            // Asks before doing (R17), so the refusal says *why* and `Equip` does not log a second
            // copy of it.
            ctx.Engine.ForEachPlayer((world, entity) =>
                Log.Info(LogCat.Console, world.CanEquip(entity, item, out string why) && world.Equip(entity, item)
                    ? $"{World.Describe(entity)} equips {item.Name}"
                    : $"{World.Describe(entity)} cannot equip {item.Name}: {why}"));
        });

        ctx.Engine.CVars.RegisterCommand("unequip", CVarFlags.Cheat, "unequip [slot]: put away what the local player wears in a slot, or everything.", a =>
        {
            string? slot = a.Count > 0 ? Slots.Find(a[0]) : null;
            if (a.Count > 0 && slot == null)
            {
                Log.Warn(LogCat.Console, $"unequip: no equipment slot '{a[0]}' (slots: {string.Join(", ", Slots.Names)})");
                return;
            }
            ctx.Engine.ForEachPlayer((world, entity) =>
            {
                foreach (string each in slot != null ? new[] { slot } : Slots.Names.ToArray()) world.Unequip(entity, each);
                Log.Info(LogCat.Console, $"{World.Describe(entity)} puts away {(slot != null ? $"its {slot}" : "everything")}");
            });
        });

        ctx.Engine.CVars.RegisterCommand("drop", CVarFlags.Cheat, "drop <item> [count]: put an item on the ground in front of the local player.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "drop <item> [count]"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            int count = a.Count > 1 && int.TryParse(a[1], out int parsed) ? parsed : 1;
            ctx.Engine.ForEachPlayer((world, entity) =>
                Log.Info(LogCat.Console, world.Drop(entity, item, count).IsNull
                    ? $"{World.Describe(entity)} has no {item.Name} to drop"
                    : $"{World.Describe(entity)} drops {count}x {item.Name}"));
        });
    }

    // Items were checked against the slots as content loaded: one registered now would have missed that.
    public void Start(ModuleContext ctx) => Slots.Seal.Seal("content was loaded");

    public void OnWorldCreated(World world)
    {
        world.Resources.Add(new InteractionState());
        world.AddSystem(new InteractionSystem(world, _records!, _actions!, _interactRange!));
        world.AddSystem(new ContainerSystem(world));   // chests and bodies (issue #378)
    }
}

// Spells and everything else that is cast (16 §3.3, F21). Almost all of what an ability *does* is
// effects, which Attributes already owns; what lives here is the gating — cost, cooldown, tags — and
// choosing what it lands on.
[Plugin("sage.gameplay.abilities", "0.1.0")]
[RequiresPlugin("sage.gameplay.character")]
public sealed class AbilitiesModule : IModule
{
    private RecordStore? _records;
    private CVar<bool>? _debugCasts;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(AttributesModule) };

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        // Ability *use* is the base's: `world.Cast` from anything. A readied spell that a Cast button
        // fires, the spellbook and the spellmaker are the RPG kit's (Sage.Kits.Rpg, issue #27).
        // An ability's `delivery` names a registered one (issue #28), checked when content loads.
        var vocabularies = ctx.Engine.Vocabularies;
        _records.AddCheck<AbilityRecord>((ability, check) => AbilityDeliveries.Check(vocabularies, ability, check));

        _debugCasts = ctx.Engine.CVars.Register("cast_debug", false, CVarFlags.DevOnly,
            "Draw every cast: where it reached and what it caught (needs r_debugdraw 1).");

        ctx.Engine.CVars.RegisterCommand("cast", CVarFlags.Cheat, "cast <ability>: cast it as the local player.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "cast <ability>"); return; }
            var ability = ctx.Engine.Records.Resolve("ability", a[0]);
            ctx.Engine.ForEachPlayer((world, entity) =>
                Log.Info(LogCat.Console, world.Cast(entity, ability)
                    ? $"{World.Describe(entity)} casts {ability.Name}"
                    : $"{World.Describe(entity)} knows no magic at all"));
        });

        ctx.Engine.CVars.RegisterCommand("learn", CVarFlags.Cheat, "learn <ability>: teach the local player an ability.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "learn <ability>"); return; }
            var ability = ctx.Engine.Records.Resolve("ability", a[0]);
            ctx.Engine.ForEachPlayer((world, entity) =>
            {
                world.Teach(entity, ability);
                Log.Info(LogCat.Console, $"{World.Describe(entity)} learns {ability.Name}");
            });
        });
    }

    public void OnWorldCreated(World world)
    {
        world.AddSystem(new AbilitySystem(world, _records!, _debugCasts!));

        // Flight resolves before effects tick too, so a spell that arrives this tick is felt this
        // tick — and after the cast system, so one thrown *this* tick starts moving next (16 §3.2).
        world.AddSystem(new ProjectileSystem(world, _records!, _debugCasts!));
    }
}

// Creatures that decide for themselves (16 §3.4): HL1-style schedules of tasks, writing the same
// `PawnIntent` the player's controller writes.
[Plugin("sage.gameplay.ai", "0.1.0")]
[RequiresPlugin("sage.gameplay.character")]
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
    private CVar<bool>? _navMesh;
    private CVar<bool>? _navAvoid;
    private CVar<int>? _offscreenBudget;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(CombatModule) };

    // Games add their own tasks to this before the first world is created (16 §3.4).
    public AITaskRegistry AITasks { get; } = new();

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;
        // Condition and selector names are vocabularies (issue #28), sealed when content loads, so a
        // schedule's interrupts and a profile's selector and rules are checked then (issue #22); task
        // names are open until the first world, so they are checked then.
        var vocabularies = ctx.Engine.Vocabularies;
        _records.AddCheck<AIScheduleRecord>((schedule, check) => AIChecks.Schedule(vocabularies, schedule, check));
        _records.AddCheck<AIProfileRecord>((profile, check) => AIChecks.Profile(vocabularies, profile, check));
        var records = _records;
        _records.AddCheck<RoutineRecord>((routine, check) => RoutineChecks.Check(records, routine, check));
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
        // The navmesh (#264): baked from brush floors, terrain and static colliders as creatures need it.
        // Off, every plan is the local grid's, which does not see brushes.
        _navMesh = ctx.Engine.CVars.Register("nav_mesh", true, CVarFlags.Cheat,
            "Plan on the navmesh baked from brushes, terrain and static colliders; off, only the local grid (#264).");
        // Crowds (#271): creatures steer round each other. Off, they walk through each other as they did.
        _navAvoid = ctx.Engine.CVars.Register("nav_avoid", true, CVarFlags.Cheat,
            "Let creatures steer round each other (sampled velocity obstacles, #271); off, they walk through each other.");
        var navEngine = ctx.Engine;
        ctx.Engine.CVars.RegisterCommand("nav_rebuild", CVarFlags.None,
            "Drop every baked navmesh tile; they are baked again from what is there now, as creatures need them.", _ =>
        {
            foreach (var world in navEngine.Worlds)
                if (world.Resources.TryGet<Navigation>(out var nav) && nav != null)
                {
                    int tiles = nav.Mesh.TileCount;
                    nav.Rebuild();
                    Log.Info(LogCat.Console, $"'{world.Name}': {tiles} navmesh tile(s) dropped");
                }
        });
        // An off-mesh link switched from a map or a script (#265): a bridge raised, a ladder kicked away.
        // Routed to the link, like a relay's Enable.
        ctx.Engine.Inputs.Register<NavLink>("Enable", static (World world, in IOContext io) => world.Get<NavLink>(io.Self).Disabled = false);
        ctx.Engine.Inputs.Register<NavLink>("Disable", static (World world, in IOContext io) => world.Get<NavLink>(io.Self).Disabled = true);
        // A level's markers a body cannot get to (#264), found by baking its brushes at load: `sage
        // validate` says so at the marker's line.
        var navRecords = _records;
        _records.AddCheck<MapRecord>((map, check) => NavMeshChecks.Map(navRecords, ctx.Engine, map, check));

        // Off-screen simulation (issue 4g-6): how much of it a tick may do while catching up with the clock.
        _offscreenBudget = ctx.Engine.CVars.Register("offscreen_budget", OffscreenSystem.DefaultBudget, CVarFlags.Archive,
            "Off-screen agent steps (an agent, a game minute) a tick may run catching up with the clock; a skip of time runs them all.", 100, 1000000);
        var engine = ctx.Engine;
        _records.Reloaded += () =>
        {
            foreach (var world in engine.Worlds)
            {
                if (world.Resources.TryGet<OffscreenMap>(out var map) && map != null) map.Invalidate();
                // The nav_area records may have changed, and every tile baked from them (#271).
                if (world.Resources.TryGet<Navigation>(out var nav) && nav != null) nav.ReloadAreas();
            }
        };
        ctx.Engine.CVars.RegisterCommand("offscreen_status", CVarFlags.None,
            "The off-screen agents of each world: how many, how many dead, and the game minute simulated to.", _ =>
        {
            foreach (var world in engine.Worlds)
            {
                if (!world.Resources.TryGet<OffscreenAgents>(out var table) || table == null) continue;
                int dead = 0;
                foreach (var agent in table.Agents) if (agent.Dead) dead++;
                Log.Info(LogCat.Console, $"'{world.Name}': {table.Agents.Count} off-screen agent(s), {dead} dead, at minute {table.Minute}");
                foreach (var agent in table.Agents) Log.Info(LogCat.Console, $"  {agent}{(agent.Dead ? " (dead)" : "")}");
            }
        });

        ctx.Engine.CVars.RegisterCommand("nav_stats", CVarFlags.None,
            "What navigation has been asked for and what it cost.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Navigation>(out var nav) || nav == null) continue;
                Log.Info(LogCat.Console,
                    $"'{world.Name}': {nav.Plans} plans ({nav.MeshPlans} on the navmesh, {nav.GridPlans} on the grid), " +
                    $"{nav.Refused} over budget, {nav.NoRoute} with no way through; {nav.Crowd.Steered} steer(s) round {nav.Crowd.Count} character(s); " +
                    $"{nav.Areas.Count} nav_area(s); navmesh {nav.Mesh.TileCount} tile(s), " +
                    $"{nav.Mesh.Baked} baked, {nav.Mesh.LastNodes} nodes last search; last grid {nav.Grid.Width}x{nav.Grid.Height} " +
                    $"cells of {nav.Grid.CellSize:F2} m, {nav.Grid.BlockedCells()} blocked, {nav.Grid.LastNodes} nodes searched");
                nav.ResetStats();
                nav.Grid.ResetStats();
            }
        });
    }

    public void Start(ModuleContext ctx) => ctx.Provide(AITasks);

    // Every schedule's tasks against the tasks there are, once, when the first world has them all:
    // an unknown name or a misnamed argument is reported at its line, before any creature runs it.
    private bool _tasksChecked;

    private void CheckTasks()
    {
        if (_tasksChecked || _records == null) return;
        _tasksChecked = true;
        foreach (var id in _records.Ids("ai_schedule"))
        {
            if (!_records.TryGet(id, out AIScheduleRecord schedule)) continue;
            for (int i = 0; i < schedule.Tasks.Count; i++)
            {
                var step = schedule.Tasks[i];
                string where = $"{_records.Where("ai_schedule", id, $"Tasks[{i}]")}: ai_schedule {id}";
                if (AITasks.Find(step.Task) is not { } task)
                    Log.Error(LogCat.AI, $"{where}: no AI task named '{step.Task}'" + Spelling.Suggest(step.Task, AITasks.Names));
                else if (step.Argument != null && task.Argument != null && !string.Equals(step.Argument, task.Argument, StringComparison.OrdinalIgnoreCase))
                    Log.Error(LogCat.AI, $"{where}: task '{step.Task}' takes '{task.Argument}', not '{step.Argument}'");
            }
        }
    }

    public void OnWorldCreated(World world)
    {
        CheckTasks();
        // Both controllers write PawnIntent in the Commands phase, so movement (PrePhysics) acts on
        // it in the same tick it was decided. AI used to sit in Phase.AI, four phases *after* the
        // movement that reads it, which cost every creature a tick of lag (review #48) — and is now
        // a contract the engine checks rather than a comment (03 §3.5).
        world.AddSystem(new AIThinkSystem(world, _records!, AITasks, _actions!));
        // One navigation per world, like the physics space: the grid holds origin-space positions, and
        // two worlds do not share an origin (R6, and the lesson of the audio mixer in 11 §3).
        world.Resources.Add(new Navigation
        {
            Enabled = _navEnabled!.Value,
            CellSize = _navCell!.Value,
            MaxNodes = _navNodes!.Value,
            PlansPerTick = _navPlans!.Value,
            UseMesh = _navMesh!.Value,
            Avoidance = _navAvoid!.Value,
        });
        world.AddSystem(new AIDebugSystem(world, _records!, _aiDebug!));   // 16 §11
        // Off-screen simulation (issue 4g-6): the saved table of agents, and what steps it.
        world.Resources.Add(new OffscreenAgents());
        world.AddSystem(new OffscreenSystem(world, _records!, _offscreenBudget));
        world.AddSystem(new NavDebugSystem(world, _navEnabled!, _navDebug!, _navCell!, _navNodes!, _navPlans!, _navMesh!));
    }
}

// Keeps navigation's settings in step with the cvars, and draws what the last search saw (16 §3.4, F23).
//
// A system rather than `Changed` handlers because there is one `Navigation` per world and several cvars:
// copying five values once a tick is cheaper than five subscriptions per world that have to be undone
// when the world goes.
[System("sage.ai.nav_debug", Phase.Late)]
internal sealed class NavDebugSystem : ISystem
{
    private readonly Navigation _nav;
    private readonly DebugDraw _draw;
    private readonly Query<Transform, AIState> _agents;
    private readonly CVar<bool> _enabled, _debug, _mesh;
    private readonly CVar<float> _cell;
    private readonly CVar<int> _nodes, _plans;
    private bool _wasEnabled, _wasMesh;
    private float _wasCell;
    private int _wasNodes, _wasPlans;

    public NavDebugSystem(World world, CVar<bool> enabled, CVar<bool> debug,
                          CVar<float> cell, CVar<int> nodes, CVar<int> plans, CVar<bool> mesh)
    {
        _mesh = mesh;
        _wasMesh = mesh.Value;
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
        // **Only what changed.** Copying all five every tick would silently undo anything that set them
        // in code — a game tuning its own creatures, or a test asking for a budget of two — and the loser
        // of that argument is always the one who cannot see the assignment. A cvar wins when somebody
        // moves it, which is when it is expressing an intent.
        if (_enabled.Value != _wasEnabled) _nav.Enabled = _wasEnabled = _enabled.Value;
        if (_cell.Value != _wasCell) _nav.CellSize = _wasCell = _cell.Value;
        if (_nodes.Value != _wasNodes) _nav.MaxNodes = _wasNodes = _nodes.Value;
        if (_plans.Value != _wasPlans) _nav.PlansPerTick = _wasPlans = _plans.Value;
        if (_mesh.Value != _wasMesh) _nav.UseMesh = _wasMesh = _mesh.Value;
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
// Optional: nothing calls into it. Combat, abilities and AI ask `Factions` who is an enemy, which
// without this plugin is the engine's pre-faction answer (a creature is hostile to the player), and a
// death reaches it as a `Died` event (issue #26). Dialogue and quests are their own plugins
// (QuestsModule, DialogueModule). It depends on attributes for the conventions record, which names
// the player's faction.
[Plugin("sage.gameplay.factions", "0.1.0")]
public sealed class FactionsModule : IModule
{
    public IReadOnlyList<Type> Dependencies => new[] { typeof(AttributesModule) };

    public void Init(ModuleContext ctx)
    {
        // The faction record, the saved Reputation and the `faction` prefab part are this plugin's by
        // their attributes (Plugin = "sage.gameplay.factions"); generated code registers them (#16, #17).
        BridgeIO.RegisterFactions(ctx.Engine);      // SetFaction (issue #91)

        ctx.Engine.CVars.RegisterCommand("rep", CVarFlags.None,
            "What every faction thinks of you, and what that makes them.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Reputation>(out var reputation) || reputation == null) continue;
                var player = world.Conventions().PlayerFaction;
                foreach (var id in ctx.Engine.Records.Ids("faction"))
                {
                    if (id == player || !ctx.Engine.Records.TryGet(id, out FactionRecord record)) continue;
                    float standing = Factions.StandingWith(world, id);
                    string stance = standing <= record.HostileBelow ? "hostile"
                                  : standing >= record.FriendlyAbove ? "friendly" : "neutral";
                    Log.Info(LogCat.Console, $"  {id,-28} {standing,6:F1}  {stance}");
                }
            }
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

    // A world remembers what it thinks of the player, and a save carries it (09 §3.1).
    public void OnWorldCreated(World world)
    {
        world.Resources.Add(new Reputation());
        world.AddSystem(new FactionDeathSystem(world));
    }
}
