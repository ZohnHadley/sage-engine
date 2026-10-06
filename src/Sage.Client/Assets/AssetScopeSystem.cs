#nullable enable
namespace Sage.Client;

// The frame's safe point for assets (issue #308, Sage.Simulation's Content/AssetScopes.cs): first in
// FrameUpdate, before any world extracts, the renderer frees what sectors released that no world holds
// now and the UI pictures no screen has drawn for a while, and starts the frame's upload budget. One per
// world, so a streaming world's releases are watched; the renderer does the work once a frame.
[System("sage.client.asset_scopes", Phase.FrameUpdate)]
internal sealed class AssetScopeSystem : ISystem
{
    private readonly Renderer _renderer;

    public AssetScopeSystem(World world, Renderer renderer)
    {
        _renderer = renderer;
        if (world.Resources.TryGet<SectorAssets>(out var assets) && assets != null) renderer.Releases.Watch(assets);
    }

    public void Run(in SystemContext ctx) => _renderer.BeginAssetFrame(ctx.Frame.Frame);
}
