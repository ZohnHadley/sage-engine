#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// The visual log's timeline (docs/design/02 §9, issue #300): `vlog_window 1` opens it. A slider over the
// ticks kept, Live to follow the newest, a checkbox per category (they write `vlog_show`), and the shown
// tick's shapes with their text — "why did it path there?" scrubbed back to the tick that decided it. The
// shapes themselves are drawn in the world through the debug lines (`r_debugdraw`). Every control is a
// console command or cvar as well (vlog_at, vlog_step, vlog_show, vlog_list).
internal sealed class VisualLogWindow
{
    private readonly CVarRegistry _cvars;
    private readonly Func<World?> _world;
    private readonly CVar<bool> _open;
    private readonly List<VisualLogEntry> _entries = new();
    private readonly List<string> _shown = new();

    public VisualLogWindow(CVarRegistry cvars, Func<World?> world)
    {
        _cvars = cvars;
        _world = world;
        _open = cvars.Register("vlog_window", false, CVarFlags.DevOnly,
            "Show the visual log's timeline: scrub through the recorded ticks (vlog_record), pick categories (issue #300).");
    }

    public void Draw()
    {
        if (!_open.Value || _world() is not { } world) return;
        var log = world.VisualLog();

        ImGui.SetNextWindowSize(new Vector2(460, 300), ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("Visual log", ref open))
        {
            if (!log.Recording)
            {
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Not recording.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Record")) _cvars.Execute("vlog_record 1", ExecSource.Console);
            }

            bool live = log.ScrubTick == null;
            if (ImGui.Checkbox("Live", ref live)) log.ScrubTick = live ? null : log.ShownTick;
            ImGui.SameLine();
            if (ImGui.ArrowButton("##back", ImGuiDir.Left)) log.ScrubTick = log.ShownTick - 1;
            ImGui.SameLine();
            if (ImGui.ArrowButton("##forward", ImGuiDir.Right)) log.ScrubTick = log.ShownTick + 1;
            ImGui.SameLine();
            ImGui.Text($"ticks {log.OldestTick}..{log.NewestTick}");

            // Ticks are longs; a window of them fits an int slider as an offset from the oldest.
            int span = (int)Math.Min(int.MaxValue, log.NewestTick - log.OldestTick);
            int at = (int)(log.ShownTick - log.OldestTick);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderInt("##tick", ref at, 0, Math.Max(span, 0), $"tick {log.ShownTick}"))
                log.ScrubTick = log.OldestTick + at;

            if (log.Categories.Count > 0)
            {
                bool changed = false;
                _shown.Clear();
                foreach (var category in log.Categories)
                {
                    bool shown = log.IsShown(category);
                    if (ImGui.Checkbox(category, ref shown)) changed = true;
                    if (shown) _shown.Add(category);
                    ImGui.SameLine();
                }
                ImGui.NewLine();
                if (changed && _cvars.Find("vlog_show") is CVar<string> show)
                    show.Value = _shown.Count == log.Categories.Count ? "*" : _shown.Count == 0 ? "-" : string.Join(' ', _shown);
            }

            ImGui.Separator();
            _entries.Clear();
            log.CollectAt(log.ShownTick, _entries);
            ImGui.Text($"{_entries.Count} shape(s) at tick {log.ShownTick}");
            if (ImGui.BeginChild("##entries"))
            {
                foreach (var e in _entries)
                {
                    if (e.Text == null) continue;   // the shapes that say something
                    string who = e.Entity.IsNull ? "" : World.Describe(e.Entity) + ": ";
                    ImGui.Text($"[{e.Category}] {who}{e.Text ?? e.Shape.ToString()}");
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
        if (!open) _open.Value = false;
    }
}
