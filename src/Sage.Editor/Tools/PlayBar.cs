#nullable enable
using System;
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// Play-in-editor's part of the screen (issue #226). What playing *is* belongs to `PlaySession`
// (Sage.Editing, tested headlessly); this is the button, the key and what changes on screen.
//
// - **In the editor**: a Play button under the gizmo's toolbar, and Ctrl+P (`ed_play`). The player
//   starts on the ground below the free camera, facing its way.
// - **While playing**: the play world has the screen (`Renderer.ScreenWorld`) and the host gives it the
//   player's input; the editor's panels are not drawn, only a slim bar saying how to stop: Escape (the
//   host's Menu action), Ctrl+P or the bar's Stop button, which are all `ed_stop`. The console is closed
//   on the way in, because an open console takes the keyboard from the game; `~` opens it as in any run,
//   and it is opened again on the way out if it was open.
internal sealed class PlayBar
{
    private readonly PlaySession _session;
    private readonly Func<Renderer?> _renderer;
    private readonly DevConsoleWindow _console;
    private readonly CVarRegistry _cvars;
    private bool _consoleWasOpen;

    public PlayBar(PlaySession session, Func<Renderer?> renderer, DevConsoleWindow console, CVarRegistry cvars)
    {
        _session = session;
        _renderer = renderer;
        _console = console;
        _cvars = cvars;
        session.Started += OnStarted;
        session.Stopped += OnStopped;
    }

    public bool Playing => _session.IsPlaying;

    private void OnStarted(World world)
    {
        if (_renderer() is { } renderer) renderer.ScreenWorld = world;
        _consoleWasOpen = _console.IsOpen;
        if (_console.IsOpen) _console.Close();
    }

    private void OnStopped()
    {
        if (_renderer() is { } renderer) renderer.ScreenWorld = _session.EditWorld;
        if (_consoleWasOpen && !_console.IsOpen) _console.Toggle();
    }

    // The editor's frame: the Play button, and Ctrl+P.
    public void DrawButton()
    {
        Key();
        Begin("##sage_editor_play", 40f);
        if (ImGui.Button("Play")) Press("ed_play");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("ed_play (Ctrl+P): play the document from the free camera");
        ImGui.End();
    }

    // A playing frame: the bar and nothing else of the editor's.
    public void DrawPlaying()
    {
        Key();
        Begin("##sage_editor_playing", 6f);
        ImGui.TextUnformatted($"Playing {(_session.Document.IsOpen ? _session.Document.Title : "the scene")}");
        ImGui.SameLine();
        if (ImGui.Button("Stop")) Press("ed_stop");
        ImGui.SameLine();
        ImGui.TextDisabled("Esc, Ctrl+P or ed_stop");
        ImGui.End();
    }

    private void Key()
    {
        var io = ImGui.GetIO();
        if (!io.WantCaptureKeyboard && io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.P, false)) Press(Playing ? "ed_stop" : "ed_play");
    }

    private void Press(string command) => _cvars.Execute(command, ExecSource.Console);

    private static void Begin(string id, float top)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(viewport.WorkPos.X + viewport.WorkSize.X * 0.5f, viewport.WorkPos.Y + top), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.75f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        ImGui.Begin(id, flags);
    }
}
