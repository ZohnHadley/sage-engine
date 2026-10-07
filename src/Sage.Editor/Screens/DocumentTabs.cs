#nullable enable
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;

namespace Sage.Editor;

// The document tabs under the menu bar (issue #375). What a tab *is* — a document, its world and its undo
// history — is EditorWorkspace's (Sage.Editing, tested headlessly); this draws one tab per document, the
// active one selected, a dot on one with unsaved changes, and a "+" that opens a document in a new tab or
// picks which folder saves go into (a mod's, `ed_mod`). Every action is the console command too.
internal sealed class DocumentTabs
{
    private readonly EditorWorkspace _workspace;
    private readonly List<RecordId> _available = new();
    private int _shown = -1;   // the tab ImGui has selected, as far as this knows

    public DocumentTabs(EditorWorkspace workspace)
    {
        _workspace = workspace;
    }

    // Inside the editor's dock host, above the dock space (EditorLayout.BeginFrame's header).
    public void Draw() => DrawTabs();

    private void DrawTabs()
    {
        if (!ImGui.BeginTabBar("documents", ImGuiTabBarFlags.FittingPolicyScroll)) return;

        var documents = _workspace.Documents;
        int active = _workspace.ActiveIndex;
        bool forcing = active != _shown;   // a command or a new tab changed the active one: show it
        int selected = -1, close = -1;
        for (int i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            // The id after ### is the tab's place, so a document opened into it keeps the tab.
            string label = $"{(document.IsOpen ? document.Id.ToString() : "(empty)")}{(document.SavesAsPatch ? " (patch)" : "")}###doc{i}";
            var tabFlags = ImGuiTabItemFlags.None;
            if (document.Dirty) tabFlags |= ImGuiTabItemFlags.UnsavedDocument;
            if (forcing && i == active) tabFlags |= ImGuiTabItemFlags.SetSelected;
            bool open = true;
            if (ImGui.BeginTabItem(label, ref open, tabFlags))
            {
                selected = i;
                ImGui.EndTabItem();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{document.Title}: {EditorWorkspace.Describe(document)}  [{document.World.Name}]");
            if (!open) close = i;
        }

        if (ImGui.TabItemButton("+", ImGuiTabItemFlags.Trailing | ImGuiTabItemFlags.NoTooltip)) ImGui.OpenPopup("open_tab");
        DrawOpenPopup();
        ImGui.EndTabBar();

        if (forcing)
        {
            if (selected == active) _shown = active;
        }
        else if (selected >= 0 && selected != active)
        {
            _workspace.Activate(selected);
            _shown = selected;
        }
        if (close >= 0) _workspace.Close(close);   // one with unsaved changes stays, and the log says so
    }

    private void DrawOpenPopup()
    {
        if (!ImGui.BeginPopup("open_tab")) return;
        ImGui.TextDisabled("Open in a new tab (ed_tab_open)");
        _available.Clear();
        _available.AddRange(_workspace.Engine.Records.Ids("placements"));
        if (_available.Count == 0) ImGui.TextDisabled("no placements records");
        foreach (var id in _available)
            if (ImGui.MenuItem(id.ToString(), "", _workspace.IndexOf(id) >= 0)) _workspace.Open(id);
        if (ImGui.MenuItem("New document", "ed_tab_new")) _workspace.Engine.CVars.Execute("ed_tab_new", ExecSource.Console);

        ImGui.Separator();
        ImGui.TextDisabled("Save into (ed_mod)");
        if (ImGui.MenuItem("each document's own file", "", _workspace.Target == null)) _workspace.Engine.CVars.Execute("ed_mod -", ExecSource.Console);
        foreach (var mod in _workspace.Mods())
            if (ImGui.MenuItem($"the mod '{mod.RecordNamespace}'", "", ReferenceEquals(_workspace.Target, mod)))
                _workspace.Engine.CVars.Execute($"ed_mod {mod.RecordNamespace}", ExecSource.Console);
        ImGui.EndPopup();
    }
}
