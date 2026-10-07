#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Sage.Editing;

// The tabs' console commands (issue #375): every tab a menu or a click opens, a script opens the same way.
// The document commands (`doc_*`, `ed_undo`, EditorCommands) act on the active tab's document.
//
//   ed_tabs                       the tabs, the active one marked, and where each saves
//   ed_tab <n|id>                 make tab n (1 is the first) or the tab with that document active
//   ed_tab_open <id>              open a placements document in a tab of its own (or switch to its tab)
//   ed_tab_new [id]               a new tab with an empty document
//   ed_tab_close [n] [!]          close the active tab (or tab n); `!` closes it with unsaved changes
//   ed_mod [id|-]                 save into that mod only (a game's level saves as a patch there); `-` stops
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class WorkspaceCommands
{
    public static void Register(CVarRegistry cvars, Func<EditorWorkspace?> workspace)
    {
        cvars.RegisterCommand("ed_tabs", CVarFlags.DevOnly, "ed_tabs: the open documents' tabs, the active one marked, and where each saves.", _ =>
        {
            if (Current(workspace, out var w)) Log.Info(LogCat.Console, Tabs(w));
        });

        cvars.RegisterCommand("ed_tab", CVarFlags.DevOnly, "ed_tab <n|id>: make tab n (1 is the first), or the tab with that document, active.", a =>
        {
            if (!Current(workspace, out var w)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ed_tab <n|id>   (see ed_tabs)"); return; }
            int index = int.TryParse(a[0], out int n) ? n - 1 : IndexOf(w, a[0]);
            if (!w.Activate(index)) { Log.Warn(LogCat.Console, $"ed_tab: no tab '{a[0]}' (see ed_tabs)"); return; }
            Log.Info(LogCat.Console, $"tab {index + 1}: {w.Active!.Title}");
        });

        cvars.RegisterCommand("ed_tab_open", CVarFlags.DevOnly, "ed_tab_open <id>: open a placements document in a tab of its own, or switch to its tab.", a =>
        {
            if (!Current(workspace, out var w)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ed_tab_open <id>   (see rec_list placements)"); return; }
            var id = w.Engine.Records.Resolve("placements", a[0]);
            if (!id.IsEmpty && w.Open(id) != null) Log.Info(LogCat.Console, $"tab {w.ActiveIndex + 1}: {w.Active!.Title}");
        });

        cvars.RegisterCommand("ed_tab_new", CVarFlags.DevOnly, "ed_tab_new [id]: a new tab with an empty placements document.", a =>
        {
            if (!Current(workspace, out var w)) return;
            string ns = w.Target?.RecordNamespace ?? EditDocument.GameNamespace(w.Engine);
            var document = w.New(a.Count > 0 ? RecordId.Parse(a[0], ns) : default);
            Log.Info(LogCat.Console, $"tab {w.ActiveIndex + 1}: {document.Title}");
        });

        cvars.RegisterCommand("ed_tab_close", CVarFlags.DevOnly, "ed_tab_close [n] [!]: close the active tab, or tab n; '!' closes it with unsaved changes.", a =>
        {
            if (!Current(workspace, out var w)) return;
            int index = w.ActiveIndex;
            bool force = false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] == "!") force = true;
                else if (int.TryParse(a[i], out int n)) index = n - 1;
                else index = IndexOf(w, a[i]);
            }
            if (index < 0 || index >= w.Count) { Log.Warn(LogCat.Console, "ed_tab_close: no such tab (see ed_tabs)"); return; }
            if (w.Close(index, force)) Log.Info(LogCat.Console, $"closed tab {index + 1}");
        });

        cvars.RegisterCommand("ed_mod", CVarFlags.DevOnly,
            "ed_mod [id|-]: save into that loaded mod only, a game's own level as a patch there; '-' saves each document into its own file again.", a =>
        {
            if (!Current(workspace, out var w)) return;
            if (a.Count == 0)
            {
                Log.Info(LogCat.Console, w.Target == null ? "saving each document into its own file (ed_mod <id> to save into a mod)"
                                                          : $"saving into the mod '{w.Target.RecordNamespace}' ({w.Target.Name})");
                return;
            }
            if (!w.SetTarget(a[0] == "-" ? null : a[0], out string error)) Log.Warn(LogCat.Console, $"ed_mod: {error}");
        });
    }

    // What ed_tabs prints: `>` at the active tab.
    public static string Tabs(EditorWorkspace workspace)
    {
        var text = new StringBuilder($"{workspace.Count} tab(s)");
        text.Append(workspace.Target == null ? ", each saving into its own file" : $", saving into the mod '{workspace.Target.RecordNamespace}'");
        var documents = workspace.Documents;
        for (int i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            text.Append('\n').Append(i == workspace.ActiveIndex ? "> " : "  ").Append(i + 1).Append(". ").Append(document.Title)
                .Append("  [").Append(document.World.Name).Append("]  ").Append(EditorWorkspace.Describe(document));
        }
        return text.ToString();
    }

    private static int IndexOf(EditorWorkspace workspace, string text)
    {
        var id = workspace.Engine.Records.Resolve("placements", text);
        return id.IsEmpty ? -1 : workspace.IndexOf(id);
    }

    private static bool Current(Func<EditorWorkspace?> workspace, [NotNullWhen(true)] out EditorWorkspace? w)
    {
        w = workspace();
        if (w != null) return true;
        Log.Warn(LogCat.Console, "no world yet: the editor's tabs work once a world exists");
        return false;
    }
}
