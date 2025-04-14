using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
namespace sage_engine;

class GameAssets{
    public static Model standforBunny;
    public static Texture2D lightIconTexture;

    public static void InitializeModels(ContentManager Content){
        standforBunny = Content.Load<Model>("stanford_bunny");
        lightIconTexture = Content.Load<Texture2D>("light");
    }
}