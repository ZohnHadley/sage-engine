using Microsoft.Xna.Framework.Input;
using ImGuiNET;
using sage_engine;

namespace sage_engine;
class EditorUI
{   

    private static EditorUI instance = null;
    private EntityContext context;

    private EditorUI( )
    {
        context = EntityContext.getInstance( );
    }

    public static EditorUI GetInstance( )
    {
        if ( instance == null )
        {
            instance = new EditorUI( );
        }
        return instance;
    } 
 
    public void Draw( )
    {
       //create menu bar with ImGui
        ImGui.BeginMainMenuBar( );
        if ( ImGui.BeginMenu( "File" ) )
        {
            if ( ImGui.MenuItem( "New" ) )
            {
                // New file action
            }
            if ( ImGui.MenuItem( "Open" ) )
            {
                // Open file action
            }
            if ( ImGui.MenuItem( "Save" ) )
            {
                // Save file action
            }
            if ( ImGui.MenuItem( "Exit" ) )
            {
                // Exit action
                System.Environment.Exit( 0 );
            }
            ImGui.EndMenu( );
        }
        ImGui.EndMainMenuBar( );

    }
}

