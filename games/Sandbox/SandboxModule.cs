using System.Numerics;

namespace Sandbox;   // the Sage.* and Friflo.Engine.ECS usings come from games/Directory.Build.props

// The Sandbox game's *simulation* (docs/design/01 §4, §3.1): the dogfooding game that grows into the
// Daggerfall-like vertical slice (TODO milestone). It runs the game's own rules and makes things hop on
// the Jump action (PlayerCommand, 08 §3.4). Its scene is data — `sandbox:main` in content/data/scene.json,
// named by game.json's "scene" — and the engine places it (issue #29).
//
// It references the base engine and nothing else, so all of this is testable headlessly (R15); the HUD
// and anything else needing a screen live in `Sandbox.Client`.
[Plugin("sandbox", "0.1.0")]
[RequiresPlugin("sage.streaming", ">=0.1")]            // its hills
[RequiresPlugin("sage.gameplay.character", ">=0.1")]   // the player it commands and looks through
public sealed class SandboxModule : IGameModule
{
    private ActionId _jump = ActionId.None;

    // The simulation depends on gameplay, not on the client: that is the whole point of the split
    // (R15). Sandbox.Client declares the ClientModule dependency for the half that needs a screen.
    public IReadOnlyList<Type> Dependencies => new[] { typeof(ItemsModule), typeof(AIModule) };

    public void Init(ModuleContext ctx)
    {
        // What a game adding to prefabs looks like (05 "As built (prefabs)"): `hop` is declared below
        // (HopPart) and needs no call. `box_mesh` builds a mesh at run time, which needs the renderer,
        // so it lives in Sandbox.Client — here the simulation only says that going without it is
        // fine, which is what a dedicated server does.
        ctx.Engine.Prefabs.Optional("box_mesh");
        ctx.Engine.Prefabs.Optional("audio");     // the client declares it (11 §3); headless has no ears
        // The campfire's embers, likewise the client's (06 §3.12). Missing until prefab bodies were
        // checked at load (issue #22): only a headless spawn of the campfire ever said so.
        ctx.Engine.Prefabs.Optional("particles");
    }

    public void Start(ModuleContext ctx)
    {
        _jump = ctx.Engine.Actions.Get("Jump");   // registered by CharacterModule (08 §3.2)
    }

    public void OnWorldCreated(World world)
    {
        // Terrain first: the engine places the scene on the ground once this returns (14 §3, TODO F13).
        var terrain = world.Resources.Get<Terrain>();
        terrain.Generator = new HillsGenerator();
        terrain.Seed = 1;
        terrain.Load(SectorCoord.Zero);

        world.AddSystem(new HopSystem(world, _jump));
        world.AddSystem(new TriggerLogSystem(world));
        world.AddSystem(new CombatLogSystem(world));
        world.AddSystem(new FaceCameraSystem(world));
    }

    // The Sandbox's rules for each world; the engine starts them once every module has set it up.
    public GameRules CreateRules(World world) => new SandboxRules();
}

// Tags.
[Tag("sandbox:faces_camera")]
public struct FacesCamera : ITag { }

// A little vertical hop with gravity.
[Component("sandbox:hop")]
public struct Hop : IComponent
{
    [Property(Unit = "m", Tooltip = "The ground it lands back on")]
    public float BaseY;
    [Property(Unit = "m/s", Tooltip = "Upward speed; set by a hop, pulled down by gravity")]
    public float Velocity;
}

// "hop": {} — a toy that has to read where the thing was placed, so it is a part and belongs to the
// simulation: the ground it bounces back to is wherever the scene put it.
[PrefabPart("hop")]
public sealed class HopPart : IPrefabPart
{
    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new Hop { BaseY = ctx.World.Get<Transform>(ctx.Entity).LocalPosition.Y });
}

// Gameplay phase (Fixed): Jump (from the tick's PlayerCommand, never from the keyboard) launches
// every hopping entity that is on the ground. A tap shorter than a tick still counts: the command
// latches presses between ticks (08 §3.4).
[System("sandbox.hop", Phase.Gameplay)]
public sealed class HopSystem : ISystem
{
    private const float LaunchSpeed = 3.5f, Gravity = -12f;
    private readonly Query<Transform, Hop> _hoppers;
    private readonly ActionId _jump;

    public HopSystem(World world, ActionId jump)
    {
        _hoppers = world.Query<Transform, Hop>();
        _jump = jump;
    }

    public void Run(in SystemContext ctx)
    {
        var input = ctx.World.Resources.Get<PlayerInput>();
        bool jump = input.HasCommand && input.Command.Pressed.Has(_jump);
        if (jump) Log.Debug(LogCat.Gameplay, $"Jump in the command for tick {input.Command.Tick}");
        float dt = ctx.Tick.Dt;
        foreach (var (transforms, hops, _) in _hoppers.Chunks)
        {
            var t = transforms.Span;
            var h = hops.Span;
            for (int n = 0; n < t.Length; n++)
            {
                bool grounded = t[n].LocalPosition.Y <= h[n].BaseY;
                if (jump && grounded) h[n].Velocity = LaunchSpeed;
                if (grounded && h[n].Velocity <= 0) continue;
                h[n].Velocity += Gravity * dt;
                t[n].LocalPosition.Y = MathF.Max(h[n].BaseY, t[n].LocalPosition.Y + h[n].Velocity * dt);
                if (t[n].LocalPosition.Y <= h[n].BaseY) h[n].Velocity = 0;
            }
        }
    }
}

