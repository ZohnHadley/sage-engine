#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Editing;

// Every panel of the editor, by its window title, and `ed_panel`, which brings them to the front (issue #371).
//
// The docked panels share tabs, and ImGui draws only the tab in front: a panel behind another is laid out
// but its contents are never drawn, so a run that only opens the editor never runs most panels' Draw.
// `ed_panel all` walks the whole list, holding each panel in front for `FramesEach` frames (a window that
// only some cvar shows is opened first), so a scripted run (CI's `-edit` smoke) draws every panel with what
// the script set up, and a panel that throws takes the host down with it. `ed_panel <title>` brings one
// forward, for a person as much as for a script.
//
// What to focus is decided here; the host focuses it (ImGui's SetWindowFocus) after its panels drew, so
// the panel is in front from the next frame. A panel the editor gains adds its title where the host builds
// this list (DevTools), and the smoke run's `ed_panel all` draws it with no other change.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class PanelTour
{
    public const int DefaultFrames = 3;

    private readonly List<(string Title, Action? Open)> _panels = new();
    private readonly Queue<int> _queue = new();
    private int _current = -1, _framesLeft, _shown, _total;
    private int _framesEach = DefaultFrames;

    // The panels' titles, in the order `ed_panel all` shows them.
    public IReadOnlyList<string> Titles => _panels.Select(p => p.Title).ToList();

    // How many frames each panel is held in front (at least 2: the frame it is focused in, and one drawn in front).
    public int FramesEach
    {
        get => _framesEach;
        set => _framesEach = Math.Max(2, value);
    }

    // A tour is under way: Next has a panel to focus.
    public bool IsTouring => _current >= 0 || _queue.Count > 0;

    // The panel in front now, or null between tours.
    public string? Current => _current >= 0 ? _panels[_current].Title : null;

    // A panel, by its window title. `open` makes a window that is not always drawn show (a cvar, a toggle).
    public void Add(string title, Action? open = null)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("a panel needs its window title", nameof(title));
        if (IndexOf(title) >= 0) throw new ArgumentException($"the panel '{title}' is already listed", nameof(title));
        _panels.Add((title, open));
    }

    // "all" queues every panel; anything else is a title (any case), or the start of exactly one.
    public bool Show(string which, out string error)
    {
        error = "";
        if (string.Equals(which, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (_panels.Count == 0) { error = "no panels"; return false; }
            Queue(Enumerable.Range(0, _panels.Count));
            return true;
        }
        int index = Find(which, out error);
        if (index < 0) return false;
        Queue(new[] { index });
        return true;
    }

    // Once a frame, after the panels drew: the title to focus now, or null when there is none. Moving on to a
    // panel opens it and logs it; the end of a tour logs how many were shown.
    public string? Next()
    {
        if (_current >= 0 && _framesLeft > 0)
        {
            _framesLeft--;
            return _panels[_current].Title;
        }
        if (_queue.Count == 0)
        {
            if (_current >= 0)
            {
                Log.Info(LogCat.Editor, $"ed_panel: showed {_shown} panel(s)");
                _current = -1;
            }
            return null;
        }

        _current = _queue.Dequeue();
        _shown++;
        _framesLeft = _framesEach - 1;
        var (title, open) = _panels[_current];
        open?.Invoke();
        Log.Info(LogCat.Editor, $"ed_panel: {title} ({_shown}/{_total})");
        return title;
    }

    private void Queue(IEnumerable<int> indices)
    {
        // A new request replaces what was left of the last one.
        _queue.Clear();
        _current = -1;
        _shown = 0;
        foreach (int i in indices) _queue.Enqueue(i);
        _total = _queue.Count;
    }

    private int IndexOf(string title) => _panels.FindIndex(p => string.Equals(p.Title, title, StringComparison.OrdinalIgnoreCase));

    private int Find(string which, out string error)
    {
        error = "";
        int exact = IndexOf(which);
        if (exact >= 0) return exact;
        var starts = Enumerable.Range(0, _panels.Count)
            .Where(i => _panels[i].Title.StartsWith(which, StringComparison.OrdinalIgnoreCase)).ToList();
        if (starts.Count == 1) return starts[0];
        error = starts.Count == 0
            ? $"no panel '{which}' (panels: {string.Join(", ", _panels.Select(p => p.Title))})"
            : $"'{which}' could be {string.Join(" or ", starts.Select(i => _panels[i].Title))}";
        return -1;
    }

    // `ed_panel [all|<title>]`: with no argument, the panels; `tour` is null outside the editor.
    public static void Register(CVarRegistry cvars, Func<PanelTour?> tour)
    {
        cvars.RegisterCommand("ed_panel", CVarFlags.DevOnly,
            "ed_panel [all|<title>]: bring an editor panel to the front, or each in turn (all); with nothing, list them.", a =>
        {
            if (tour() is not { } panels) { Log.Warn(LogCat.Console, "ed_panel: only in the editor (start the host with -edit)"); return; }
            if (a.Count == 0) { Log.Info(LogCat.Console, $"ed_panel: {string.Join(", ", panels.Titles)}"); return; }
            if (!panels.Show(a.Rest, out string error)) Log.Warn(LogCat.Console, $"ed_panel: {error}");
        });
    }
}
