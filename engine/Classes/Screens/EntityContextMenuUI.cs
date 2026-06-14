using System;
using System.Numerics;
using Microsoft.Xna.Framework.Input;
using ImGuiNET;
using sage_engine;

namespace sage_engine;
internal class EntityContextMenuUI
{
    private static EntityContextMenuUI instance = null;
    private EntityContext context;
    private ImGuiWindowFlags mainFlags = ImGuiWindowFlags.AlwaysVerticalScrollbar  ;
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
        
            foreach (Entity entity in context.getAllEntities())
            {
                String entityName = entity.getName() + " " + entity.getId();
                ImGui.Separator();

                bool nodeOpen = ImGui.TreeNodeEx(entityName, ImGuiTreeNodeFlags.SpanFullWidth);

                if (ImGui.BeginPopupContextItem("ctx_" + entity.getId()))
                {
                    if (ImGui.MenuItem("Delete")) { context.removeEntity(entity); }
                    ImGui.EndPopup();
                }

                if (nodeOpen)
                {
                    ImGui.TextColored(new Vector4(1, 0.5f, 1, 1), "Components: " + entity.Components.Count);
                    foreach (var (component_type, component) in entity.Components)
                    {
                        string componentName = component.GetType().ToString();
                        ImGui.Separator();
                        if (ImGui.TreeNodeEx(componentName, ImGuiTreeNodeFlags.SpanFullWidth))
                        {
                            //show all the properties of the component
                            foreach (var property in component.GetType().GetProperties())
                            {
                                ImGui.TextColored(new Vector4(1, 1, 0.5f, 1), property.Name + ": " + property.GetValue(component));
                            }
                            ImGui.TreePop();
                        }
                    }
                    ImGui.TreePop();
                }
            }
        ImGui.End();
    }
}