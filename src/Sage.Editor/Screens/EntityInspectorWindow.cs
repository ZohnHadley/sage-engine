#nullable enable
using System;
using System.Numerics;
using System.Reflection;
using Friflo.Engine.ECS;
using ImGuiNET;

namespace sage_engine;

// The editable inspector (docs/design/15 §3, TODO F28).
//
// A component is a **struct**, so editing one is: box it, change a field, put the box back. That is what
// `ComponentSchema.Write` is for, and it is why this cannot simply hold a reference and poke at it.
//
// The fields are found by reflection, which is the same stand-in the records pipeline uses until the
// source generator (09 §3.2) arrives. The generator will replace *how a field is found*, not what this
// window does with it — so the widget table below is the part worth getting right.
internal sealed class EntityInspectorWindow
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    private readonly World _world;
    private readonly ComponentSchema _schema;
    private readonly EditorSelection _selection;
    private readonly EditorDocument _document;

    public EntityInspectorWindow(World world, ComponentSchema schema, EditorSelection selection, EditorDocument document)
    {
        _world = world;
        _schema = schema;
        _selection = selection;
        _document = document;
    }

    public void Draw()
    {
        ImGui.SetNextWindowPos(new Vector2(8, 266), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(280, 138), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("Inspector"))
        {
            ImGui.End();
            return;
        }

        var entity = _selection.Entity;
        if (entity.IsNull || !_world.IsAlive(entity))
        {
            ImGui.TextDisabled("Nothing selected.");
            ImGui.End();
            return;
        }

        ImGui.Text(World.Describe(entity));
        if (entity.TryGetComponent<FromPlacements>(out var from))
            ImGui.TextDisabled($"placed by {from.Document}");
        ImGui.Separator();

        foreach (var component in entity.Components)
        {
            if (!ImGui.CollapsingHeader(component.Type.Name)) continue;

#pragma warning disable CS0618   // Friflo prefers GetComponent<T>(); an inspector only knows the type
            object? boxed = component.Value;                       // at run time, so it needs the box.
#pragma warning restore CS0618
            if (boxed == null) continue;

            bool changed = false;
            foreach (var field in boxed.GetType().GetFields(PublicInstance))
                changed |= Field(component.Type.Name, field, boxed);

            if (changed)
            {
                _schema.Write(entity, component.Type, boxed);
                _document.Touch();
            }
        }

        ImGui.End();
    }

    // One field, as whatever widget fits it. Anything this does not know how to edit is shown and left
    // alone — a read-only row is honest, and an inspector that silently refuses edits is not.
    private static bool Field(string component, FieldInfo field, object boxed)
    {
        string label = $"{field.Name}##{component}.{field.Name}";
        object? value = field.GetValue(boxed);

        switch (value)
        {
            case float number:
            {
                float edited = number;
                if (!ImGui.DragFloat(label, ref edited, 0.05f)) return false;
                field.SetValue(boxed, edited);
                return true;
            }
            case int number:
            {
                int edited = number;
                if (!ImGui.DragInt(label, ref edited)) return false;
                field.SetValue(boxed, edited);
                return true;
            }
            case bool flag:
            {
                bool edited = flag;
                if (!ImGui.Checkbox(label, ref edited)) return false;
                field.SetValue(boxed, edited);
                return true;
            }
            case Vector3 vector:
            {
                var edited = vector;
                if (!ImGui.DragFloat3(label, ref edited, 0.05f)) return false;
                field.SetValue(boxed, edited);
                return true;
            }
            case string text:
            {
                string edited = text ?? "";
                if (!ImGui.InputText(label, ref edited, 128)) return false;
                field.SetValue(boxed, edited);
                return true;
            }
            case Enum choice:
            {
                var names = Enum.GetNames(field.FieldType);
                int index = Array.IndexOf(names, choice.ToString());
                if (!ImGui.Combo(label, ref index, names, names.Length) || index < 0) return false;
                field.SetValue(boxed, Enum.Parse(field.FieldType, names[index]));
                return true;
            }
            default:
                ImGui.TextDisabled($"{field.Name}: {value}");
                return false;
        }
    }
}

// What the outliner and the inspector agree about. One entity, because multi-select wants the command
// log to be worth having (F30).
internal sealed class EditorSelection
{
    public Entity Entity { get; private set; }

    public void Select(Entity entity) => Entity = entity;
    public void Clear() => Entity = default;
    public bool Is(Entity entity) => !Entity.IsNull && Entity.Id == entity.Id;
}
