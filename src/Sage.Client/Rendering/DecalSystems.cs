#nullable enable
using System;
using System.Numerics;
using Vector2 = Microsoft.Xna.Framework.Vector2;
using Vector4 = Microsoft.Xna.Framework.Vector4;

namespace Sage.Client;

// Decals into the snapshot (issue #306; the pool and where marks go are the simulation's: Decals,
// DecalSystem).
//
// **No new shader.** A decal is a textured, tinted quad, which the sprite path already draws: this writes
// `SpriteInstance`s laid in the surface's plane (`Right`/`Up`) instead of turned to the camera, with the
// engine's blended decal material (`sage:decal`: the sprite effect's blended technique, no depth write).
// It is lifted a little off the surface along its normal so it does not fight the wall for depth. A
// quad, not a projection onto the surface's triangles: a mark at the edge of a brush overhangs it, the
// price of drawing the same way on brushes, terrain and meshes alike.
[System("sage.client.extract.decals", Phase.Extract, After = new[] { "sage.client.extract.camera" })]
internal sealed class DecalExtract : ISystem
{
    // Metres off the surface, and a little more with distance, where depth precision runs out.
    private const float Lift = 0.006f;
    private const float LiftPerMetre = 0.0004f;

    private readonly Decals _decals;
    private readonly RenderSnapshot _snapshot;
    private readonly Renderer _renderer;
    private readonly CVar<int> _ceiling;

    public DecalExtract(World world, Renderer renderer, CVar<int> ceiling)
    {
        _decals = world.Resources.Get<Decals>();
        _snapshot = world.Resources.Get<RenderSnapshot>();
        _renderer = renderer;
        _ceiling = ceiling;
    }

    public void Run(in SystemContext ctx)
    {
        _decals.Ceiling = _ceiling.Value;   // `r_decals`: lowering it drops the oldest at once
        if (!_snapshot.HasView || _decals.Count == 0) return;

        var live = _decals.Live;
        for (int v = 0; v < _snapshot.Views.Count; v++)
        {
            var view = _snapshot.Views[v];
            if (view.ShadowCaster) continue;   // a mark casts no shadow
            var camera = view.CameraPosition.ToNumerics();
            var forward = view.Forward.ToNumerics();

            for (int i = 0; i < live.Length; i++)
            {
                ref readonly var decal = ref live[i];
                uint colour = decal.Colour;
                if ((colour >> 24) == 0) continue;   // faded out

                var relative = decal.Position - camera;
                if (Vector3.Dot(relative, decal.Normal) > 0f) continue;   // behind the surface it is on
                var centre = relative + decal.Normal * (Lift + relative.Length() * LiftPerMetre);

                int texture = _renderer.ResolveTexture(decal.Record.Texture);
                int material = _renderer.Materials.Resolve(decal.Record.Material.IsEmpty
                    ? DecalRecord.DefaultMaterial : decal.Record.Material);
                var pass = _renderer.Materials.Get(material)?.Pass ?? RenderPass.Transparent;

                // Every field, every time: `Add()` hands back last frame's slot as it was (06 §3.1).
                ref var instance = ref _snapshot.Sprites.Add();
                instance.Center = centre;
                instance.Size = new Vector2(decal.Record.Size, decal.Record.Size);
                instance.Pivot = new Vector2(0.5f, 0.5f);
                instance.Uv = new Vector4(0f, 0f, 1f, 1f);
                instance.Tint = new Vector4(((colour >> 0) & 0xFF) / 255f, ((colour >> 8) & 0xFF) / 255f,
                                            ((colour >> 16) & 0xFF) / 255f, ((colour >> 24) & 0xFF) / 255f);
                instance.Material = material;
                instance.Texture = texture;
                instance.Mode = BillboardMode.Spherical;   // unused: Right and Up are set
                instance.Roll = 0f;
                instance.Right = decal.Right;
                instance.Up = decal.Up;
                instance.SortKey = RenderSortKey.Make(pass, 0, material, texture, Vector3.Dot(centre, forward), view.Far);
                instance.View = v;
            }
        }
    }
}
