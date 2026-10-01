#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Sage.Editing;

// The document's console commands (issue #217): every menu item is a command as well, so a script or a
// test presses the editor the way a person does (phase 10a decision 6).
//
// Registered once, by whatever holds the editor (DevTools in the host, a test on a built engine), with a
// way to find the document they act on now: a document belongs to a world, and the world changes.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class EditorCommands
{
    public static void Register(CVarRegistry cvars, Func<EditDocument?> document)
    {
        cvars.RegisterCommand("doc_new", CVarFlags.DevOnly, "doc_new [id]: start an empty placements document.", a =>
        {
            if (!Current(document, out var doc)) return;
            doc.New(a.Count > 0 ? RecordId.Parse(a[0], EditDocument.GameNamespace(doc.Engine)) : default);
        });

        cvars.RegisterCommand("doc_level", CVarFlags.DevOnly,
            "doc_level <scene>: start a new level: a scene naming a new placements document, both written on the first save.", a =>
        {
            if (!Current(document, out var doc)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, "doc_level <scene>"); return; }
            doc.NewLevel(RecordId.Parse(a[0], EditDocument.GameNamespace(doc.Engine)));
        });

        cvars.RegisterCommand("doc_open", CVarFlags.DevOnly, "doc_open <id>: open a placements document.", a =>
        {
            if (!Current(document, out var doc)) return;
            if (a.Count == 0) { Log.Warn(LogCat.Console, "doc_open <id>   (see rec_list placements)"); return; }
            var id = doc.Engine.Records.Resolve("placements", a[0]);
            if (!id.IsEmpty) doc.Open(id);
        });

        cvars.RegisterCommand("doc_save", CVarFlags.DevOnly, "doc_save: write the open document back to its file.",
            _ => { if (Current(document, out var doc)) doc.Save(); });

        cvars.RegisterCommand("doc_close", CVarFlags.DevOnly, "doc_close: close the document, removing what it placed.",
            _ => { if (Current(document, out var doc)) doc.Close(); });

        cvars.RegisterCommand("doc_status", CVarFlags.None, "doc_status: what is open, and whether it is saved.",
            _ => Log.Info(LogCat.Console, document() is { IsOpen: true } doc
                ? $"{doc.Title} — {(doc.Path.Length > 0 ? doc.Path : "never saved")}"
                : "no document open"));

        cvars.RegisterCommand("ed_undo", CVarFlags.DevOnly, "ed_undo [count]: undo the document's last edit (or that many).",
            a => Step(document, a, undo: true));

        cvars.RegisterCommand("ed_redo", CVarFlags.DevOnly, "ed_redo [count]: redo what was undone (or that many).",
            a => Step(document, a, undo: false));

        cvars.RegisterCommand("ed_history", CVarFlags.DevOnly, "ed_history: the document's edits, oldest first, and which are undone.", _ =>
        {
            if (!Current(document, out var doc)) return;
            Log.Info(LogCat.Console, History(doc));
        });
    }

    // The log as ed_history prints it: `>` at the last done edit, `(undone)` after it, and where it was saved.
    public static string History(EditDocument document)
    {
        var log = document.History;
        if (!document.IsOpen) return "no document open";
        var text = new StringBuilder($"{document.Title}: {log.Position} of {log.Entries.Count} edit(s) done");
        if (log.SavedPosition == 0) text.Append(", saved before the first");
        else if (log.SavedPosition > 0) text.Append($", saved after {log.SavedPosition}");
        for (int i = 0; i < log.Entries.Count; i++)
        {
            text.Append('\n').Append(i == log.Position - 1 ? "> " : "  ").Append(i + 1).Append(". ").Append(log.Entries[i].Description);
            if (i >= log.Position) text.Append("  (undone)");
            if (i == log.SavedPosition - 1) text.Append("  [saved]");
        }
        return text.ToString();
    }

    private static void Step(Func<EditDocument?> document, ConsoleArgs a, bool undo)
    {
        if (!Current(document, out var doc)) return;
        int count = a.Count > 0 && int.TryParse(a[0], out int n) && n > 0 ? n : 1;
        var log = doc.History;
        for (int i = 0; i < count; i++)
        {
            string what = undo
                ? (log.CanUndo ? log.Entries[log.Position - 1].Description : "")
                : (log.CanRedo ? log.Entries[log.Position].Description : "");
            if (!(undo ? doc.Undo() : doc.Redo()))
            {
                Log.Info(LogCat.Console, undo ? "nothing to undo" : "nothing to redo");
                return;
            }
            Log.Info(LogCat.Console, $"{(undo ? "undid" : "redid")}: {what}");
        }
    }

    private static bool Current(Func<EditDocument?> document, [NotNullWhen(true)] out EditDocument? doc)
    {
        doc = document();
        if (doc != null) return true;
        Log.Warn(LogCat.Console, "no world yet: the editor's document commands work once a world exists");
        return false;
    }
}
