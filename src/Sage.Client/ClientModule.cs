#nullable enable
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// What the host offers client modules (ModuleManager.ProvideHostService): the MonoGame Game and its
// graphics device. Provided before Start, so client modules can create GPU resources there.
public sealed class ClientHost
{
    public ClientHost(Game game) { Game = game; }
    public Game Game { get; }
    public GraphicsDevice GraphicsDevice => Game.GraphicsDevice;
}

// The client engine module (a default module; docs/design/01 §3.1): registers the client record types
// and the engine's input actions, provides ContentService and Renderer, and per world installs the
// RenderSnapshot and the Extract/Render systems (06 §3.1).
public sealed class ClientModule : IModule
{
    private ContentService? _content;
    private Renderer? _renderer;
    private RecordStore? _records;

    public void Init(ModuleContext ctx)
    {
        ctx.Engine.Records.Register<MaterialRecord>();
        ctx.Engine.Records.Register<InputMapRecord>();
        ctx.Engine.Records.Register<SpriteSheetRecord>();

        // The engine's actions (08 §3.2); bindings are in engine_content/data/input.json. Move and Look
        // feed PlayerCommand.Move and the view angles.
        var actions = ctx.Engine.Actions;
        actions.Register("Move", ActionKind.Axis2D);
        actions.Register("Look", ActionKind.Axis2D);
        actions.Register("Jump", ActionKind.Button);
        actions.Register("Attack", ActionKind.Button);
        actions.Register("Use", ActionKind.Button);
        actions.Register("Menu", ActionKind.Button);
        actions.Register("ToggleConsole", ActionKind.Button);
    }

    public void Start(ModuleContext ctx)
    {
        var host = ctx.Get<ClientHost>();
        _records = ctx.Engine.Records;
        _content = new ContentService(host, ctx.Engine.Vfs);
        _renderer = new Renderer(host, _content, ctx.Engine);
        ctx.Provide(_content);
        ctx.Provide(_renderer);
    }

    public void OnWorldCreated(World world)
    {
        world.Resources.Set(new RenderSnapshot());
        // Sprite animation is simulation, not rendering (12 §3): it runs at the tick rate, and a
        // headless server would run it too. It lives here until there is a framework module.
        world.AddSystem(new SpriteAnimationSystem(world), Phase.Animation);
        world.AddSystem(new CameraExtract(world, _renderer!), Phase.Extract);
        world.AddSystem(new MeshExtract(world, _renderer!), Phase.Extract, after: new[] { typeof(CameraExtract) });
        world.AddSystem(new SpriteExtract(world, _renderer!, _records!), Phase.Extract, after: new[] { typeof(CameraExtract) });
        world.AddSystem(new RenderSystem(world, _renderer!), Phase.Render);
    }

    public void Shutdown()
    {
        _renderer?.Dispose();
        _content?.Dispose();
    }
}
