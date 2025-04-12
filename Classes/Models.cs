using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
namespace sage_engine;

class Models{
    public static Model standforBunny;

    public static void InitializeModels(ContentManager Content){
        standforBunny = Content.Load<Model>("stanford_bunny");
    }
}