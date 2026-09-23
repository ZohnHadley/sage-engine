using Microsoft.Xna.Framework;
using ImGuiNET;

namespace sage_engine;
internal class EditorUI
{

    // One instance, owned by the host (no singleton).

    public void Draw(Game game)
    {
       //create menu bar with ImGui
        ImGui.BeginMainMenuBar();
        if (ImGui.BeginMenu("File"))
        {
            // New/Open/Save arrive with editor documents and the command log (15 §3, F28). There is
            // nothing to open until a document format exists, so the menu doesn't pretend otherwise.
            if (ImGui.MenuItem("Exit"))
            {
                game.Exit();
            }
            ImGui.EndMenu();
        }
        ImGui.EndMainMenuBar();

    }
}

