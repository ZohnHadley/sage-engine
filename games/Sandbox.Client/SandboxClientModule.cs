using System.Numerics;

namespace Sandbox;   // sage_engine and Friflo.Engine.ECS come from games/Directory.Build.props

// The Sandbox's client half (docs/design/01 §3.1, TODO R15): the parts of the game that need a
// screen. Everything else — the scene, the rules, spawning, the combat log — is in `Sandbox`, which
// references only `Sage.Engine`, so it can be ticked headlessly in a test.
//
// The split is the one the engine review asked games to follow (item 6). It costs a second assembly
// and buys two things: a game's simulation becomes testable without MonoGame, and the boundary is
// enforced by the compiler rather than by remembering. A dedicated server would run `Sandbox` and not
// this.
//
// It is a plain `IModule`, not an `IGameModule` — there is exactly one of those per game, and it is
// the simulation. The host loads this through `game.json`'s `modules.add`.
public sealed class SandboxClientModule : IModule
{
    private Renderer? _renderer;
    private ContentService? _content;
    private RecordStore? _records;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(ClientModule) };

    public void Init(ModuleContext ctx)
    {
        // A box mesh built at run time needs the renderer, which is why this part lives on the client
        // side and the simulation only declares that it is optional (F31). Headless, a crate is a
        // collider with no mesh, which is exactly right.
        ctx.Engine.Prefabs.Register("box_mesh", (world, entity, options, where) =>
        {
            var o = PrefabParts.Read<BoxMeshOptions>(world, options, "box_mesh", where);
            if (o.Size == Vector3.Zero) { Log.Error(LogCat.Records, $"{where}: box_mesh needs a \"size\""); return; }
            world.Add(entity, new MeshRenderer { Handle = _renderer!.CreateBox(o.Size, World.Describe(entity)), Material = o.Material });
        });
    }

    private sealed class BoxMeshOptions { public Vector3 Size; public RecordId Material; }

    public void Start(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _renderer = ctx.Get<Renderer>();
        _content = ctx.Get<ContentService>();   // textures for the HUD's viewmodel (13 §3)
    }

    public void OnWorldCreated(World world) =>
        world.AddSystem(new SandboxHud(world, _records!, _content!), Phase.FrameUpdate);   // 13 §3
}
