#nullable enable
using System.Collections.Generic;
using ImGuiNET;
using Microsoft.Xna.Framework;

namespace Sage.Editor;

// The menu bar (docs/design/15 §3, TODO F28). One instance, owned by the host (no singleton).
//
// File → New/Open/Save act on the open placements document, which is the only document this editor has:
// levels are TrenchBroom's (15 §10a) and prefabs are records. The title says which document and whether
// it has unsaved changes, because an editor that does not is an editor you lose work in.
internal class EditorUI
{
    private readonly EditDocument _document;
    private readonly List<RecordId> _available = new();
    private readonly CVar<bool> _camFree;
    private readonly CVar<bool> _viewport;

    public EditorUI(EditDocument document, CVar<bool> camFree, CVar<bool> viewport)
    {
        _document = document;
        _camFree = camFree;
        _viewport = viewport;
    }

    public void Draw(Game game)
    {
        ImGui.BeginMainMenuBar();

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New")) _document.New();

            if (ImGui.BeginMenu("Open"))
            {
                _available.Clear();
                _available.AddRange(_document.Available());
                if (_available.Count == 0) ImGui.TextDisabled("no placements records");
                foreach (var id in _available)
                    if (ImGui.MenuItem(id.ToString())) _document.Open(id);
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Save", "", false, _document.IsOpen)) _document.Save();
            if (ImGui.MenuItem("Close", "", false, _document.IsOpen)) _document.Close();

            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) game.Exit();
            ImGui.EndMenu();
        }

        // The document's history (issue #217): ed_undo and ed_redo.
        if (ImGui.BeginMenu("Edit"))
        {
            var log = _document.History;
            if (ImGui.MenuItem(log.CanUndo ? $"Undo {log.Entries[log.Position - 1].Description}" : "Undo", "ed_undo", false, log.CanUndo)) _document.Undo();
            if (ImGui.MenuItem(log.CanRedo ? $"Redo {log.Entries[log.Position].Description}" : "Redo", "ed_redo", false, log.CanRedo)) _document.Redo();
            ImGui.EndMenu();
        }

        // The cameras (issue #81): each item is a cvar, so a script can press it too.
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Free camera", "cam_free", _camFree.Value)) _camFree.Value = !_camFree.Value;
            if (ImGui.MenuItem("Viewport", "ed_viewport", _viewport.Value)) _viewport.Value = !_viewport.Value;
            ImGui.EndMenu();
        }

        // What is open, on the right of the bar where a title belongs.
        ImGui.Separator();
        ImGui.TextDisabled(_document.Title);

        ImGui.EndMainMenuBar();
    }
}
