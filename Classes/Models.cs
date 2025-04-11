using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Liru3D.Models;
namespace sage_engine;

class Models{
    public static Model debug_monkey_head;

    public static void InitializeModels(ContentManager Content){
        debug_monkey_head = Content.Load<Model>("hydrahead");
    }
}