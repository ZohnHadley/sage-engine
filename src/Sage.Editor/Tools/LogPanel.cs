#nullable enable
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// The editor's log panel (issue #219, 15 §3): the log's recent lines, filtered by level and category.
// What to show is `LogView`'s (Sage.Editing, tested headlessly); this only draws it. The console window
// beside it shows the same ring with a search box and a prompt; this one has no prompt, and a category
// list instead, because what an editor asks of a log is "what did Records and Editor just say".
internal sealed class LogPanel
{
    public const string Title = "Log";
    private static readonly string[] LevelNames = { "Trace", "Debug", "Info", "Warn", "Error", "Fatal" };

    private readonly LogView _view = new(Log.Ring);
    private bool _autoScroll = true;

    public void Draw()
    {
        if (!ImGui.Begin(Title))
        {
            ImGui.End();
            return;
        }

        int level = (int)_view.MinLevel;
        ImGui.SetNextItemWidth(90);
        if (ImGui.Combo("Level", ref level, LevelNames, LevelNames.Length)) _view.MinLevel = (LogLevel)level;
        ImGui.SameLine();
        if (ImGui.BeginCombo("##categories", "Categories", ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.Selectable("All")) _view.Only(null);
            ImGui.Separator();
            foreach (var category in _view.Categories)
            {
                bool shown = _view.IsShown(category);
                if (ImGui.Checkbox(category, ref shown)) _view.Show(category, shown);
                ImGui.SameLine();
                if (ImGui.SmallButton($"only##{category}")) _view.Only(category);
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);

        ImGui.Separator();
        ImGui.BeginChild("lines", Vector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
        bool changed = _view.Refresh();
        var lines = _view.Lines;
        // Every line, formatted each frame, as the console window does (both allocate while open, TODO #41).
        for (int i = 0; i < lines.Count; i++)
            ImGui.TextColored(DevConsoleWindow.ColorFor(lines[i].Level), LogFormatter.FormatShort(lines[i]));
        if (changed && _autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
            ImGui.SetScrollHereY(1.0f);
        ImGui.EndChild();
        ImGui.End();
    }
}
