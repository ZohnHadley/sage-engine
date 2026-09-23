#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Controllers and pawns (docs/design/16 §3.1). A player and an AI drive a body the same way: they
// write PawnIntent, and movement, combat and interaction only ever read that. It is the seed of the
// gameplay framework; GameRules, possession and AI controllers come with 16/F19.

// Marks a body a controller can drive.
public struct Pawn : IComponent { }

// Tag: this pawn is driven by the local player's PlayerCommand (08).
public struct PlayerControlled : ITag { }

// What a controller wants the pawn to do this tick: written by controllers in the Commands phase,
// read by movement, combat and interaction later in the same tick. Written by controllers only.
public struct PawnIntent : IComponent
{
    public Vector2 Move;        // x = right, y = forward; length <= 1
    public float Yaw, Pitch;    // radians, absolute view angles
    public ActionMask Held;
    public ActionMask Pressed;
}

// Commands phase: the local player's command becomes intent (16 §3.1, 08 §3.4). AI controllers write
// the same component from their own state, so everything downstream is shared.
public sealed class PlayerControlSystem : ISystem
{
    private readonly ArchetypeQuery<PawnIntent> _pawns;

    public PlayerControlSystem(World world)
    {
        _pawns = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        var input = ctx.World.Resources.Get<PlayerInput>();
        if (!input.HasCommand) return;
        ref readonly var command = ref input.Command;

        foreach (var (intents, _) in _pawns.Chunks)
        {
            var intent = intents.Span;
            for (int n = 0; n < intent.Length; n++)
            {
                intent[n].Move = command.Move;
                intent[n].Yaw = command.ViewYaw;
                intent[n].Pitch = command.ViewPitch;
                intent[n].Held = command.Held;
                intent[n].Pressed = command.Pressed;
            }
        }
    }
}

