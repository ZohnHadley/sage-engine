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
    private ActionRegistry? _actions;

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
        _actions = ctx.Engine.Actions;
        _jump = ctx.Engine.Actions.Get("Jump");   // registered by GameplayModule (08 §3.2)
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
        world.AddSystem(new AutoAttackSystem(world, _cvars!, _actions!), Phase.Commands, after: new[] { typeof(PlayerControlSystem) });
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
            foreach (var e in world.Query<Transform>().AllTags(Tags.Get<FromSpawnRecord>()).Entities.ToEntityList())
                world.Destroy(e);
            Spawn(world);
        }
    }

    private void Spawn(World world)
    {
        foreach (var spawn in _records!.All<SpawnRecord>())
        {
            if (spawn.Player) continue;   // the rules spawn the player when the world starts (16)
            SpawnOne(world, spawn);
        }
        Log.Info(LogCat.Gameplay, $"Sandbox: spawned {world.Query<Transform>().AllTags(Tags.Get<FromSpawnRecord>()).Count} entities in '{world.Name}'");
    }

    // Where the player starts: the position of the spawn record marked "player".
    public Vector3 PlayerStart(World world)
    {
        foreach (var spawn in _records!.All<SpawnRecord>())
            if (spawn.Player)
            {
                Vector3 position = HillsGenerator.SceneCenter + spawn.Position;
                position.Y = world.Resources.Get<Terrain>().HeightAt(position.X, position.Z) + spawn.Position.Y;
                return position;
            }
        return HillsGenerator.SceneCenter;
    }

    // Called by SandboxRules once every module has set the world up.
    public Entity SpawnPlayer(World world)
    {
        foreach (var spawn in _records!.All<SpawnRecord>())
            if (spawn.Player) return SpawnOne(world, spawn);

        Log.Warn(LogCat.Gameplay, "No spawn record marked \"player\": there is nothing to control");
        return default;
    }

    private Entity SpawnOne(World world, SpawnRecord spawn)
    {
        // A spawn's position is relative to the scene's spot in the sector, and its y is height above
        // the ground: everything stands on the terrain.
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
        bool character = spawn.Character || spawn.Ai;

        // What it looks like.
        if (!spawn.Sheet.IsEmpty)
        {
            world.Add(e, new SpriteRenderer { Sheet = spawn.Sheet, Material = spawn.Material, Size = spawn.Size });
            if (spawn.Animate) world.Add(e, SpriteAnimator.Play(0));
        }
        else if (spawn.BoxMesh != Vector3.Zero)
        {
            world.Add(e, new MeshRenderer { Handle = _renderer!.CreateBox(spawn.BoxMesh, spawn.Name), Material = spawn.Material });
        }
        else if (!spawn.Model.IsEmpty)
        {
            world.Add(e, new MeshRenderer { Mesh = spawn.Model, Material = spawn.Material });
            if (spawn.FacesCamera) e.AddTag<FacesCamera>();
        }

        if (character)
        {
            // A capsule the engine moves, so the world collides with it and it collides with the
            // world, plus an intent for its controller to write (10, 16). The player's controller is
            // the local PlayerCommand; a creature's is its AI schedules.
            var layers = world.Resources.Get<PhysicsSpace>().Layers;
            world.AddCharacter(e, spawn.Player ? layers.Player : layers.Enemy);
            if (spawn.Player) e.AddTag<PlayerControlled>();
            if (spawn.Ai) world.Add(e, new AIState { Schedule = AIThinkSystem.Schedules.Idle });
            if (!spawn.Attack.IsEmpty) world.Add(e, Melee.With(spawn.Attack));   // what it swings (16 §3.2)
            world.AddAttributes(e);   // health, mana and the rest, from the attribute records (16)

            // Starting effects: the creature's tough hide is armour, so the player's fists do less to
            // it than to a person. An effect rather than a starting value, so a spell could strip it.
            foreach (var effect in spawn.Effects) Effects.Apply(world, e, effect);
        }
        else
        {
            // Props: a collider (and a mass if it should fall), or the old hop toy for scenery.
            if (spawn.ColliderSize != Vector3.Zero)
            {
                // A capsule stands on the spawn point, like the art above it; a box or a sphere is
                // centred on it, which is what a crate or a trigger volume wants (10 §3).
                var collider = spawn.Collider == ColliderShape.Capsule
                    ? Collider.Standing(spawn.ColliderSize.X, spawn.ColliderSize.Y)
                    : new Collider { Shape = spawn.Collider, Size = spawn.ColliderSize };
                collider.IsTrigger = spawn.Trigger;
                world.Add(e, collider);
                world.Add(e, spawn.Mass > 0 ? RigidBody.Dynamic(spawn.Mass) : new RigidBody { Kind = BodyKind.Static });
            }
            else if (!spawn.Sheet.IsEmpty || !spawn.Model.IsEmpty)
            {
                world.Add(e, new Hop { BaseY = position.Y });
            }
        }

        e.AddTag<FromSpawnRecord>();
        return e;
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
    public float Yaw;                  // degrees about +Y, 0 faces -Z (SageMath): picks the sprite group
    public Vector2 Size;               // sprite size in metres; 0 = the sheet's
    public bool Animate;               // play the sheet's first clip
    public bool FacesCamera;           // meshes only: the old billboard test

    // Physics (docs/design/10): a box mesh drawn at BoxMesh size, a collider, and a mass that makes
    // it a falling dynamic body instead of a static one.
    public Vector3 BoxMesh;
    public ColliderShape Collider = ColliderShape.Box;
    public Vector3 ColliderSize;       // zero = none; box: full extents, capsule: [radius, total height]
    public float Mass;                 // > 0 = dynamic
    public bool Trigger;

    // A character (docs/design/10 §3): a capsule the engine moves. `player` makes it the pawn the
    // local PlayerCommand drives, with the first-person camera in its head.
    public bool Character;
    public bool Player;
    public bool Ai;                    // a creature that chases the player (16 3.4)

    // Combat (16 §3.2): what it swings, and the effects it starts with (armour from a hide, a buff).
    public RecordId Attack;
    public List<RecordId> Effects = new();
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
    private float _yaw;
    private bool _turning;

    public AutoWalkSystem(World world, CVarRegistry cvars)
    {
        _pawns = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
        _autoWalk = cvars.Register("sandbox_autowalk", 0f, CVarFlags.DevOnly,
            "Walk the player forward by itself, turning this many degrees per second (0 = off).", 0f, 180f);
    }

    public void Run(in SystemContext ctx)
    {
        if (_autoWalk.Value <= 0f) { _turning = false; return; }
        float turn = _autoWalk.Value * MathF.PI / 180f * ctx.Tick.Dt;
        foreach (var (intents, _) in _pawns.Chunks)
        {
            var intent = intents.Span;
            for (int n = 0; n < intent.Length; n++)
            {
                // The yaw has to be accumulated here: PlayerControlSystem ran first this tick and
                // overwrote intent.Yaw with the (unchanging) view yaw, so turning it by a step per tick
                // only ever produced a constant offset and the pawn walked in a straight line (#51).
                if (!_turning) { _yaw = intent[n].Yaw; _turning = true; }
                _yaw = SageMath.WrapPi(_yaw - turn);
                intent[n].Move = new Vector2(0, 1);
                intent[n].Yaw = _yaw;
            }
        }
    }
}

