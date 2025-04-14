using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;
class ComponentMeshRenderer : Component{
    public Model model {get; set;}
    public ComponentMeshRenderer(Model model)
    {
        this.model = model;
    }
}