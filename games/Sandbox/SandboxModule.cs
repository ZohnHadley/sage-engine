using System.Numerics;

namespace Sandbox;   // sage_engine and Friflo.Engine.ECS come from games/Directory.Build.props

// The Sandbox game module (docs/design/01 §4): the dogfooding game that grows into the Daggerfall-like
// vertical slice (TODO milestone). Today it spawns the test scene from `spawn` records.
public sealed class SandboxModule : IGameModule
{
    private ContentService? _content;
    private RecordStore? _records;
    private readonly List<World> _worlds = new();

    public IReadOnlyList<Type> Dependencies => new[] { typeof(ClientModule) };

    public void Init(ModuleContext ctx)
    {
        ctx.Engine.Records.Register<SpawnRecord>();
    }

    public void Start(ModuleContext ctx)
    {
        _content = ctx.Get<ContentService>();
        _records = ctx.Engine.Records;
        _records.Reloaded += RespawnAll;   // hot reload: edit content/data/scene.json while running
    }

    public void OnWorldCreated(World world)
    {
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
            if (string.IsNullOrEmpty(spawn.Model)) continue;   // templates like creature_base
            var e = world.Create(Transform.At(spawn.Position), string.IsNullOrEmpty(spawn.Name) ? null : spawn.Name);
            world.Add(e, new ModelRenderer { Model = _content!.LoadModel(spawn.Model) });
            e.AddTag<FromSpawnRecord>();
            if (spawn.FacesCamera) e.AddTag<FacesCamera>();
        }
        Log.Info(LogCat.Gameplay, $"Sandbox: spawned {world.Query<Transform>().AllTags(Tags.Get<FromSpawnRecord>()).Count} entities in '{world.Name}'");
    }
}

// `spawn` records (content/data/*.json): what to place in the test scene.
[Record("spawn")]
public sealed class SpawnRecord
{
    public string Name = "";
    public string Model = "";          // content path without extension (loaded through the VFS)
    public Vector3 Position;
    public bool FacesCamera;
}

// Tags.
public struct FacesCamera : ITag { }
public struct FromSpawnRecord : ITag { }

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
