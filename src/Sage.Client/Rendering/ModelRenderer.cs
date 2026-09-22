#nullable enable
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// Draws a MonoGame Model at the entity's Transform. Client-only because it holds a MonoGame type;
// it becomes MeshRenderer { AssetPath Mesh; RecordId Material } in Sage.Engine once assets and
// materials exist (docs/design/03 §10, 06).
internal struct ModelRenderer : IComponent
{
    public Model? Model;
}
