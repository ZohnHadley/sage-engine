using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

class ModelComponent : Component{
    public Model model {get; set;}
    public ModelComponent(Model model)
    {
        this.model = model;
    }
}