// Gameplay phase (Fixed): turns FacesCamera entities towards the active camera. Running at the tick
// rate and drawn interpolated, it also shows the loop at work: try `sim_tickrate 5`.
// (Sprite billboarding proper is a renderer job; docs/design/06 §3.8.)
[System("sandbox.face_camera", Phase.Gameplay)]
public sealed class FaceCameraSystem : ISystem
{
    private readonly Query<Transform> _facing;

    public FaceCameraSystem(World world)
    {
        _facing = world.Query<Transform>().AllTags(Tags.Get<FacesCamera>());
    }

    public void Run(in SystemContext ctx)
    {
        Vector3 camera = ctx.World.Resources.Get<ActiveCamera>().Position;
        foreach (var (transforms, _) in _facing.Chunks)
        {
            var t = transforms.Span;
            for (int n = 0; n < t.Length; n++)
                TransformMath.Billboard(ref t[n], camera);
        }
    }
}

// PostPhysics: says what fell through the trigger volume (10 §3). Once the event bus exists (04) this
// becomes a TriggerEntered event instead of reading the space's lists.
[System("sandbox.trigger_log", Phase.PostPhysics)]
public sealed class TriggerLogSystem : ISystem
{
    private readonly IPhysicsWorld _space;

    public TriggerLogSystem(World world)
    {
        _space = world.Resources.Get<IPhysicsWorld>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var overlap in _space.TriggerEnter)
            Log.Info(LogCat.Gameplay, $"{World.Describe(overlap.Other)} entered {World.Describe(overlap.Trigger)}");
        foreach (var overlap in _space.TriggerExit)
            Log.Info(LogCat.Gameplay, $"{World.Describe(overlap.Other)} left {World.Describe(overlap.Trigger)}");
    }
}


// Late phase: reports every hit the tick landed, whoever threw it (16 §3.2). A real game would
// play a sound and flash the screen here (cues, F21); the slice prints a line.
//
// It reads the same Damaged events the death seam does, with its own cursor (04 §3.2): two readers
// of one queue, neither aware of the other, and no rule about which phase has to run first.
[System("sandbox.combat_log", Phase.Late)]
public sealed class CombatLogSystem : ISystem
{
    private readonly EventReader<Damaged> _damage;

    public CombatLogSystem(World world)
    {
        _damage = world.Events.Reader<Damaged>(this);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        foreach (ref readonly var ev in _damage.Read())
        {
            var hit = ev.Hit;
            Log.Info(LogCat.Gameplay, $"{World.Describe(hit.Attacker)} hits {World.Describe(hit.Target)} " +
                                      $"for {ev.Applied:F0} ({hit.Amount:F0} before armour): " +
                                      $"health {world.Attribute(hit.Target, world.Conventions().Health):F0}");

            // And on screen, from the player's point of view: what hit me, or what I hit (13 §3).
            bool mine = IsPlayer(world, hit.Attacker);
            if (mine) world.Say($"You hit {Name(world, hit.Target)} for {ev.Applied:F0}", MessageKind.Good, 3f);
            else if (IsPlayer(world, hit.Target)) world.Say($"{Name(world, hit.Attacker)} hits you for {ev.Applied:F0}", MessageKind.Bad, 3f);
        }
    }

    private static bool IsPlayer(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && entity.Tags.Has<PlayerControlled>();

    private static string Name(World world, Entity entity) =>
        entity.IsNull || !world.IsAlive(entity) ? "something"
        : entity.Name ?? "something";
}

// The Sandbox's rules (docs/design/16): what happens when the world starts and when something dies.
// Where the player comes from is the scene's `player` (the engine's GameRules.SpawnPlayer). Time of day
// joins it as the slice grows.
public sealed class SandboxRules : GameRules
{
    public override void OnWorldStarted(World world)
    {
        var player = SpawnPlayer(world);
        Log.Info(LogCat.Gameplay, $"Sandbox rules started in '{world.Name}': player {World.Describe(player)}");
    }

    // The creature's claws can kill the player: put them back on their feet, healed (16 §3.1). A real
    // game would show a screen and ask; this is the slice's stand-in.
    public override void OnEntityDied(World world, Entity victim, Entity killer)
    {
        Log.Info(LogCat.Gameplay, killer.IsNull
            ? $"{World.Describe(victim)} died"
            : $"{World.Describe(victim)} was killed by {World.Describe(killer)}");
        if (victim.Tags.Has<PlayerControlled>()) world.Say("You died", MessageKind.Bad, 4f);
        else world.Say($"{(victim.Name ?? "Something")} dies", MessageKind.Good, 4f);
        var dead = world.Conventions().Dead;   // the engine's words, as this game's content left them
        if (!world.HasTag(victim, dead)) return;

        if (world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList().Contains(victim))
        {
            world.RemoveTag(victim, dead);
            world.AddAttributes(victim);                       // back to full health
            if (world.PlayerStart() is { } start) world.Teleport(victim, Transform.At(start));
            Log.Info(LogCat.Gameplay, "The player respawns at the start");
        }
    }
}
