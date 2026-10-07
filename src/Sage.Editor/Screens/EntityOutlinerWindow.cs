#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using ImGuiNET;

namespace Sage.Editor;

// Entity outliner + read-only inspector for one World. Right-click an entity to delete it.
// Uses reflection and per-entity label strings, so it allocates about 130 bytes per listed entity
// per frame while the window is open (TODO #41): fine for a dev tool, and replaced by the generated
// inspector metadata later (docs/design/09, 15). Collapsing the window costs nothing.
internal sealed class EntityOutlinerWindow
{
    private readonly World _world;
    private readonly Query _all;
    private readonly List<Entity> _entities = new();
    private readonly ImGuiWindowFlags _flags = ImGuiWindowFlags.AlwaysVerticalScrollbar;
    private readonly EditorSelection? _selection;
    private readonly string? _title;   // the editor's docked "Outliner" (issue #219); else "Entities (<world>)"

    public EntityOutlinerWindow(World world, EditorSelection? selection = null, string? title = null)
    {
        _world = world;
        _title = title;
        _all = world.QueryAll();
        _selection = selection;
    }

    public void Draw()
    {
        // A place to be on the first run; ImGui remembers wherever you drag it afterwards. Without this
        // the outliner and the inspector open on top of each other, which is what an editor looks like
        // when nobody has run it.
        ImGui.SetNextWindowPos(new Vector2(8, 28), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(280, 230), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin(_title ?? $"Entities ({_world.Name})", _flags))
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

            // Selected rows are drawn as such, and clicking one selects it for the inspector (F28).
            var flags = ImGuiTreeNodeFlags.SpanFullWidth;
            if (_selection != null && _selection.Is(entity)) flags |= ImGuiTreeNodeFlags.Selected;

            bool nodeOpen = ImGui.TreeNodeEx($"{World.Describe(entity)}##{entity.Id}", flags);
            if (_selection != null && ImGui.IsItemClicked())
            {
                // Ctrl+click adds one of the document's placements to the selection, or takes it out (#367).
                if (ImGui.GetIO().KeyCtrl && _selection.Document.IsOpen && _selection.Document.PlacementOf(entity) is { } picked) _selection.Toggle(picked);
                else _selection.Select(entity);
            }

            if (ImGui.BeginPopupContextItem($"ctx_{entity.Id}"))
            {
                // One of the open document's placements goes as an edit (undoable, saved); anything else is just destroyed.
                if (ImGui.MenuItem("Delete"))
                {
                    if (_selection is { Document.IsOpen: true } s && s.Document.PlacementOf(entity) is { } placement) ViewportTools.Delete(s.Document, placement);
                    else _world.Destroy(entity);
                }
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
                // By stable id where it has one (issue #16), as the inspector and `ent_dump` show it.
                string label = _world.Engine?.Components.IdOf(component.Type) ?? component.Type.Name;
                if (ImGui.TreeNodeEx($"{label}##{entity.Id}", ImGuiTreeNodeFlags.SpanFullWidth))
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
