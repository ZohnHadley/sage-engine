#nullable enable
using System;
using System.Numerics;
using System.Linq;
using ImGuiNET;

namespace Sage.Editor;

// The editable inspector (docs/design/15 §3, TODO F28).
//
// A component is a **struct**, so editing one is: box it, change a field, put the box back. That is what
// `ComponentSchema.Write` is for, and it is why this cannot simply hold a reference and poke at it.
//
// The fields come from the metadata table (issue #18), not from reflection: each has its JSON name,
// its range (a drag clamps to Min/Max), its unit (shown in the number), its tooltip (on hover), its
// category (fields are grouped under it), its enum values (a dropdown) and, for a RecordId, the record
// type it names — so the widget is a dropdown of the records of that type instead of a text box. The
// setters are generated code that writes the boxed struct in place.
internal sealed class EntityInspectorWindow
{
    private readonly World _world;
    private readonly ComponentSchema _schema;
    private readonly EditorSelection _selection;
    private readonly EditDocument _document;

    public EntityInspectorWindow(World world, ComponentSchema schema, EditorSelection selection, EditDocument document)
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

        bool moved = false;
        foreach (var component in entity.Components)
        {
            // Headed by the stable id a prefab or a save would write (issue #16); Friflo's own
            // components have none and keep their type name.
            string id = _schema.IdOf(component.Type) ?? component.Type.Name;
            if (!ImGui.CollapsingHeader(id)) continue;

#pragma warning disable CS0618   // Friflo prefers GetComponent<T>(); an inspector only knows the type
            object? boxed = component.Value;                       // at run time, so it needs the box.
#pragma warning restore CS0618
            if (boxed == null) continue;

            var meta = Metadata.Of(boxed.GetType());
            bool changed = false;
            string? category = null;
            foreach (var field in meta.Fields.OrderBy(f => f.Category ?? "", StringComparer.Ordinal))
            {
                if (field.Category != category)
                {
                    category = field.Category;
                    if (category != null) ImGui.SeparatorText(category);
                }
                changed |= Field(id, field, boxed);
                if (field.Tooltip != null && ImGui.IsItemHovered())
                    ImGui.SetTooltip(field.Transient ? field.Tooltip + " (not saved)" : field.Tooltip);
            }

            if (changed)
            {
                _schema.Write(entity, component.Type, boxed);
                moved |= component.Type == typeof(Transform);
            }
        }

        // Until the inspector edits overrides (issue #223), what it changes on the entity is not kept,
        // except where the document's own placement stands: that goes back as a SetPlacement, which
        // re-spawns the entity (the selection follows it) and merges a drag's frames into one edit.
        if (moved) Capture(entity);
        if (!ImGui.IsAnyItemActive()) _document.History.EndMerge();

