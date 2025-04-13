
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input; 
using MonoGame.ImGuiNet;

namespace sage_engine;
class EditorUI
{   

    private static EditorUI instance = null;
    public static ImGuiRenderer GuiRenderer;
    private EditorUI( )
    {
    }

    public static EditorUI GetInstance( )
    {
        if ( instance == null )
        {
            instance = new EditorUI( );
        }
        return instance;
    } 
 
 
}