// The other half of the keyboardless smoke test (`sandbox_autowalk`): swing every so often, so a
// scripted run exercises the player's side of combat. A real game would never ship this.
public sealed class AutoAttackSystem : ISystem
{
    private readonly ArchetypeQuery<PawnIntent> _pawns;
    private readonly CVar<float> _autoAttack;
    private readonly ActionId _attack;
    private float _next;

    public AutoAttackSystem(World world, CVarRegistry cvars, ActionRegistry actions)
    {
        _pawns = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
        _attack = actions.Get("Attack");
        _autoAttack = cvars.Register("sandbox_autoattack", 0f, CVarFlags.DevOnly,
            "Swing the player's weapon this many seconds apart by itself (0 = off).", 0f, 30f);
    }

    public void Run(in SystemContext ctx)
    {
        if (_autoAttack.Value <= 0f) return;
        _next -= ctx.Tick.Dt;
        if (_next > 0f) return;
        _next = _autoAttack.Value;

        foreach (var (intents, _) in _pawns.Chunks)
        {
            var intent = intents.Span;
            for (int n = 0; n < intent.Length; n++) intent[n].Pressed = intent[n].Pressed.With(_attack);
        }
    }
}

// Late phase: reports every hit the tick landed, whoever threw it (16 §3.2). A real game would
// play a sound and flash the screen here (cues, F21); the slice prints a line.
public sealed class CombatLogSystem : ISystem
{
    private readonly CombatEvents _events;

    public CombatLogSystem(World world)
    {
        _events = world.Resources.Get<CombatEvents>();
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var hit in _events.Damage)
            Log.Info(LogCat.Gameplay, $"{World.Describe(hit.Attacker)} hits {World.Describe(hit.Target)} " +
                                      $"for {hit.Applied:F0} ({hit.Amount:F0} before armour): " +
                                      $"health {ctx.World.Attribute(hit.Target, AttributeRecord.Health):F0}");
    }
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
