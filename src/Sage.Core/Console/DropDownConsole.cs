#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Core;

// The keys the drop-down console reads besides typed characters. The client maps the keyboard to these;
// a test presses them directly.
public enum ConsoleNavKey { Up, Down, Left, Right, Home, End, Tab, PageUp, PageDown }

// The Shipping console (issue #353; docs/design/13 "the Shipping console is a drop-down in the game UI's
// font renderer"): the model of a console that slides down over the game, headless so a test drives it.
// It is what Dear ImGui's DevConsoleWindow is to a dev build, without ImGui: a scrollback of the log
// (Log.Ring, filtered by log_console_level, scrolled with PageUp/PageDown), one input line with a caret,
// Up/Down history and Tab completion (ConsoleInput, #299). The client draws it; nothing here knows a font.
//
// It is available when CoreCVars.ConsoleAvailable says so (a dev build, or con_enable 1 in Shipping), and
// not otherwise: Toggle does nothing while it is off, and turning con_enable off closes an open console.
// Typing is characters (08 §3.1), as for the widget text field; the key that toggles it (` or ~) is never
// typed into the line.
public sealed class DropDownConsole
{
    // Seconds the console takes to drop down or roll up.
    public const float SlideSeconds = 0.18f;

    public const int MaxLine = 512;

    private readonly CVarRegistry _cvars;
    private readonly CoreCVars _core;
    private readonly ConsoleInput _input;
    private readonly Func<bool> _available;
    private readonly List<LogEntry> _entries = new(2000);
    private readonly List<string> _lines = new(2000);
    private readonly List<LogLevel> _levels = new(2000);   // _entries shown, formatted; rebuilt when the log or the level moved
    private long _seenVersion = -1;
    private LogLevel _seenLevel;
    private string _line = "";
    private int _caret;
    private int _scroll;   // lines scrolled back from the newest; 0 follows the log

    // `available`: whether the console may open; default CoreCVars.ConsoleAvailable. A test passes
    // `() => core.ConsoleEnabled.Value` to see a Shipping build's rule, which a dev-build test run is not.
    public DropDownConsole(CVarRegistry cvars, CoreCVars core, Func<IEnumerable<string>>? recordIds = null, Func<bool>? available = null)
    {
        _available = available ?? (() => core.ConsoleAvailable);
        _cvars = cvars;
        _core = core;
        _input = new ConsoleInput(cvars, recordIds);
    }

    // Open or opening: the client gives it the keyboard. Closing keeps Slide above zero until it has rolled up.
    public bool IsOpen { get; private set; }

    // How far down it is, 0 (hidden) to 1 (fully down), eased by Update.
    public float Slide { get; private set; }

    // Something to draw: open, or still rolling up.
    public bool IsVisible => Slide > 0f;

    // The line being typed and where the caret is in it.
    public string Line => _line;
    public int Caret => _caret;

    // Candidates of the last Tab that matched more than one: the line is extended to their common prefix
    // and they are written to the log, as the dev console does; this holds them for the draw.
    public IReadOnlyList<string> Candidates { get; private set; } = Array.Empty<string>();

    public ConsoleInput Input => _input;

    // The scrollback as shown, oldest first, after Refresh.
    public IReadOnlyList<string> Lines => _lines;

    // The level of each of Lines, for colouring.
    public IReadOnlyList<LogLevel> Levels => _levels;

