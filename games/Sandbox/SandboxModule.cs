using System.Numerics;

namespace Sandbox;   // sage_engine and Friflo.Engine.ECS come from games/Directory.Build.props

// The Sandbox game's *simulation* (docs/design/01 §4, §3.1): the dogfooding game that grows into the
// Daggerfall-like vertical slice (TODO milestone). It places its scene from prefabs (F31), runs the
// game's own rules, and makes things hop on the Jump action (PlayerCommand, 08 §3.4).
//
// It references `Sage.Engine` and nothing else, so all of this is testable headlessly (R15); the HUD
// and anything else needing a screen live in `Sandbox.Client`.
public sealed class SandboxModule : IGameModule
{
    private RecordStore? _records;
    private ActionId _jump = ActionId.None;
    private readonly List<World> _worlds = new();

    // The simulation depends on gameplay, not on the client: that is the whole point of the split
    // (R15). Sandbox.Client declares the ClientModule dependency for the half that needs a screen.
    public IReadOnlyList<Type> Dependencies => new[] { typeof(ItemsModule), typeof(AIModule) };

    public void Init(ModuleContext ctx)
    {
        ctx.Engine.Records.Register<SceneRecord>();

        // What a game adding to prefabs looks like (05 "As built (prefabs)"). `hop` is a toy that has
        // to read where the thing was placed, so it belongs to the simulation. `box_mesh` builds a
        // mesh at run time, which needs the renderer, so it lives in Sandbox.Client — here the
        // simulation only says that going without it is fine, which is what a dedicated server does.
        ctx.Engine.Prefabs.Optional("box_mesh");
        ctx.Engine.Prefabs.Register("hop", (world, entity, _, _) =>
            world.Add(entity, new Hop { BaseY = world.Get<Transform>(entity).LocalPosition.Y }));
    }


    public void Start(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _records.Reloaded += RespawnAll;   // hot reload: edit content/data/scene.json while running
        _jump = ctx.Engine.Actions.Get("Jump");   // registered by CharacterModule (08 §3.2)
    }

    public void OnWorldCreated(World world)
    {
        // Terrain first: the scene is placed on the ground (14 §3, TODO F13).
        var terrain = world.Resources.Get<Terrain>();
        terrain.Generator = new HillsGenerator();
        terrain.Seed = 1;
        terrain.Load(SectorCoord.Zero);

        world.AddSystem(new HopSystem(world, _jump), Phase.Gameplay);
        world.AddSystem(new TriggerLogSystem(world), Phase.PostPhysics);
        world.AddSystem(new CombatLogSystem(world), Phase.Late);
        world.Resources.Set<GameRules>(new SandboxRules(this));
        world.AddSystem(new FaceCameraSystem(world), Phase.Gameplay);
        _worlds.Add(world);
        Spawn(world);
    }

    public void Shutdown()
    {
        if (_records != null) _records.Reloaded -= RespawnAll;
    }

    private void RespawnAll()
    {
        foreach (var world in _worlds)
        {
            foreach (var e in world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList())
                world.Destroy(e);
            Spawn(world);

            // And the player, which the rules placed at world start rather than the scene loop. It
            // carries the same tag, so the sweep above took it: without this, saving a record file
            // left the world with nothing to control (review #59).
            SpawnPlayer(world);
        }
    }

    private SceneRecord? Scene => _records!.TryGet(new RecordId("sandbox", "main"), out SceneRecord scene) ? scene : null;

    private void Spawn(World world)
    {
        var scene = Scene;
        if (scene == null) { Log.Warn(LogCat.Gameplay, "No `scene` record 'sandbox:main': the world is empty"); return; }

        for (int i = 0; i < scene.Place.Count; i++) Place(world, scene.Place[i], i);
        Log.Info(LogCat.Gameplay, $"Sandbox: placed {world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Count} entities in '{world.Name}'");
    }

    // Where the player starts, without placing anything: the camera needs it before the rules run.
    public Vector3 PlayerStart(World world) =>
        Scene?.Player is { } start ? Ground(world, start) : HillsGenerator.SceneCenter;

    // Called by SandboxRules once every module has set the world up.
    public Entity SpawnPlayer(World world)
    {
        if (Scene?.Player is { } start) return Place(world, start, -1);   // -1: the player's own identity
        Log.Warn(LogCat.Gameplay, "The scene has no \"player\" placement: there is nothing to control");
        return default;
    }

    // A placement's `at` is relative to the scene's spot in the sector, and its y is height above the
    // ground, so everything stands on the terrain however the hills came out.
    private static Vector3 Ground(World world, ScenePlacement placement)
    {
        Vector3 position = HillsGenerator.SceneCenter + placement.At;
        position.Y = world.Resources.Get<Terrain>().HeightAt(position.X, position.Z) + placement.At.Y;
        return position;
    }

