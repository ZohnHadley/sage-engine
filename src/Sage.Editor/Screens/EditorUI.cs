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
    private readonly EditorDocument _document;
    private readonly World _world;
    private readonly List<RecordId> _available = new();
    private readonly CVar<bool> _camFree;
    private readonly CVar<bool> _viewport;

    public EditorUI(World world, EditorDocument document, CVar<bool> camFree, CVar<bool> viewport)
    {
        _world = world;
        _document = document;
        _camFree = camFree;
        _viewport = viewport;
    }

    // The host runs the editor (`-edit`): the View menu offers its layout.
    public bool Editing { get; set; }

    public void Draw(Game game)
    {
        ImGui.BeginMainMenuBar();

        if (ImGui.BeginMenu("File"))
        {
            if (ImGui.MenuItem("New")) _document.New(_world, NamespaceOfGame());

            if (ImGui.BeginMenu("Open"))
            {
                _available.Clear();
                _available.AddRange(_document.Available());
                if (_available.Count == 0) ImGui.TextDisabled("no placements records");
                foreach (var id in _available)
                    if (ImGui.MenuItem(id.ToString())) _document.Open(_world, id);
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Save", "", false, _document.IsOpen)) _document.Save(_world);
            if (ImGui.MenuItem("Close", "", false, _document.IsOpen)) _document.Close(_world);

            ImGui.Separator();
            if (ImGui.MenuItem("Exit")) game.Exit();
            ImGui.EndMenu();
        }

        // The cameras (issue #81): each item is a cvar, so a script can press it too.
        if (ImGui.BeginMenu("View"))
        {
            if (ImGui.MenuItem("Free camera", "cam_free", _camFree.Value)) _camFree.Value = !_camFree.Value;
            if (ImGui.MenuItem("Viewport", "ed_viewport", _viewport.Value)) _viewport.Value = !_viewport.Value;
            // The editor's docked panels (`-edit`, issue #219), as the command says it.
            if (Editing && ImGui.MenuItem("Reset layout", "ed_layout")) _world.Engine!.CVars.Execute("ed_layout", ExecSource.Console);
            ImGui.EndMenu();
        }

        // What is open, on the right of the bar where a title belongs.
        ImGui.Separator();
        ImGui.TextDisabled(_document.Title);

        ImGui.EndMainMenuBar();
    }

    // A new document belongs to the game that is loaded, because that is whose content folder it will
    // be saved into.
    private string NamespaceOfGame()
    {
        foreach (var mount in _world.Engine!.Vfs.Mounts)
            if (mount.RecordNamespace != "sage") return mount.RecordNamespace;
        return "sage";
    }
}
