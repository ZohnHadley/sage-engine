using System.Numerics;

namespace Sandbox;   // sage_engine and Friflo.Engine.ECS come from games/Directory.Build.props

// The Sandbox game module (docs/design/01 §4): the dogfooding game that grows into the Daggerfall-like
// vertical slice (TODO milestone). Today it spawns the test scene from `spawn` records, draws it with
// material records, and makes it hop on the Jump action (PlayerCommand, 08 §3.4).
public sealed class SandboxModule : IGameModule
{
    private RecordStore? _records;
    private Renderer? _renderer;
    private CVarRegistry? _cvars;
    private ActionId _jump = ActionId.None;
    private readonly List<World> _worlds = new();

    public IReadOnlyList<Type> Dependencies => new[] { typeof(ClientModule) };

    public void Init(ModuleContext ctx)
    {
        ctx.Engine.Records.Register<SpawnRecord>();
    }

    public void Start(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _cvars = ctx.Engine.CVars;
        _renderer = ctx.Get<Renderer>();
        _records.Reloaded += RespawnAll;   // hot reload: edit content/data/scene.json while running
        _jump = ctx.Engine.Actions.Get("Jump");   // registered by ClientModule
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
        world.AddSystem(new AutoWalkSystem(world, _cvars!), Phase.Commands, after: new[] { typeof(PlayerControlSystem) });
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
            foreach (var e in world.Query<Transform>().AllTags(Tags.Get<FromSpawnRecord>()).Entities.ToEntityList())
                world.Destroy(e);
            Spawn(world);
        }
    }

    private void Spawn(World world)
    {
        foreach (var spawn in _records!.All<SpawnRecord>())
        {
            if (spawn.Model.IsEmpty && spawn.Sheet.IsEmpty && spawn.BoxMesh == Vector3.Zero && spawn.ColliderSize == Vector3.Zero && !spawn.Character) continue;
            // A spawn's position is relative to the scene's spot in the sector, and its y is height
            // above the ground: everything stands on the terrain.
            var terrain = world.Resources.Get<Terrain>();
            Vector3 position = HillsGenerator.SceneCenter + spawn.Position;
            position.Y = terrain.HeightAt(position.X, position.Z) + spawn.Position.Y;
            var transform = new Transform
            {
                LocalPosition = position,
                LocalRotation = Quaternion.CreateFromYawPitchRoll(spawn.Yaw * MathF.PI / 180f, 0, 0),
                LocalScale = Vector3.One,
            };
            var e = world.Create(transform, string.IsNullOrEmpty(spawn.Name) ? null : spawn.Name);
            if (!spawn.Sheet.IsEmpty)
            {
                world.Add(e, new SpriteRenderer { Sheet = spawn.Sheet, Material = spawn.Material, Size = spawn.Size });
                if (spawn.Animate) world.Add(e, SpriteAnimator.Play(0));
                world.Add(e, new Hop { BaseY = position.Y });
            }
            else if (spawn.BoxMesh != Vector3.Zero)
            {
                world.Add(e, new MeshRenderer { Handle = _renderer!.CreateBox(spawn.BoxMesh, spawn.Name), Material = spawn.Material });
            }
            else if (!spawn.Model.IsEmpty)
            {
                world.Add(e, new MeshRenderer { Mesh = spawn.Model, Material = spawn.Material });
                if (spawn.FacesCamera) e.AddTag<FacesCamera>();
                world.Add(e, new Hop { BaseY = position.Y });
            }

            // A character: capsule + kinematic body, so it collides with the world and the world
            // collides with it, and an intent for its controller to write (16 §3.1).
            if (spawn.Character)
            {
                var profile = _records.TryGet(MovementProfileRecord.Default, out MovementProfileRecord p) ? p : new MovementProfileRecord();
                world.Add(e, CharacterController.Create());
                world.Add(e, new Pawn());
                world.Add(e, new PawnIntent());
                world.Add(e, Collider.Capsule(profile.Radius, profile.StandHeight - 2 * profile.Radius, layer: 1));
                world.Add(e, RigidBody.Kinematic());
                if (spawn.Player) e.AddTag<PlayerControlled>();
                continue;
            }

            // Physics: a collider, and a mass if it should fall (10 §3).
            if (spawn.ColliderSize != Vector3.Zero)
            {
                world.Add(e, new Collider { Shape = spawn.Collider, Size = spawn.ColliderSize, IsTrigger = spawn.Trigger });
                world.Add(e, spawn.Mass > 0 ? RigidBody.Dynamic(spawn.Mass) : new RigidBody { Kind = BodyKind.Static });
            }
            e.AddTag<FromSpawnRecord>();
        }
        Log.Info(LogCat.Gameplay, $"Sandbox: spawned {world.Query<Transform>().AllTags(Tags.Get<FromSpawnRecord>()).Count} entities in '{world.Name}'");
    }
}

// `spawn` records (content/data/*.json): what to place in the test scene. Either a mesh (`model`) or
// a billboard sprite (`sheet`, docs/design/06 §3.8).
[Record("spawn")]
public sealed class SpawnRecord
{
    public string Name = "";
    public AssetPath Model;            // mesh asset path (an MGCB model: no extension)
    public RecordId Sheet;             // sprite sheet record
    public RecordId Material;          // empty = the default for the kind
    public Vector3 Position;
    public float Yaw;                  // degrees; which way it faces (picks the sprite's direction)
    public Vector2 Size;               // sprite size in metres; 0 = the sheet's
    public bool Animate;               // play the sheet's first clip
    public bool FacesCamera;           // meshes only: the old billboard test

    // Physics (docs/design/10): a box mesh drawn at BoxMesh size, a collider, and a mass that makes
    // it a falling dynamic body instead of a static one.
    public Vector3 BoxMesh;
    public ColliderShape Collider = ColliderShape.Box;
    public Vector3 ColliderSize;       // zero = no collider
    public float Mass;                 // > 0 = dynamic
    public bool Trigger;

    // A character (docs/design/10 §3): a capsule the engine moves. `player` makes it the pawn the
    // local PlayerCommand drives, with the first-person camera in its head.
    public bool Character;
    public bool Player;
}

// Tags.
public struct FacesCamera : ITag { }
public struct FromSpawnRecord : ITag { }

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

// Commands phase, after the player controller: walks the pawn forward on its own while
// `sandbox_autowalk` is on. It is how the character controller gets exercised without a person at the
// keyboard (screenshots, smoke runs); a real game would never ship this.
public sealed class AutoWalkSystem : ISystem
{
    private readonly ArchetypeQuery<PawnIntent> _pawns;
    private readonly CVar<float> _autoWalk;

    public AutoWalkSystem(World world, CVarRegistry cvars)
    {
        _pawns = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
        _autoWalk = cvars.Register("sandbox_autowalk", 0f, CVarFlags.DevOnly,
            "Walk the player forward by itself, turning this many degrees per second (0 = off).", 0f, 180f);
    }

    public void Run(in SystemContext ctx)
    {
        if (_autoWalk.Value <= 0f) return;
        float turn = _autoWalk.Value * MathF.PI / 180f * ctx.Tick.Dt;
        foreach (var (intents, _) in _pawns.Chunks)
        {
            var intent = intents.Span;
            for (int n = 0; n < intent.Length; n++)
            {
                intent[n].Move = new Vector2(0, 1);
                intent[n].Yaw -= turn;
            }
        }
    }
}
