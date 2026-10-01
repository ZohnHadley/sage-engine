#nullable enable
using System;
using System.Numerics;
using System.Linq;
using ImGuiNET;

namespace Sage.Editor;

// The editable inspector (docs/design/15 §3, TODO F28; issue #223 for the document).
//
// **On the document, an edit is an override.** For an entity the open document placed, what is shown is
// Sage.Editing's `InspectorModel`: the placement's own fields (at, yaw, name, frame) as a form, then each
// component the prefab names and each part, one row per field, and a change is a `SetOverride` the
// document saves (a drag's frames merge into one undo); an overridden field is marked and has a revert
// button (`ClearOverride`), and hovering a field says who set it (the prefab's file and line, a mod's
// patch, or this placement). What the prefab does not name — a part's collider, the transform — is shown
// and left alone.
//
// **Anything else is edited live, and says so.** An entity the game spawned, a prefab's child, or anything
// in a world with no document open has no placement to write to; its components are edited in place, as
// this window always did, under a "not saved" note — a developer tweaking a running game still can.
//
// A component is a **struct**, so a live edit is: box it, change a field, put the box back
// (`ComponentSchema.Write`). The fields come from the metadata table (issue #18), not from reflection:
// each has its JSON name, its range (a drag clamps to Min/Max), its unit, its tooltip, its category, its
// enum values (a dropdown) and, for a RecordId, the record type it names (a dropdown of those records).
internal sealed class EntityInspectorWindow
{
    private static readonly Vector4 OverriddenColour = new(1f, 0.78f, 0.3f, 1f);

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
        var model = InspectorModel.Of(_document, _world, entity);
        if (model.FromDocument) DrawDocument(model);
        else DrawLive(entity);

        // A drag is one edit: its frames merge until nothing is held (the transform path does the same).
        if (!ImGui.IsAnyItemActive()) _document.History.EndMerge();

