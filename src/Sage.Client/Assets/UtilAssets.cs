using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
namespace sage_engine;

internal class UtilAssets{
    public static Model stanfordBunny;

    public static void InitializeModels(ContentManager Content){
        stanfordBunny = Content.Load<Model>("stanford_bunny");
    }
}