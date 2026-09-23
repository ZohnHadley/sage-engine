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
    // Games add the lot with one call, because "gameplay" is the unit a game wants, not six lines.
    public static void AddGameplay(this ModuleManager modules)
    {
        modules.Add(new AttributesModule());
        modules.Add(new CharacterModule());
        modules.Add(new AnimationModule());
        modules.Add(new CombatModule());
        modules.Add(new ItemsModule());
        modules.Add(new AbilitiesModule());
        modules.Add(new AIModule());
    }

    // Every cheat that acts on "the player" means the same thing by it (16 §3.1).
    internal static void ForEachPlayer(Engine engine, Action<World, Entity> act)
    {
        foreach (var world in engine.Worlds)
            foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
                act(world, entity);
    }
}

// Attributes, tags and effects (16 §3.3): the one path by which a number on an entity changes.
// Everything else that wants to change one — damage, a buff, a cheat — goes through here, which is
// why this module has no dependencies and everything else depends on it.
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
        world.Resources.Set(Registries);
        Registries.Rebuild(_records!);
        world.AddSystem(new EffectSystem(world, _records!), Phase.Gameplay);
    }
}

// A body that walks (10 §3, 16 §3.1): the capsule the engine moves, the intent its controller writes,
// and the first-person rig that sits in its head.
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

// Hitting things (16 §3.2): one damage pipeline, `attack` records, and the melee both the player and
// the AI drive by pressing the same button.
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

        ctx.Engine.CVars.RegisterCommand("inv", CVarFlags.None, "What the local player is carrying and wearing.", _ =>
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
            {
                if (!world.TryGet<Inventory>(entity, out var inventory) || inventory.Items == null)
                {
                    Log.Info(LogCat.Console, $"{World.Describe(entity)} carries nothing at all");
                    return;
                }
                world.TryGet<Equipment>(entity, out var equipment);
                Log.Info(LogCat.Console, $"{World.Describe(entity)}: {inventory.Items.Count} stacks, " +
                                         $"{world.WeightOf(entity):F1} kg" +
                                         (inventory.Capacity > 0 ? $" of {inventory.Capacity:F0} kg" : "") +
                                         $", holding {(equipment.MainHand.IsEmpty ? "nothing" : equipment.MainHand.Name)}" +
                                         $"{(equipment.OffHand.IsEmpty ? "" : " and " + equipment.OffHand.Name)}");
                foreach (var stack in inventory.Items)
                    Log.Info(LogCat.Console, $"    {stack.Count,3}x {stack.Item}");
            }));

        ctx.Engine.CVars.RegisterCommand("equip", CVarFlags.Cheat, "equip <item>: wield or wear something the local player carries.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "equip <item>"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
                Log.Info(LogCat.Console, world.Equip(entity, item)
                    ? $"{World.Describe(entity)} equips {item.Name}"
                    : $"{World.Describe(entity)} cannot equip {item.Name}"));
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
        world.Resources.Set(new InteractionState());
        world.AddSystem(new InteractionSystem(world, _records!, _actions!, _interactRange!), Phase.Gameplay,
            before: new[] { typeof(EffectSystem) });
    }
}

// Spells and everything else that is cast (16 §3.3, F21). Almost all of what an ability *does* is
// effects, which Attributes already owns; what lives here is the gating — cost, cooldown, tags — and
// choosing what it lands on.
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

        ctx.Engine.CVars.RegisterCommand("spells", CVarFlags.None, "What the local player can cast, and what is ready.", _ =>
            GameplayModules.ForEachPlayer(ctx.Engine, (world, entity) =>
            {
                if (!world.TryGet<Abilities>(entity, out var abilities) || abilities.Known is not { Count: > 0 })
                {
                    Log.Info(LogCat.Console, $"{World.Describe(entity)} knows no abilities");
                    return;
                }
                foreach (var id in abilities.Known)
                {
                    string cost = ctx.Engine.Records.TryGet(id, out AbilityRecord record) && record.Cost > 0f
                        ? $"{record.Cost:F0} {record.CostAttribute.Name}" : "free";
                    Log.Info(LogCat.Console, $"  {id,-28} {cost}");
                }
            }));
    }

    public void OnWorldCreated(World world) =>
        world.AddSystem(new AbilitySystem(world, _records!, _actions!, _debugCasts!), Phase.Gameplay,
            before: new[] { typeof(EffectSystem) });
}

// Creatures that decide for themselves (16 §3.4): HL1-style schedules of tasks, writing the same
// `PawnIntent` the player's controller writes.
public sealed class AIModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;
    private CVar<bool>? _aiDebug;

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
        world.AddSystem(new AIDebugSystem(world, _records!, _aiDebug!), Phase.Late);   // 16 §11
    }
}
