#nullable enable
using System;
using System.IO;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
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

// Loads MonoGame content (.xnb built by MGCB) through the VFS instead of a fixed Content folder, so
// game mounts and mods can provide or shadow content by path (docs/design/05 §3.2). Interim: the
// AssetServer with runtime PNG/glTF loaders replaces this (TODO R12).
public sealed class ContentService : IDisposable
{
    private readonly VfsContentManager _content;

    internal ContentService(ClientHost host, VirtualFileSystem vfs)
    {
        _content = new VfsContentManager(host.Game.Services, vfs);
    }

    // `name` is a virtual path without the .xnb extension, e.g. "stanford_bunny".
    public Model LoadModel(string name) => _content.Load<Model>(name);
    public Texture2D LoadTexture(string name) => _content.Load<Texture2D>(name);

    public void Dispose() => _content.Dispose();

    private sealed class VfsContentManager : ContentManager
    {
        private readonly VirtualFileSystem _vfs;

        public VfsContentManager(IServiceProvider services, VirtualFileSystem vfs) : base(services) { _vfs = vfs; }

        protected override Stream OpenStream(string assetName)
        {
            var path = VirtualPath.Parse(assetName + ".xnb");
            var mount = _vfs.Which(path);
            if (mount == null)
            {
                Log.Warn(LogCat.Assets, $"Content '{assetName}' not found in any mount");
                throw new ContentLoadException($"Content '{assetName}' not found in any mount (looked for {path}).");
            }
            Log.Debug(LogCat.Assets, $"Loading {path} from {mount.Name}");
            return mount.Open(path);
        }
    }
}

// The client engine module (a default module; docs/design/01 §3.1): content loading through the VFS,
// and per world the RenderView resource + the model renderer.
public sealed class ClientModule : IModule
{
    private ContentService? _content;

    public void Init(ModuleContext ctx) { }

    public void Start(ModuleContext ctx)
    {
        _content = new ContentService(ctx.Get<ClientHost>(), ctx.Engine.Vfs);
        ctx.Provide(_content);
    }

    public void OnWorldCreated(World world)
    {
        world.Resources.Set(new RenderView());
        world.AddSystem(new ModelRendererSystem(world), Phase.Render);
    }

    public void Shutdown() => _content?.Dispose();
}
