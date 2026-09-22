#nullable enable
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// Render phase: draws every entity with GlobalTransform + ModelRenderer using BasicEffect, at the
// pose interpolated between the last two ticks (FrameTime.Alpha), so motion is smooth at any frame
// rate. Replaced by Extract + RenderSnapshot + materials (docs/design/06, 07; TODO R9).
internal sealed class ModelRendererSystem : ISystem
{
    private readonly ArchetypeQuery<GlobalTransform, ModelRenderer> _renderables;

    public ModelRendererSystem(World world)
    {
        _renderables = world.Query<GlobalTransform, ModelRenderer>();
    }

    public void Run(in SystemContext ctx)
    {
        var view = ctx.World.Resources.Get<RenderView>();
        float alpha = ctx.Frame.Alpha;

        foreach (var (globals, renderers, _) in _renderables.Chunks)
        {
            var g = globals.Span;
            var r = renderers.Span;
            for (int n = 0; n < g.Length; n++)
            {
                Model? model = r[n].Model;
                if (model == null) continue;

                Matrix world = g[n].Interpolated(alpha).ToMatrix();   // System.Numerics → MonoGame (implicit)
                foreach (ModelMesh mesh in model.Meshes)
                {
                    foreach (BasicEffect effect in mesh.Effects)
                    {
                        effect.View = view.View;
                        effect.World = world;
                        effect.Projection = view.Projection;
                        effect.EnableDefaultLighting();
                    }
                    mesh.Draw();
                }
            }
        }
    }
}
