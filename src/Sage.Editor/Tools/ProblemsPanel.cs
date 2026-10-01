#nullable enable
using System;
using System.Numerics;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The editor's problems panel (issue #227, 15 §10c): the content's errors and warnings, mod conflicts and
// the open document's own problems, grouped by file. What is listed is `ProblemList` (Sage.Editing,
// tested headlessly); this only draws it. A row about a placement selects it; one about a record is
// handed to `OpenRecord`, which the record browser (#224) takes.
internal sealed class ProblemsPanel : IDisposable
{
    public const string Title = "Problems";
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.35f, 1f);
    private static readonly Vector4 WarningColor = new(1f, 0.8f, 0.3f, 1f);

    private readonly ProblemList _list;
    private readonly EditorSelection? _selection;

    public ProblemsPanel(ProblemList list, EditorSelection? selection)
    {
        _list = list;
        _selection = selection;
    }

    // A record row opens the record in the record browser (DevTools sets it to RecordsPanel.OpenById).
    public Action<RecordId>? OpenRecord { get; set; }

    // "2 errors, 1 warning" for the status bar.
    public string Summary => _list.Summary;

    public void Draw()
    {
        if (!ImGui.Begin(Title))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted(_list.Summary);
        ImGui.SameLine();
        if (ImGui.SmallButton("refresh")) _list.Refresh();
        ImGui.Separator();

        ImGui.BeginChild("problems");
        foreach (var group in _list.Groups)
        {
            if (!ImGui.TreeNodeEx($"{group.File} ({group.Problems.Count})###{group.File}", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            int row = 0;
            foreach (var problem in group.Problems)
            {
                bool error = problem.Severity == ProblemSeverity.Error;
                ImGui.PushStyleColor(ImGuiCol.Text, error ? ErrorColor : WarningColor);
                string line = problem.Line > 0 ? $"{problem.Line}: " : "";
                bool clicked = ImGui.Selectable($"{(error ? "error" : "warning")}  {line}{problem.Message}##{row++}");
                ImGui.PopStyleColor();
                if (clicked) ProblemList.Activate(problem, _selection, OpenRecord);
            }
            ImGui.TreePop();
        }
        ImGui.EndChild();
        ImGui.End();
    }

    public void Dispose() => _list.Dispose();
}