        ImGui.End();
    }

    // ---- On the document ---------------------------------------------------------------------------

    private void DrawDocument(InspectorModel model)
    {
        var placement = model.Placement!;
        ImGui.TextDisabled($"placement of {_document.Id}: edits are saved with it");
        ImGui.Separator();
        DrawPlacement(placement);

        foreach (var group in model.Groups)
        {
            string title = group.Rows.Any(r => r.Overridden) ? $"{group} *" : group.ToString();
            if (!ImGui.CollapsingHeader($"{title}###{group.Section}.{group.Key}")) continue;
            if (group.Section == null && group.Note != null) ImGui.TextDisabled(group.Note);

            string? category = null;
            foreach (var row in group.Rows.OrderBy(r => r.Field.Category ?? "", StringComparer.Ordinal))
            {
                if (row.Field.Category != category)
                {
                    category = row.Field.Category;
                    if (category != null) ImGui.SeparatorText(category);
                }
                DrawRow(model, row);
            }
        }
    }

    private void DrawRow(InspectorModel model, InspectorRow row)
    {
        string label = $"{row.Field.JsonName}##{row.Group.Section}.{row.Path}";
        if (row.Overridden) ImGui.PushStyleColor(ImGuiCol.Text, OverriddenColour);
        bool changed = row.Editable ? Widget(label, row.Field, row.Value, out object? edited) : ReadOnly(row.Field, row.Value, out edited);
        if (row.Overridden) ImGui.PopStyleColor();

        if (ImGui.IsItemHovered())
        {
            string tip = row.Field.Tooltip ?? row.Field.TypeName;
            if (row.Provenance.Length > 0) tip += $"\nset by {row.Provenance}";
            ImGui.SetTooltip(tip);
        }
        if (changed && !model.Set(row, model.ToNode(edited), out string error) && error.Length > 0)
            Log.Warn(LogCat.Editor, error);

        if (row.Overridden)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"revert##{row.Group.Section}.{row.Path}") && !model.Revert(row, out string why))
                Log.Warn(LogCat.Editor, why);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Back to the prefab's value");
        }
    }

    // The placement's own fields: a SetPlacement each, merged over a drag.
    private void DrawPlacement(Placement placement)
    {
        var fields = PlacementFields.Of(placement);
        var at = fields.At;
        float yaw = fields.Yaw;
        string name = fields.Name;
        string[] frames = { "(document)", nameof(PlacementFrame.World), nameof(PlacementFrame.Origin), nameof(PlacementFrame.Ground) };
        int frame = fields.RelativeTo is { } f ? (int)f + 1 : 0;

        bool changed = false;
        changed |= ImGui.DragFloat3("at##placement", ref at, 0.05f, 0f, 0f, "%.3f m");
        changed |= ImGui.DragFloat("yaw##placement", ref yaw, 0.5f, 0f, 0f, "%.1f deg");
        changed |= ImGui.InputText("name##placement", ref name, 128);
        changed |= ImGui.Combo("relativeTo##placement", ref frame, frames, frames.Length);
        if (!changed) return;
        _document.Execute(new SetPlacement(_document, placement, new PlacementFields(
            at, yaw, name, frame == 0 ? null : (PlacementFrame)(frame - 1))));
    }

    // ---- Live -------------------------------------------------------------------------------------------

    private void DrawLive(Entity entity)
    {
        if (entity.TryGetComponent<FromPlacements>(out var from))
            ImGui.TextDisabled($"placed by {from.Document}, which is not the open document");
        ImGui.TextDisabled("Edited live: not saved.");
        ImGui.Separator();

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
                object? value = field.Get?.Invoke(boxed);
                string label = $"{field.JsonName}##{id}.{field.Name}";
                bool edited = field.Editable && field.Set != null
                    ? Widget(label, field, value, out object? now) : ReadOnly(field, value, out now);
                if (edited)
                {
                    field.Set!(boxed, now);
                    changed = true;
                }
                if (field.Tooltip != null && ImGui.IsItemHovered())
                    ImGui.SetTooltip(field.Transient ? field.Tooltip + " (not saved)" : field.Tooltip);
            }

            if (changed) _schema.Write(entity, component.Type, boxed);
        }
    }

    // ---- Widgets --------------------------------------------------------------------------------------

    private static bool ReadOnly(FieldMetadata field, object? value, out object? edited)
    {
        ImGui.TextDisabled($"{field.JsonName}: {InspectorValue.Format(value)}");
        edited = null;
        return false;
    }

    // One field, as whatever widget fits it; `edited` is the new value, of the field's own type. Anything
    // this does not know how to edit is shown and left alone — a read-only row is honest, and an inspector
    // that silently refuses edits is not.
    private bool Widget(string label, FieldMetadata field, object? value, out object? edited)
    {
        edited = null;
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
                if (!ImGui.DragFloat(label, ref number, 0.05f, min, max, format)) return false;
                edited = number;
                return true;
            }
            case ValueKind.Number when value is double number:
            {
                float f = (float)number;
                if (!ImGui.DragFloat(label, ref f, 0.05f, min, max, format)) return false;
                edited = (double)f;
                return true;
            }
            case ValueKind.Integer when value != null:
            {
                int whole = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                int iMin = field.Min is { } a ? (int)a : 0, iMax = field.Max is { } b ? (int)b : 0;
                if (clamped && field.Max == null) iMax = int.MaxValue;
                if (!ImGui.DragInt(label, ref whole, 1f, iMin, iMax, field.Unit != null ? "%d " + field.Unit : "%d")) return false;
                edited = Convert.ChangeType(whole, Nullable.GetUnderlyingType(field.Type) ?? field.Type,
                                            System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            case ValueKind.Bool when value is bool flag:
            {
                if (!ImGui.Checkbox(label, ref flag)) return false;
                edited = flag;
                return true;
            }
            case ValueKind.Vector3 when value is Vector3 vector:
            {
                if (!ImGui.DragFloat3(label, ref vector, 0.05f, min, max, format)) return false;
                edited = vector;
                return true;
            }
            case ValueKind.Vector2 when value is Vector2 vector:
            {
                if (!ImGui.DragFloat2(label, ref vector, 0.05f, min, max, format)) return false;
                edited = vector;
                return true;
            }
            case ValueKind.String:
            {
                string text = value as string ?? "";
                if (!ImGui.InputText(label, ref text, 128)) return false;
                edited = text;
                return true;
            }
            case ValueKind.Enum when value is Enum choice:
            {
                var names = field.EnumValues.ToArray();
                int index = Array.IndexOf(names, choice.ToString());
                if (!ImGui.Combo(label, ref index, names, names.Length) || index < 0) return false;
                edited = Enum.Parse(Nullable.GetUnderlyingType(field.Type) ?? field.Type, names[index]);
                return true;
            }
            case ValueKind.RecordId when value is RecordId current:
                return RecordField(label, field, current, out edited);
            case ValueKind.RecordId when value is IRecordRef typed:   // RecordRef<T>
                return RecordField(label, field, typed.Id, out edited);
            default:
                return ReadOnly(field, value, out edited);
        }
    }

    // A reference to a record: the records of the type it names, as a dropdown. One whose field does not
    // say ([RecordRef] missing) is shown and left alone, because a free-text id is how typos get in.
    private bool RecordField(string label, FieldMetadata field, RecordId current, out object? edited)
    {
        edited = null;
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
                edited = As(field, default);
                changed = true;
            }
            foreach (var id in engine.Records.Ids(field.RecordType).OrderBy(i => i.ToString(), StringComparer.Ordinal))
            {
                if (!ImGui.Selectable(id.ToString(), id == current)) continue;
                edited = As(field, id);
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
