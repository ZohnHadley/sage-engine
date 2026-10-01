#nullable enable
using System;
using System.Numerics;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The editor's prefab palette (issue #222, 15 §6): a search box and the prefabs by namespace. Clicking one
// arms it; the next left click in the viewport places it where the pointer's ray meets the world, and
// Escape disarms. What is listed and where a click lands are `PrefabPalette` and `Placing` (Sage.Editing,
// tested headlessly); this only draws and passes the click on.
internal sealed class PalettePanel
{
    public const string Title = "Palette";

    private readonly PrefabPalette _palette;
    private readonly Func<EditDocument?> _document;
    private readonly Func<Vector2, Vector2, EditorRay?> _rayThrough;   // (pointer, display size) -> the ray from the screen's camera
    private readonly Action<Entity> _onPlaced;
    private string _search = "";

    public PalettePanel(PrefabPalette palette, Func<EditDocument?> document, Func<Vector2, Vector2, EditorRay?> rayThrough, Action<Entity> onPlaced)
    {
        _palette = palette;
        _document = document;
        _rayThrough = rayThrough;
        _onPlaced = onPlaced;
    }

    public void Draw()
    {
        if (!ImGui.Begin(Title))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##search", "search prefabs", ref _search, 128)) _palette.Search = _search;

        if (_palette.IsArmed)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"Placing {_palette.Armed}: click in the viewport, Esc to cancel");
            ImGui.SameLine();
            if (ImGui.SmallButton("cancel")) _palette.Disarm();
        }
        else ImGui.TextDisabled("pick a prefab, then click in the viewport");

        ImGui.Separator();
        ImGui.BeginChild("prefabs");
        bool searching = _search.Length > 0;
        foreach (var group in _palette.Groups())
        {
            if (searching) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if (!ImGui.TreeNode($"{group.Namespace} ({group.Prefabs.Count})###{group.Namespace}")) continue;
            foreach (var prefab in group.Prefabs)
                if (ImGui.Selectable(prefab.Name, _palette.Armed == prefab))
                    _palette.Arm(_palette.Armed == prefab ? default : prefab);
            ImGui.TreePop();
        }
        ImGui.EndChild();
        ImGui.End();
    }

    // After the panels are drawn: with a prefab armed, Escape disarms and a left click that ImGui does not
    // want (the viewport is the dock space's hole) places it. Stays armed, to place another.
    public void HandleViewport()
    {
        if (!_palette.IsArmed) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { _palette.Disarm(); return; }
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if (_document() is not { IsOpen: true } document) return;
        if (_rayThrough(io.MousePos, io.DisplaySize) is not { } ray) return;
        if (Placing.PlaceAt(document, _palette.Armed, ray) is { } placed && document.EntityOf(placed) is { IsNull: false } entity)
            _onPlaced(entity);
    }
}
