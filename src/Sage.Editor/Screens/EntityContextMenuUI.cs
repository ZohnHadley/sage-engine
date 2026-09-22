#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Friflo.Engine.ECS;
using ImGuiNET;

namespace sage_engine;

// Entity outliner + read-only inspector for one World. Right-click an entity to delete it.
// Uses reflection and per-entity label strings, so it allocates about 130 bytes per listed entity
// per frame while the window is open (TODO #41): fine for a dev tool, and replaced by the generated
// inspector metadata later (docs/design/09, 15). Collapsing the window costs nothing.
internal sealed class EntityContextMenuUI
{
    private readonly World _world;
    private readonly ArchetypeQuery _all;
    private readonly List<Entity> _entities = new();
    private readonly ImGuiWindowFlags _flags = ImGuiWindowFlags.AlwaysVerticalScrollbar;

    public EntityContextMenuUI(World world)
    {
        _world = world;
        _all = world.QueryAll();
    }

    public void draw()
    {
        if (!ImGui.Begin($"Entities ({_world.Name})", _flags))
        {
            ImGui.End();   // collapsed: skip the listing entirely (it allocates per entity)
            return;
        }

        // Copy first: deleting from the context menu changes the world while we draw.
        _entities.Clear();
        foreach (var e in _all.Entities)
            _entities.Add(e);

        foreach (var entity in _entities)
        {
            if (!_world.IsAlive(entity)) continue;
            ImGui.Separator();
            bool nodeOpen = ImGui.TreeNodeEx($"{World.Describe(entity)}##{entity.Id}", ImGuiTreeNodeFlags.SpanFullWidth);

            if (ImGui.BeginPopupContextItem($"ctx_{entity.Id}"))
            {
                if (ImGui.MenuItem("Delete")) _world.Destroy(entity);
                ImGui.EndPopup();
            }

            if (!nodeOpen || !_world.IsAlive(entity))
            {
                if (nodeOpen) ImGui.TreePop();
                continue;
            }

            ImGui.TextColored(new Vector4(1, 0.5f, 1, 1), $"Components: {entity.Components.Count}");
            foreach (var component in entity.Components)
            {
                ImGui.Separator();
                if (ImGui.TreeNodeEx($"{component.Type.Name}##{entity.Id}", ImGuiTreeNodeFlags.SpanFullWidth))
                {
#pragma warning disable CS0618   // Friflo marks the boxed Value obsolete in favour of GetComponent<T>(); a
                                 // reflection inspector only knows the type at runtime, so it needs the box.
                    object? value = component.Value;
#pragma warning restore CS0618
                    if (value != null)
                    {
                        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
                        foreach (var field in value.GetType().GetFields(publicInstance))
                            ImGui.TextColored(new Vector4(1, 1, 0.5f, 1), $"{field.Name}: {field.GetValue(value)}");
                        foreach (var property in value.GetType().GetProperties(publicInstance))
                        {
                            if (property.GetIndexParameters().Length > 0) continue;
                            ImGui.TextColored(new Vector4(1, 1, 0.5f, 1), $"{property.Name}: {SafeGet(property, value)}");
                        }
                    }
                    ImGui.TreePop();
                }
            }
            ImGui.TreePop();
        }
        ImGui.End();
    }

    private static string SafeGet(PropertyInfo property, object target)
    {
        try { return property.GetValue(target)?.ToString() ?? "null"; }
        catch (Exception ex) { return $"({ex.GetType().Name})"; }
    }
}
