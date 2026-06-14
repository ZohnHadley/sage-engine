using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;
internal class ComponentMeshRenderer : IComponent{
    public Model model {get; set;}
    public ComponentMeshRenderer(Model model)
    {
        this.model = model;
    }
}