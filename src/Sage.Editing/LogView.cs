#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Editing;

// The editor's log panel, without the drawing (issue #219, 15 §3 "log panel (02)"): the log's recent
// lines (`Log.Ring`, or any RingBufferLogSink) filtered by level and by category, kept between frames
// and only worked out again when the ring or the filter changed. The console window shows the same ring
// with a text search; this is the panel that stays docked under the viewport, where what you want is
// "warnings from Records and Editor", not a search box.
//
// A category is shown unless it was hidden, so one a game or a mod declares later turns up on its own.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class LogView
{
    private readonly RingBufferLogSink _ring;
    private readonly List<LogEntry> _all = new();
    private readonly List<LogEntry> _shown = new();
    private readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private long _ringVersion = -1;
    private bool _filterChanged = true;
    private LogLevel _minLevel = LogLevel.Info;

    public LogView(RingBufferLogSink ring) => _ring = ring;

    // The lines that pass the filter, oldest first, as of the last Refresh.
    public IReadOnlyList<LogEntry> Lines => _shown;

    // Every category the ring held at the last Refresh, by name: what the panel offers to hide.
    public IReadOnlyCollection<string> Categories => _seen;

    public LogLevel MinLevel
    {
        get => _minLevel;
        set { if (_minLevel != value) { _minLevel = value; _filterChanged = true; } }
    }

    public bool IsShown(string category) => !_hidden.Contains(category);

    public void Show(string category, bool shown)
    {
        if (shown ? _hidden.Remove(category) : _hidden.Add(category)) _filterChanged = true;
    }

    // Shows only `category` (or every category again, given null): the one-click "what did Records say".
    // It hides the categories seen so far; one that first appears afterwards is shown.
    public void Only(string? category)
    {
        _hidden.Clear();
        if (category != null)
            foreach (var other in _seen)
                if (!string.Equals(other, category, StringComparison.OrdinalIgnoreCase)) _hidden.Add(other);
        _filterChanged = true;
    }

    // Re-reads the ring if it changed and re-filters if either changed. True when Lines did.
    public bool Refresh()
    {
        long version = _ring.Version;
        if (version == _ringVersion && !_filterChanged) return false;
        if (version != _ringVersion)
        {
            _ring.Snapshot(_all);
            _ringVersion = version;
            foreach (var entry in _all) _seen.Add(entry.Category.Name);
        }
        _filterChanged = false;

        _shown.Clear();
        foreach (var entry in _all)
            if (entry.Level >= _minLevel && !_hidden.Contains(entry.Category.Name)) _shown.Add(entry);
        return true;
    }
}