// The seed of the gameplay framework module (docs/design/16 §3.2): today it registers the movement
// profile records and installs the character systems in every world. GameRules, possession, AI
// controllers, abilities and combat join it as 16/F19 is built, and it moves to Sage.Framework then.
public sealed class GameplayModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;
    private CVar<bool>? _aiDebug;
    private CVar<bool>? _combatDebug;
    private CVar<float>? _interactRange;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(PhysicsModule) };   // characters sweep the space

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;

        // Registered here, once, rather than in the systems: systems are per world (review #57).
        _aiDebug = ctx.Engine.CVars.Register("ai_debug", false, CVarFlags.DevOnly,
            "Draw each agent's sight cone, target and state (needs r_debugdraw 1).");
        _combatDebug = ctx.Engine.CVars.Register("combat_debug", false, CVarFlags.DevOnly,
            "Draw every swing: where it reached and what it found (needs r_debugdraw 1).");
        _interactRange = ctx.Engine.CVars.Register("g_interact_range", 2.5f, CVarFlags.None,
            "How far the Use action reaches, in metres (16 §3.2).", 0.5f, 10f);
        _records.Register<MovementProfileRecord>();
        _records.Register<AIProfileRecord>();
        _records.Register<AIScheduleRecord>();
        _records.Register<AttributeRecord>();
        _records.Register<TagRecord>();
        _records.Register<EffectRecord>();
        _records.Register<DamageTypeRecord>();
        _records.Register<AttackRecord>();
        _records.Register<ItemRecord>();

        // How a prefab says "a character", "a thing that fights", "a thing on the ground" (F31).
        // Each is a setup several components have to agree about, so it is a part rather than data;
        // Collider, RigidBody, AIState and SpriteRenderer stay plain components. Registration order
        // is apply order, and character comes first because the rest hang off its body. When
        // GameplayModule splits (R15) each part moves with the feature that owns it.
        var prefabs = ctx.Engine.Prefabs;
        prefabs.Register("body", PrefabParts.Body);
        prefabs.Register("sprite", PrefabParts.Sprite);
        prefabs.Register("character", PrefabParts.Character);
        prefabs.Register("attributes", PrefabParts.Attributes);
        prefabs.Register("melee", PrefabParts.Melee);
        prefabs.Register("inventory", PrefabParts.Inventory);
        prefabs.Register("pickup", PrefabParts.Pickup);
        prefabs.Register("effects", PrefabParts.Effects);
        _records.Reloaded += () => Registries.Rebuild(_records);

        // `god`: the player stops taking damage. It is a tag, so effects block themselves with it
        // (16 §3.3) instead of every damage path checking a flag.
        ctx.Engine.CVars.RegisterCommand("god", CVarFlags.Cheat, "Toggle invulnerability for the local player.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
                foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
                {
                    bool on = !world.HasTag(entity, TagRecord.Invulnerable);
                    if (on) world.AddTag(entity, TagRecord.Invulnerable); else world.RemoveTag(entity, TagRecord.Invulnerable);
                    Log.Info(LogCat.Console, $"god {(on ? "ON" : "off")} for {World.Describe(entity)}");
                }
        });

        // `hurt <amount> [type]`: run damage through the whole pipeline (16 §3.2) without needing
        // something to hit you, which is how resistances and the death seam get tested by hand.
        ctx.Engine.CVars.RegisterCommand("hurt", CVarFlags.Cheat, "hurt <amount> [damage type]: damage the local player.", a =>
        {
            float amount = a.Count > 0 && float.TryParse(a[0], out float parsed) ? parsed : 10f;
            var type = a.Count > 1 ? ctx.Engine.Records.Resolve("damage_type", a[1]) : DamageTypeRecord.Physical;
            foreach (var world in ctx.Engine.Worlds)
                foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
                {
                    float applied = Combat.ApplyDamage(world, new DamageInfo(default, entity, type, amount,
                        world.Get<Transform>(entity).LocalPosition, Vector3.UnitY));
                    Log.Info(LogCat.Console, $"{World.Describe(entity)} takes {applied:F0} {type.Name} damage: " +
                                             $"health {world.Attribute(entity, AttributeRecord.Health):F0}");
                }
        });

        // Items (16 §3.2, F19). There is no inventory screen yet — 13 is a later phase — so these are
        // how you handle things: `give` puts one in your pack, `equip` puts it in your hand, and the
        // combat log shows the difference the moment you swing.
        ctx.Engine.CVars.RegisterCommand("give", CVarFlags.Cheat, "give <item> [count]: put an item in the local player's inventory.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "give <item> [count]"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            int count = a.Count > 1 && int.TryParse(a[1], out int parsed) ? parsed : 1;
            ForEachPlayer(ctx.Engine, (world, entity) =>
            {
                if (world.Give(entity, item, count))
                    Log.Info(LogCat.Console, $"{World.Describe(entity)} receives {count}x {item.Name}");
            });
        });

        ctx.Engine.CVars.RegisterCommand("inv", CVarFlags.None, "What the local player is carrying and wearing.", _ =>
            ForEachPlayer(ctx.Engine, (world, entity) =>
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
            ForEachPlayer(ctx.Engine, (world, entity) =>
                Log.Info(LogCat.Console, world.Equip(entity, item)
                    ? $"{World.Describe(entity)} equips {item.Name}"
                    : $"{World.Describe(entity)} cannot equip {item.Name}"));
        });

        ctx.Engine.CVars.RegisterCommand("unequip", CVarFlags.Cheat, "unequip [main|off]: put away what the local player is holding.", a =>
        {
            var slot = a.Count > 0 && a[0].StartsWith("off", StringComparison.OrdinalIgnoreCase) ? EquipSlot.OffHand : EquipSlot.MainHand;
            ForEachPlayer(ctx.Engine, (world, entity) => { world.Unequip(entity, slot); Log.Info(LogCat.Console, $"{World.Describe(entity)} puts away its {slot}"); });
        });

        ctx.Engine.CVars.RegisterCommand("drop", CVarFlags.Cheat, "drop <item> [count]: put an item on the ground in front of the local player.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "drop <item> [count]"); return; }
            var item = ctx.Engine.Records.Resolve("item", a[0]);
            int count = a.Count > 1 && int.TryParse(a[1], out int parsed) ? parsed : 1;
            ForEachPlayer(ctx.Engine, (world, entity) =>
                Log.Info(LogCat.Console, world.Drop(entity, item, count).IsNull
                    ? $"{World.Describe(entity)} has no {item.Name} to drop"
                    : $"{World.Describe(entity)} drops {count}x {item.Name}"));
        });

        // Gameplay actions (08 §3.2): the simulation defines them, so a headless server has the same
        // ids and a PlayerCommand means the same thing on both sides.
        _actions.Register("Move", ActionKind.Axis2D);
        _actions.Register("Jump", ActionKind.Button);
        _actions.Register("Run", ActionKind.Button);
        _actions.Register("Crouch", ActionKind.Button);
        _actions.Register("Attack", ActionKind.Button);
        _actions.Register("Use", ActionKind.Button);
    }

    // Every cheat that acts on "the player" means the same thing by it (16 §3.1).
    private static void ForEachPlayer(Engine engine, Action<World, Entity> act)
    {
        foreach (var world in engine.Worlds)
            foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
                act(world, entity);
    }

    // Games add their own tasks to this before the first world is created (16 §3.4).
    public AITaskRegistry AITasks { get; } = new();

    // Attribute and tag ids, shared by every world (16 §3.3).
    public GameplayRegistries Registries { get; } = new();

    public void OnWorldCreated(World world)
    {
        world.Resources.Set(new InteractionState());
        world.Resources.Set(Registries);
        world.Resources.Set(_records!);      // effects look up their records through the world
        Registries.Rebuild(_records!);

        // Both controllers write PawnIntent in the Commands phase, so movement (PrePhysics) acts on it
        // in the same tick it was decided. AI used to sit in Phase.AI, four phases *after* the movement
        // that reads it, which cost every creature a tick of lag (review #48).
        world.AddSystem(new PlayerControlSystem(world), Phase.Commands);
        world.AddSystem(new AIThinkSystem(world, _records!, AITasks, _actions!), Phase.Commands,
            after: new[] { typeof(PlayerControlSystem) });

        // Combat resolves before effects tick, so a blow struck this tick is felt this tick: the
        // health it costs, the tags it grants and the death it may cause all land together (16 §3.2).
        world.AddSystem(new MeleeCombatSystem(world, _records!, _actions!, _combatDebug!), Phase.Gameplay,
            before: new[] { typeof(EffectSystem) });
        world.AddSystem(new InteractionSystem(world, _records!, _actions!, _interactRange!), Phase.Gameplay,
            before: new[] { typeof(EffectSystem) });
        world.AddSystem(new EffectSystem(world, _records!), Phase.Gameplay);
        world.AddSystem(new CharacterMovementSystem(world, _records!, _actions!), Phase.PrePhysics,
            before: new[] { typeof(PhysicsSyncSystem) });
        // Animation is simulation too (12 §3): it runs at the tick rate, and its frame events are
        // what lands a sprite's blow. It moves to an animation module when skeletal animation lands.
        world.AddSystem(new SpriteAnimationSystem(world, _records!), Phase.Animation);
        world.AddSystem(new AIDebugSystem(world, _records!, _aiDebug!), Phase.Late);   // 16 §11
        world.AddSystem(new FirstPersonCameraSystem(world, _records!), Phase.FrameUpdate);
    }
}
