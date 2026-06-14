using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ImGuiNET;
using sage_engine;

namespace sage_engine;
internal class EditorUI
{

    private static EditorUI instance = null;
    private EntityContext context;

    private EditorUI()
    {
        context = EntityContext.getInstance();
    }

    public static EditorUI GetInstance()
    {
        if (instance == null)
        {
            instance = new EditorUI();
        }
        return instance;
    }

    public void Draw(Game game)
    {
       //create menu bar with ImGui
        ImGui.BeginMainMenuBar();
        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New"))
            {
                // New file action
            }
            if (ImGui.MenuItem("Open"))
            {
                // Open file action
            }
            if (ImGui.MenuItem("Save"))
            {
                // Save file action
            }
            if (ImGui.MenuItem("Exit"))
            {
                game.Exit();
            }
            ImGui.EndMenu();
        }
        ImGui.EndMainMenuBar();

    }
}