        ImGui.End();
    }

    private void Capture(Entity entity)
    {
        if (_document.PlacementOf(entity) is not { } placement) return;
        var record = _document.Record;
        var transform = entity.GetComponent<Transform>();
        var fields = PlacementFields.Of(placement) with
        {
            At = _world.PlacementAt(transform.LocalPosition, record.Origin, placement.RelativeTo ?? record.RelativeTo),
            Yaw = SageMath.YawOf(transform.LocalRotation) * 180f / MathF.PI + 0f,
        };
        _document.Execute(new SetPlacement(_document, placement, fields));
    }

    // One field, as whatever widget fits it. Anything this does not know how to edit is shown and left
    // alone — a read-only row is honest, and an inspector that silently refuses edits is not.
    private bool Field(string component, FieldMetadata field, object boxed)
    {
        string label = $"{field.JsonName}##{component}.{field.Name}";
        object? value = field.Get?.Invoke(boxed);
        if (!field.Editable || field.Set == null)
        {
            ImGui.TextDisabled($"{field.JsonName}: {value}");
            return false;
        }

        float min = field.Min is { } lo ? (float)lo : 0f;
        float max = field.Max is { } hi ? (float)hi : 0f;
        bool clamped = field.Min != null || field.Max != null;
        if (clamped && field.Max == null) max = float.MaxValue;
        if (clamped && field.Min == null) min = float.MinValue;
        string format = field.Unit != null ? "%.3f " + field.Unit : "%.3f";

        switch (field.Kind)
        {
            case ValueKind.Number when value is float number:
            {
                float edited = number;
                if (!ImGui.DragFloat(label, ref edited, 0.05f, min, max, format)) return false;
                field.Set(boxed, edited);
                return true;
            }
            case ValueKind.Number when value is double number:
            {
                float edited = (float)number;
                if (!ImGui.DragFloat(label, ref edited, 0.05f, min, max, format)) return false;
                field.Set(boxed, (double)edited);
                return true;
            }
            case ValueKind.Integer when value != null:
            {
                int edited = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                int iMin = field.Min is { } a ? (int)a : 0, iMax = field.Max is { } b ? (int)b : 0;
                if (clamped && field.Max == null) iMax = int.MaxValue;
                if (!ImGui.DragInt(label, ref edited, 1f, iMin, iMax, field.Unit != null ? "%d " + field.Unit : "%d")) return false;
                field.Set(boxed, Convert.ChangeType(edited, Nullable.GetUnderlyingType(field.Type) ?? field.Type,
                                                    System.Globalization.CultureInfo.InvariantCulture));
                return true;
            }
            case ValueKind.Bool when value is bool flag:
            {
                bool edited = flag;
                if (!ImGui.Checkbox(label, ref edited)) return false;
                field.Set(boxed, edited);
                return true;
            }
            case ValueKind.Vector3 when value is Vector3 vector:
            {
                var edited = vector;
                if (!ImGui.DragFloat3(label, ref edited, 0.05f, min, max, format)) return false;
                field.Set(boxed, edited);
                return true;
            }
            case ValueKind.Vector2 when value is Vector2 vector:
            {
                var edited = vector;
                if (!ImGui.DragFloat2(label, ref edited, 0.05f, min, max, format)) return false;
                field.Set(boxed, edited);
                return true;
            }
            case ValueKind.String:
            {
                string edited = value as string ?? "";
                if (!ImGui.InputText(label, ref edited, 128)) return false;
                field.Set(boxed, edited);
                return true;
            }
            case ValueKind.Enum when value is Enum choice:
            {
                var names = field.EnumValues.ToArray();
                int index = Array.IndexOf(names, choice.ToString());
                if (!ImGui.Combo(label, ref index, names, names.Length) || index < 0) return false;
                field.Set(boxed, Enum.Parse(Nullable.GetUnderlyingType(field.Type) ?? field.Type, names[index]));
                return true;
            }
            case ValueKind.RecordId when value is RecordId current:
                return RecordField(label, field, boxed, current);
            case ValueKind.RecordId when value is IRecordRef typed:   // RecordRef<T>
                return RecordField(label, field, boxed, typed.Id);
            default:
                ImGui.TextDisabled($"{field.JsonName}: {value}");
                return false;
        }
    }

    // A reference to a record: the records of the type it names, as a dropdown. One whose field does not
    // say ([RecordRef] missing) is shown and left alone, because a free-text id is how typos get in.
    private bool RecordField(string label, FieldMetadata field, object boxed, RecordId current)
    {
        if (field.RecordType == null || _world.Engine is not { } engine)
        {
            ImGui.TextDisabled($"{field.JsonName}: {current}");
            return false;
        }
        bool changed = false;
        if (ImGui.BeginCombo(label, current.IsEmpty ? "(none)" : current.ToString()))
        {
            if (ImGui.Selectable("(none)", current.IsEmpty))
            {
                field.Set!(boxed, As(field, default));
                changed = true;
            }
            foreach (var id in engine.Records.Ids(field.RecordType).OrderBy(i => i.ToString(), StringComparer.Ordinal))
            {
                if (!ImGui.Selectable(id.ToString(), id == current)) continue;
                field.Set!(boxed, As(field, id));
                changed = true;
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    // A RecordId as the field's own type: itself, or a RecordRef<T> wrapping it.
    private static object As(FieldMetadata field, RecordId id) =>
        field.Type == typeof(RecordId) ? id : Activator.CreateInstance(field.Type, id)!;
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
