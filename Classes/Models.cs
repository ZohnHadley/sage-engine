using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

class Models{
    public static Model debug_monkey_head;
    public static Model debug_low_poly_arms;


    public static void InitializeModels(ContentManager Content){
        debug_monkey_head = Content.Load<Model>("hydrahead");
        debug_low_poly_arms = Content.Load<Model>("low_poly_arm");
    }
}