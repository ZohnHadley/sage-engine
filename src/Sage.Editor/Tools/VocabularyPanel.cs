#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The conditions and actions editor (issue #370): a form over the value the VocabularyEditor has open — a
// wire's `requires` (the I/O panel's "requires" button) or a record field that holds conditions, actions or
// any other vocabulary's entries (the Records panel's buttons). Each entry is a picker of what the
// vocabulary registers, its settings the widgets their metadata asks for, and a setting that holds entries
// itself (an `all`'s `of`) its own rows beneath it. What is listed, checked and written is Sage.Editing's
// VocabularyForm (tested headlessly; `ed_vocab_*` press the same buttons); this only draws.
internal sealed class VocabularyPanel
{
    public const string Title = "Conditions";
    private static readonly Vector4 ProblemColor = new(1f, 0.5f, 0.3f, 1f);

    private readonly VocabularyEditor _editor;
    private readonly Dictionary<string, string> _typing = new();   // a text box's text while it has the focus
    private string _search = "";
    private string? _error;

    public VocabularyPanel(VocabularyEditor editor)
    {
        _editor = editor;
        editor.Changed += () => _typing.Clear();
    }

    public void Draw()
    {
        if (!ImGui.Begin(Title)) { ImGui.End(); return; }
        if (_editor.Current is not { } form)
        {
            ImGui.TextWrapped("Nothing open. Press \"requires\" on a wire in the I/O panel, or a conditions field's button in the Records panel (ed_vocab_wire, ed_vocab_rec).");
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted(form.Label);
        ImGui.SameLine();
        if (ImGui.SmallButton("close")) { _editor.Close(); ImGui.End(); return; }
        if (!form.Slot.IsOpen) { ImGui.TextDisabled("no longer there"); ImGui.End(); return; }
        ImGui.Separator();

        var rows = form.Rows();
        if (form.Many) Picker(form, "", "+ add", id => form.Add("", id, out _error));
        else if (rows.Count == 0) Picker(form, "", $"(no {form.Vocabulary.Name}: choose)", id => form.Choose("", id, out _error));
        foreach (var row in rows) DrawRow(form, row);

        var problems = form.Validate();
        if (problems.Count > 0)
        {
            ImGui.Separator();
            foreach (var problem in problems) ImGui.TextColored(ProblemColor, problem.ToString());
        }
        if (_error != null) ImGui.TextColored(ProblemColor, _error);
        ImGui.End();
    }

    private void DrawRow(VocabularyForm form, VocabularyRow row)
    {
        ImGui.PushID(row.Path);
        if (row.Depth > 0) ImGui.Indent(row.Depth * 14f);
        Picker(form, row.Path, row.Id.Length > 0 ? row.Id : "(choose)", id => form.Choose(row.Path, id, out _error));
        ImGui.SameLine();
        if (ImGui.SmallButton("x")) form.Remove(row.Path, out _error);
        foreach (var problem in row.Problems) ImGui.TextColored(ProblemColor, problem);

        foreach (var setting in row.Settings)
        {
            ImGui.PushID(setting.Parameter.Name);
            if (setting.Parameter.Nested is { } nested)
            {
                // Its entries are rows of their own, below; here only a way to start one.
                if (setting.Parameter.NestedList) Picker(form, setting.Path, $"+ {setting.Parameter.Name}", id => form.Add(setting.Path, id, out _error));
                else if (!setting.IsSet) Picker(form, setting.Path, $"{setting.Parameter.Name}: (choose a {nested.Name})", id => form.Choose(setting.Path, id, out _error));
            }
            else Setting(form, row, setting);
            ImGui.PopID();
        }
        if (row.Depth > 0) ImGui.Unindent(row.Depth * 14f);
        ImGui.PopID();
    }

    // One setting, by what its field is.
    private void Setting(VocabularyForm form, VocabularyRow row, VocabularySetting setting)
    {
        var field = setting.Parameter.Field;
        string label = setting.IsSet ? setting.Parameter.Name + " *" : setting.Parameter.Name;
        ImGui.SetNextItemWidth(160);
        if (field.Kind == ValueKind.Bool)
        {
            bool on = setting.Text == "true";
            if (ImGui.Checkbox(label, ref on)) form.SetParameter(row.Path, setting.Parameter.Name, JsonValue.Create(on), out _error);
        }
        else if (field.Kind == ValueKind.Enum)
        {
            if (ImGui.BeginCombo(label, setting.Text))
            {
                foreach (var name in field.EnumValues)
                    if (ImGui.Selectable(name, string.Equals(name, setting.Text, StringComparison.OrdinalIgnoreCase)))
                        form.SetParameter(row.Path, setting.Parameter.Name, name, out _error);
                ImGui.EndCombo();
            }
        }
        else if (field.Kind == ValueKind.RecordId && field.RecordType is { Length: > 0 } type)
        {
            if (ImGui.BeginCombo(label, setting.Text.Length > 0 ? setting.Text : "(none)"))
            {
                if (ImGui.Selectable("(none)", setting.Text.Length == 0)) form.SetParameter(row.Path, setting.Parameter.Name, "", out _error);
                foreach (var id in form.Engine.Records.Ids(type).OrderBy(i => i.ToString(), StringComparer.Ordinal))
                    if (ImGui.Selectable(id.ToString(), id.ToString() == setting.Text))
                        form.SetParameter(row.Path, setting.Parameter.Name, id.ToString(), out _error);
                ImGui.EndCombo();
            }
        }
        else
        {
            // Typed, and read by the field's kind when the box lets go (a number in its range, a vector as x y z).
            string text = _typing.TryGetValue(setting.Path, out var typed) ? typed : setting.Text;
            if (ImGui.InputText(label, ref text, 256)) _typing[setting.Path] = text;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                form.SetParameter(row.Path, setting.Parameter.Name, text, out _error);
                _typing.Remove(setting.Path);
            }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(setting.Parameter.Describe());
        if (setting.IsSet)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("reset")) form.SetParameter(row.Path, setting.Parameter.Name, (JsonNode?)null, out _error);
        }
    }

    // A combo of the entries the vocabulary at `path` registers, searchable, each with its settings as a tooltip.
    private void Picker(VocabularyForm form, string path, string label, Func<string, bool> pick)
    {
        ImGui.SetNextItemWidth(200);
        if (!ImGui.BeginCombo("##pick" + path, label, ImGuiComboFlags.HeightLarge)) return;
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "search", ref _search, 64);
        foreach (var choice in form.Choices(path, _search))
        {
            if (ImGui.Selectable($"{choice.Id}  ({choice.Owner})", choice.Id == label))
            {
                pick(choice.Id);
                _search = "";
            }
            if (ImGui.IsItemHovered() && choice.Parameters.Count > 0)
                ImGui.SetTooltip(string.Join("\n", choice.Parameters.Select(p => p.Describe())));
        }
        ImGui.EndCombo();
    }
}
