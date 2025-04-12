using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

class ModelComponent : Component{
    public Model model {get; set;}
    public Vector3 modelPosition {get; set;}
    public ModelComponent(Model model, Vector3 position)
    {
        this.model = model;
        this.modelPosition = position;
    }
}