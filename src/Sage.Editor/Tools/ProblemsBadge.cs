#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// The dev overlay's problems badge (issue #301): a small "3 errors, 1 warning" in the top-left corner
// while the content has any, red for errors and yellow for warnings only, nothing while it is clean. It
// reads the same list as the `problems` command (ContentProblems), cached and read again when the
// records reload. `ui_problems 0` hides it.
internal sealed class ProblemsBadge
{
    private readonly Engine _engine;
    private readonly CVar<bool> _show;
    private IReadOnlyList<ContentProblem>? _problems;
    private int _errors, _warnings;
    private bool _dirty = true;

    public ProblemsBadge(Engine engine)
    {
        _engine = engine;
        _show = engine.CVars.Register("ui_problems", true, CVarFlags.DevOnly | CVarFlags.Archive,
            "Show the content problems badge (errors and warnings, top left) in dev builds; `problems` lists them (issue #301).");
        engine.Records.Reloaded += () => _dirty = true;
    }

    public void Draw()
    {
        if (!_show.Value) return;
        if (_dirty)
        {
            _dirty = false;
            _problems = ContentProblems.Build(_engine.Records, _engine.Vfs);
            _errors = _problems.Count(p => p.IsError);
            _warnings = _problems.Count - _errors;
        }
        if (_errors + _warnings == 0) return;

        ImGui.SetNextWindowPos(new Vector2(10, 30), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.7f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##problems", flags))
        {
            var color = _errors > 0 ? new Vector4(1f, 0.35f, 0.3f, 1f) : new Vector4(1f, 0.85f, 0.3f, 1f);
            ImGui.TextColored(color, $"{ContentProblems.Summary(_errors, _warnings)} (type `problems`)");
        }
        ImGui.End();
    }
}
