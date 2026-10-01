#nullable enable
using System;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The record browser (issue #224, docs/design/15 §10): every record type, the ids of one, and a form for
// the open record, with its raw JSON beside it and a Save.
//
// **This only draws.** What is open, every edit, undo and the save belong to Sage.Editing's RecordEditor
// and RecordDocument, which `ed_rec_open` / `ed_rec_set` / `ed_rec_save` press too; a button here calls the
// same thing a console line does.
//
// The form is drawn over the record's JSON, not over the built object: numbers, booleans, strings and
// nested objects and lists as tree nodes. Where the type's metadata (the inspector's source, #18) declares
// the field — its range, its tooltip, its enum values, the record type it refers to — the widget follows
// it; the rest are edited as what the JSON says they are. The inspector's own widgets edit a boxed
// struct's field, so they cannot be lent to a JSON value, but they read the same metadata.
internal sealed class RecordsPanel
{
    private readonly RecordEditor _editor;
    public RecordEditor Editor { get; }
    private string _type = "";
    private string _search = "";
    private string _raw = "";       // the open record's text, rebuilt when it changes
    private bool _rawStale = true;

    public RecordsPanel(RecordEditor editor)
    {
        _editor = editor;
        Editor = editor;
        editor.Changed += () => _rawStale = true;
    }

    public void Draw()
    {
        ImGui.SetNextWindowPos(new Vector2(8, 266), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(320, 360), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(EditorLayout.RecordsTitle)) { ImGui.End(); return; }

        DrawBrowser();
        ImGui.Separator();
        if (_editor.Current is { } record) DrawRecord(record);
        else ImGui.TextDisabled("Pick a record above (or ed_rec_open <type> <id>).");

        ImGui.End();
    }

    private void DrawBrowser()
    {
        var types = _editor.Types();
        if (_type.Length == 0 && types.Length > 0) _type = types.Contains("placements") ? "placements" : types[0];

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.45f);
        if (ImGui.BeginCombo("##recordtype", _type))
        {
            foreach (var type in types)
                if (ImGui.Selectable($"{type}  ({_editor.Engine.Records.Ids(type).Count()})", type == _type)) _type = type;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##recordsearch", "search", ref _search, 64);

        if (ImGui.BeginChild("##recordids", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 7), ImGuiChildFlags.Border))
        {
            foreach (var id in _editor.Ids(_type, _search))
                if (ImGui.Selectable(id.ToString(), _editor.Current is { } open && open.Type == _type && open.Id == id))
                    _editor.Open(_type, id);
        }
        ImGui.EndChild();
    }

    private void DrawRecord(RecordDocument record)
    {
        ImGui.TextUnformatted(record.Title);
        ImGui.TextDisabled(string.Join(", ", record.Sources()));

        if (ImGui.Button("Save")) record.Save();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(record.SavesAsPatch ? "Writes a patch in the game's data folder: this record is not the game's own" : "Writes the change into the file the record is in");
        ImGui.SameLine();
        ImGui.BeginDisabled(!record.History.CanUndo);
        if (ImGui.Button("Undo")) record.Undo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!record.History.CanRedo);
        if (ImGui.Button("Redo")) record.Redo();
        ImGui.EndDisabled();
        if (record.SavesAsPatch) { ImGui.SameLine(); ImGui.TextDisabled("(saves as a patch)"); }

        // Ctrl+Z is this panel's while it has the focus; the placements document has the viewport's.
        var io = ImGui.GetIO();
        if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && io.KeyCtrl && !ImGui.IsAnyItemActive())
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Z)) { if (io.KeyShift) record.Redo(); else record.Undo(); }
            else if (ImGui.IsKeyPressed(ImGuiKey.Y)) record.Redo();
            else if (ImGui.IsKeyPressed(ImGuiKey.S)) record.Save();
        }

        ImGui.Separator();
        foreach (var (name, value) in record.Working.ToArray())
            Node(record, RecordDocument.ChildPath("", name), name, value);
        if (!ImGui.IsAnyItemActive()) record.History.EndMerge();

        if (ImGui.CollapsingHeader("Raw JSON (read-only)"))
        {
            if (_rawStale) { _raw = record.RawText; _rawStale = false; }
            ImGui.InputTextMultiline("##raw", ref _raw, 1 << 16, new Vector2(-1, ImGui.GetTextLineHeight() * 14), ImGuiInputTextFlags.ReadOnly);
        }
    }

    // One value of the form: an object or a list as a tree node over its children, anything else as the
    // widget that fits it.
    private void Node(RecordDocument record, string path, string label, JsonNode? node)
    {
        ImGui.PushID(path);
        switch (node)
        {
            case JsonObject obj:
                if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    Hover(record, path);
                    foreach (var (name, child) in obj.ToArray())
                        Node(record, RecordDocument.ChildPath(path, name), name, child);
                    ImGui.TreePop();
                }
                else Hover(record, path);
                break;
            case JsonArray array:
                if (ImGui.TreeNodeEx($"{label}  [{array.Count}]"))
                {
                    Hover(record, path);
                    for (int i = 0; i < array.Count; i++) Node(record, $"{path}[{i}]", $"[{i}]", array[i]);
                    ImGui.TreePop();
                }
                else Hover(record, path);
                break;
            default:
                Leaf(record, path, label, node);
                Hover(record, path);
                break;
        }
        ImGui.PopID();
    }

    // The tooltip: what the type declares about the field, and which file wrote it.
    private static void Hover(RecordDocument record, string path)
    {
        if (!ImGui.IsItemHovered()) return;
        var meta = record.MetaAt(path);
        string text = (meta?.Tooltip is { } tip ? tip + "\n" : "") + (meta != null ? $"{meta.TypeName}\n" : "")
                    + (record.Provenance(path) is { } from ? "from " + from : "set in this editor, not saved yet");
        ImGui.SetTooltip(text);
    }

    private static void Leaf(RecordDocument record, string path, string label, JsonNode? node)
    {
        var meta = record.MetaAt(path);
        string shown = record.IsEdited(path) ? label + " *" : label;
        float min = meta?.Min is { } lo ? (float)lo : float.MinValue, max = meta?.Max is { } hi ? (float)hi : float.MaxValue;

        if (node is not JsonValue value) { ImGui.TextDisabled($"{label}: null"); return; }
        if (value.TryGetValue<bool>(out bool flag))
        {
            if (ImGui.Checkbox(shown, ref flag)) record.Set(path, JsonValue.Create(flag));
        }
        else if (value.TryGetValue<long>(out long whole))
        {
            int edited = (int)Math.Clamp(whole, int.MinValue, int.MaxValue);
            if (ImGui.DragInt(shown, ref edited, 1f, min == float.MinValue ? int.MinValue : (int)min, max == float.MaxValue ? int.MaxValue : (int)max))
                record.Set(path, JsonValue.Create(edited));
        }
        else if (value.TryGetValue<double>(out double number))
        {
            float edited = (float)number;
            if (ImGui.DragFloat(shown, ref edited, 0.05f, min, max, meta?.Unit != null ? "%.3f " + meta.Unit : "%.3f"))
                record.Set(path, JsonValue.Create((double)edited));
        }
        else if (value.TryGetValue<string>(out string? text))
        {
            text ??= "";
            if (meta is { EnumValues.Count: > 0 })
            {
                var names = meta.EnumValues.ToArray();
                int index = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                if (ImGui.Combo(shown, ref index, names, names.Length) && index >= 0) record.Set(path, JsonValue.Create(names[index]));
            }
            else if (meta?.RecordType is { } type)
            {
                if (ImGui.BeginCombo(shown, text))
                {
                    foreach (var id in record.Engine.Records.Ids(type).OrderBy(i => i.ToString(), StringComparer.Ordinal))
                        if (ImGui.Selectable(id.ToString(), id.ToString() == text)) record.Set(path, JsonValue.Create(id.ToString()));
                    ImGui.EndCombo();
                }
            }
            else if (ImGui.InputText(shown, ref text, 256)) record.Set(path, JsonValue.Create(text));
        }
        else ImGui.TextDisabled($"{label}: {value.ToJsonString()}");
    }
}