    // Lines scrolled back from the newest (0 = following).
    public int Scroll => _scroll;

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (IsOpen || !_available()) return;
        IsOpen = true;
        _scroll = 0;
    }

    public void Close() => IsOpen = false;

    // `clear` and `toggleconsole`, the two commands the dev console has; the host registers them when it
    // uses this console in place of that one (a command is registered once).
    public void RegisterCommands()
    {
        _cvars.RegisterCommand("clear", CVarFlags.None, "Clear the console window.", _ => { Log.Ring.Clear(); _scroll = 0; });
        _cvars.RegisterCommand("toggleconsole", CVarFlags.None, "Open or close the console.", _ => Toggle());
    }

    // Eases the slide and closes it if the console stopped being available (con_enable 0). Call once a frame.
    public void Update(float seconds)
    {
        if (IsOpen && !_available()) IsOpen = false;
        float step = SlideSeconds <= 0f ? 1f : Math.Max(seconds, 0f) / SlideSeconds;
        Slide = IsOpen ? Math.Min(1f, Slide + step) : Math.Max(0f, Slide - step);
    }

    // Refills Lines from the log when it or log_console_level changed. Allocates only when there is news.
    public void Refresh()
    {
        long version = Log.Ring.Version;
        var level = _core.LogConsoleLevel.Value;
        if (version == _seenVersion && level == _seenLevel) return;
        int before = _lines.Count;
        _seenVersion = version;
        _seenLevel = level;
        Log.Ring.Snapshot(_entries);
        _lines.Clear();
        _levels.Clear();
        foreach (var e in _entries)
            if (e.Level >= level) { _lines.Add(LogFormatter.FormatShort(e)); _levels.Add(e.Level); }
        // A scrolled-back view stays on the same lines as new ones arrive.
        if (_scroll > 0) _scroll = Math.Min(_scroll + Math.Max(_lines.Count - before, 0), Math.Max(_lines.Count - 1, 0));
    }

    // Characters typed this frame. '\b' deletes before the caret, DEL after it, Enter submits; the
    // console's own key and other control characters are not text.
    public void Type(ReadOnlySpan<char> typed)
    {
        if (!IsOpen) return;
        foreach (char c in typed)
        {
            if (c == '\b') { if (_caret > 0) { _line = _line.Remove(_caret - 1, 1); _caret--; } }
            else if (c == '\u007f') { if (_caret < _line.Length) _line = _line.Remove(_caret, 1); }
            else if (c is '\r' or '\n') Submit();
            else if (c is '`' or '~' || char.IsControl(c)) continue;
            else if (_line.Length < MaxLine) { _line = _line.Insert(_caret, c.ToString()); _caret++; }
        }
    }

    public void Press(ConsoleNavKey key)
    {
        if (!IsOpen) return;
        switch (key)
        {
            case ConsoleNavKey.Up: SetLine(_input.Previous(_line)); break;
            case ConsoleNavKey.Down: SetLine(_input.Next(_line)); break;
            case ConsoleNavKey.Left: _caret = Math.Max(_caret - 1, 0); break;
            case ConsoleNavKey.Right: _caret = Math.Min(_caret + 1, _line.Length); break;
            case ConsoleNavKey.Home: _caret = 0; break;
            case ConsoleNavKey.End: _caret = _line.Length; break;
            case ConsoleNavKey.PageUp: ScrollBy(PageLines); break;
            case ConsoleNavKey.PageDown: ScrollBy(-PageLines); break;
            case ConsoleNavKey.Tab: Complete(); break;
        }
    }

    // How many lines a PageUp moves; the client sets it to what fits on screen.
    public int PageLines { get; set; } = 10;

    // Scrolls back (positive) or forward, clamped to the log; the wheel uses it too.
    public void ScrollBy(int lines) => _scroll = Math.Clamp(_scroll + lines, 0, Math.Max(_lines.Count - 1, 0));

    // Runs the line: it is echoed to the log (so it shows in the scrollback), kept in the history and
    // executed as the console's own source.
    public void Submit()
    {
        string line = _line.Trim();
        _input.Submit(line);
        SetLine("");
        Candidates = Array.Empty<string>();
        _scroll = 0;
        if (line.Length == 0) return;
        Log.Info(LogCat.Console, $"> {line}");
        _cvars.Execute(line, ExecSource.Console);
    }

    private void Complete()
    {
        var (completed, candidates) = _input.Complete(_line);
        Candidates = candidates;
        if (candidates.Count > 1) Log.Info(LogCat.Console, string.Join("  ", candidates));
        SetLine(completed);
    }

    private void SetLine(string line)
    {
        _line = line.Length > MaxLine ? line[..MaxLine] : line;
        _caret = _line.Length;
    }
}