    // One line, and the engine builds the whole thing from the prefab (F31). What used to live here
    // was a hundred lines of "if it has a sheet... else if it has a box mesh... else if it is a
    // character", which is the engine's job and is now done once, in one order, for every game.
    private Entity Place(World world, ScenePlacement placement, int index)
    {
        var entity = world.Spawn(placement.Prefab, Ground(world, placement), placement.Yaw);
        if (entity.IsNull) return entity;
        if (!string.IsNullOrEmpty(placement.Name)) entity.Name = new EntityName(placement.Name);
        entity.AddTag<FromScene>();

        // A stable identity, so a save can find this *same* thing next run (09 §3.5, F27). Derived
        // from the placement rather than authored, because a scene record is a list and its entries
        // do not want GUIDs in them — but it does mean that reordering `place` moves identities, which
        // a real map file (with ids in it) will not.
        world.Add(entity, new Persistent { Id = PersistentId.FromName($"sandbox:scene:{index}:{placement.Prefab}") });
        return entity;
    }

}


// Where the scene's instances go (content/data/scene.json). A prefab says what a thing *is* (05
// §3.5); this says where the copies of it are. Map files with persistent ids and per-entity overrides
// are F27 — this is the four fields that need until then.
[Record("scene")]
public sealed class SceneRecord
{
    public ScenePlacement? Player;            // where the local player starts
    public List<ScenePlacement> Place = new();
}

public sealed class ScenePlacement
{
    public RecordId Prefab;
    public Vector3 At;                        // relative to the scene centre; y is height above ground
    public float Yaw;                         // degrees about +Y, 0 faces -Z (SageMath)
    public string Name = "";                  // optional, so `ent_list` can tell two copies apart
}

// Tags.
public struct FacesCamera : ITag { }
// Placed by the scene record, so hot reload knows what to sweep away and put back.
public struct FromScene : ITag { }

// A little vertical hop with gravity.
public struct Hop : IComponent
{
    public float BaseY;
    public float Velocity;
}

// Gameplay phase (Fixed): Jump (from the tick's PlayerCommand, never from the keyboard) launches
// every hopping entity that is on the ground. A tap shorter than a tick still counts: the command
// latches presses between ticks (08 §3.4).
public sealed class HopSystem : ISystem
{
    private const float LaunchSpeed = 3.5f, Gravity = -12f;
    private readonly ArchetypeQuery<Transform, Hop> _hoppers;
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
public sealed class FaceCameraSystem : ISystem
{
    private readonly ArchetypeQuery<Transform> _facing;

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
public sealed class TriggerLogSystem : ISystem
{
    private readonly PhysicsSpace _space;

    public TriggerLogSystem(World world)
    {
        _space = world.Resources.Get<PhysicsSpace>();
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
                                      $"health {world.Attribute(hit.Target, AttributeRecord.Health):F0}");

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
        : entity.TryGetComponent(out EntityName name) ? name.value : "something";
}

// The Sandbox's rules (docs/design/16): what happens when the world starts, and where the player
// comes from. Death, respawn and time of day join it as the slice grows.
public sealed class SandboxRules : GameRules
{
    private readonly SandboxModule _game;

    public SandboxRules(SandboxModule game) { _game = game; }

    public override void OnWorldStarted(World world)
    {
        var player = SpawnPlayer(world);
        Log.Info(LogCat.Gameplay, $"Sandbox rules started in '{world.Name}': player {World.Describe(player)}");
    }

    public override Entity SpawnPlayer(World world) => _game.SpawnPlayer(world);

    // The creature's claws can kill the player: put them back on their feet, healed (16 §3.1). A real
    // game would show a screen and ask; this is the slice's stand-in.
    public override void OnEntityDied(World world, Entity victim, Entity killer)
    {
        Log.Info(LogCat.Gameplay, killer.IsNull
            ? $"{World.Describe(victim)} died"
            : $"{World.Describe(victim)} was killed by {World.Describe(killer)}");
        if (victim.Tags.Has<PlayerControlled>()) world.Say("You died", MessageKind.Bad, 4f);
        else world.Say($"{(victim.TryGetComponent(out EntityName n) ? n.value : "Something")} dies", MessageKind.Good, 4f);
        if (!world.HasTag(victim, new RecordId("sage", "state.dead"))) return;

        if (world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList().Contains(victim))
        {
            world.RemoveTag(victim, new RecordId("sage", "state.dead"));
            world.AddAttributes(victim);                       // back to full health
            world.Teleport(victim, Transform.At(_game.PlayerStart(world)));
            Log.Info(LogCat.Gameplay, "The player respawns at the start");
        }
    }
}
