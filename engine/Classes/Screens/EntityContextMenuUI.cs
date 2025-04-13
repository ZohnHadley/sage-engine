using System;
using System.Numerics;
using ImGuiNET; 
using sage_engine;

class EntityContextMenuUI
{
    private static EntityContextMenuUI instance = null;
    private EntityContext context;
    private ImGuiWindowFlags mainFlags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysVerticalScrollbar;
    private EntityContextMenuUI()
    {
        context = EntityContext.getInstance();
    }

    public static EntityContextMenuUI getInstance()
    {
        if (instance == null)
        {
            instance = new EntityContextMenuUI();
        }
        return instance;
    }

    public void draw(){
        ImGui.Begin("EntityContextMenu", mainFlags);
            ImGuiIOPtr io = ImGui.GetIO();
        
            foreach (Entity entity in context.getAllEntities())
            {
                String entityName = entity.getName() + " " + entity.getId();
                ImGui.Separator();
                if (ImGui.TreeNodeEx(entityName))
                {
                    ImGui.Text("ID: " + entity.getId());
                    
                    Vector3 position = new Vector3(entity.transform().position.X, entity.transform().position.Y, entity.transform().position.Z);
                    ImGui.TextColored(new Vector4(1, 1, 0, 1), "Position: " + position.ToString());
                    ImGui.InputFloat3("", ref position, "%.3f");
                    entity.transform().position = position;

                  
                    ImGui.TextColored(new Vector4(1, 0, 1, 1), "Components: ");
                    foreach (Component component in entity.getComponents())
                    {
                        ImGui.TextColored(new Vector4(0, 1, 1, 1), "-"+component.GetType().Name);
                    }
                    ImGui.TreePop();

                }
           
            }
        ImGui.End();
    }
}