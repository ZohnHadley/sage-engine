#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// The developer console (docs/design/02 §4.2, 01 §3.2), drawn with ImGui. Opened with `~`.
// Available in dev builds always, and in Shipping only when con_enable is 1. The Shipping console is
// meant to become a simple drop-down drawn with the game UI (docs/design/13); until the game UI exists,
// Shipping reuses this window.
//
// Shows the in-memory log (Log.Ring) with level/category/text filters, and runs typed commands.
internal sealed class DevConsoleWindow
{
    private static readonly string[] LevelNames = { "Trace", "Debug", "Info", "Warn", "Error", "Fatal" };

    private readonly CVarRegistry _cvars;
    private readonly CoreCVars _core;
    private readonly List<LogEntry> _entries = new(2000);
    private long _seenVersion = -1;
    private string _input = "";
    private string _categoryFilter = "";
    private string _textFilter = "";
    private bool _autoScroll = true;
    private bool _focusInput;

    public bool IsOpen { get; private set; }

    public DevConsoleWindow(CVarRegistry cvars, CoreCVars core)
    {
        _cvars = cvars;
        _core = core;
        cvars.RegisterCommand("clear", CVarFlags.None, "Clear the console window.", _ => Log.Ring.Clear());
        cvars.RegisterCommand("toggleconsole", CVarFlags.None, "Open or close the console.", _ => Toggle());
    }

    public void Toggle()
    {
        if (!IsOpen && !_core.ConsoleAvailable) return;
        IsOpen = !IsOpen;
        _focusInput = IsOpen;
    }

    public void Close() => IsOpen = false;

    public void Draw()
    {
        if (!IsOpen) return;
        if (!_core.ConsoleAvailable) { IsOpen = false; return; }

        var io = ImGui.GetIO();
        ImGui.SetNextWindowPos(new Vector2(0, 20), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(io.DisplaySize.X, io.DisplaySize.Y * 0.45f), ImGuiCond.FirstUseEver);
        bool open = true;
        if (!ImGui.Begin("Console", ref open))
        {
            ImGui.End();
            IsOpen = open;
            return;
        }
        IsOpen = open;

        DrawFilters();
        ImGui.Separator();
        DrawLog();
        ImGui.Separator();
        DrawInput();

        ImGui.End();
    }

    private void DrawFilters()
    {
        int level = (int)_core.LogConsoleLevel.Value;
        ImGui.SetNextItemWidth(90);
        if (ImGui.Combo("Level", ref level, LevelNames, LevelNames.Length))
            _core.LogConsoleLevel.Value = (LogLevel)level;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Category", ref _categoryFilter, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Search", ref _textFilter, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Auto-scroll", ref _autoScroll);
        ImGui.SameLine();
        if (ImGui.Button("Clear")) Log.Ring.Clear();
    }

    private void DrawLog()
    {
        float inputHeight = ImGui.GetFrameHeightWithSpacing() + 4;
        ImGui.BeginChild("log", new Vector2(0, -inputHeight), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);

        long version = Log.Ring.Version;
        bool changed = version != _seenVersion;
        if (changed)
        {
            Log.Ring.Snapshot(_entries);
            _seenVersion = version;
        }

        LogLevel min = _core.LogConsoleLevel.Value;
        foreach (var e in _entries)
        {
            if (e.Level < min) continue;
            if (_categoryFilter.Length > 0 && !e.Category.Name.Contains(_categoryFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (_textFilter.Length > 0 && !e.Message.Contains(_textFilter, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.TextColored(ColorFor(e.Level), LogFormatter.FormatShort(e));
        }

        if (changed && _autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
            ImGui.SetScrollHereY(1.0f);
        ImGui.EndChild();
    }

    private void DrawInput()
    {
        ImGui.SetNextItemWidth(-1);
        if (_focusInput)
        {
            ImGui.SetKeyboardFocusHere();
            _focusInput = false;
        }
        if (ImGui.InputText("##input", ref _input, 512, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            string line = _input.Trim();
            _input = "";
            if (line.Length > 0)
            {
                Log.Info(LogCat.Console, $"> {line}");
                _autoScroll = true;
                _cvars.Execute(line, ExecSource.Console);
            }
            _focusInput = true;   // keep typing after Enter
        }
    }

    private static Vector4 ColorFor(LogLevel level) => level switch
    {
        LogLevel.Trace => new Vector4(0.55f, 0.55f, 0.55f, 1),
        LogLevel.Debug => new Vector4(0.75f, 0.75f, 0.80f, 1),
        LogLevel.Info => new Vector4(0.95f, 0.95f, 0.95f, 1),
        LogLevel.Warn => new Vector4(1.00f, 0.85f, 0.30f, 1),
        LogLevel.Error => new Vector4(1.00f, 0.40f, 0.35f, 1),
        _ => new Vector4(1.00f, 0.30f, 1.00f, 1),
    };
}
