#nullable enable
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// Draws every entity with Transform + ModelRenderer using BasicEffect. One instance per world
// (no singleton). Replaced by Extract + RenderSnapshot + materials (docs/design/06, 07; TODO R9).
internal sealed class ModelRendererSystem : IComponentSystem
{
    private readonly ArchetypeQuery<Transform, ModelRenderer> _renderables;

    public ModelRendererSystem(World world)
    {
        _renderables = world.Query<Transform, ModelRenderer>();
    }

    public void update(float deltaSeconds) { }

    public void render(Matrix view, Matrix projection)
    {
        foreach (var (transforms, renderers, _) in _renderables.Chunks)
        {
            var t = transforms.Span;
            var r = renderers.Span;
            for (int n = 0; n < t.Length; n++)
            {
                Model? model = r[n].Model;
                if (model == null) continue;

                Matrix world = t[n].LocalMatrix;   // System.Numerics → MonoGame (implicit conversion)
                foreach (ModelMesh mesh in model.Meshes)
                {
                    foreach (BasicEffect effect in mesh.Effects)
                    {
                        effect.View = view;
                        effect.World = world;
                        effect.Projection = projection;
                        effect.EnableDefaultLighting();
                    }
                    mesh.Draw();
                }
            }
        }
    }
}
