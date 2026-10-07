#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Editing;

// What the record browser has open (issue #224): at most one RecordDocument, and the `ed_rec_*` console
// commands that press every button of the Records panel (phase 10a decision 6: a menu a script cannot press
// is a feature that cannot be checked). The panel and a test both drive this.
//
// Apart from the placements document (EditDocument): its own undo history, so Ctrl+Z in the Records panel
// takes back a field, and `ed_undo` the placement. `ed_rec_undo` / `ed_rec_redo` act on this one.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class RecordEditor
{
    public RecordEditor(Engine engine)
    {
        Engine = engine;
        engine.Records.Reloaded += () => _conflicts = null;
    }

    public Engine Engine { get; }

    // The record that is open, or null.
    public RecordDocument? Current { get; private set; }

    // Opened, closed, or the open record changed.
    public event Action? Changed;

    // Edits show in the game as they are made (RecordDocument.Live, issue #366): on in the editor, so a
    // texture picked for a material is seen on the walls at once. `ed_rec_live` turns it off and on.
    public bool Live
    {
        get => _live;
        set
        {
            _live = value;
            if (Current != null) Current.Live = value;
        }
    }
    private bool _live = true;

    public bool Open(string type, RecordId id)
    {
        var document = RecordDocument.Open(Engine, type, id);
        if (document == null) return false;
        Close();
        Current = document;
        document.Live = Live;
        document.Changed += OnChanged;
        Log.Info(LogCat.Editor, $"Opened {type} {id}");
        Changed?.Invoke();
        return true;
    }

    public void Close()
    {
        if (Current == null) return;
        Current.Revert();   // unsaved edits leave the game too
        Current.Changed -= OnChanged;
        Current = null;
        Changed?.Invoke();
    }

    private void OnChanged() => Changed?.Invoke();

    // The open record's fields that mods conflict over (issue #401): each with every mod's value and the
    // winner. Worked out when first asked after an open or a reload, not every frame; empty when no
    // record is open or no two mods wrote the same field of it.
    public IReadOnlyList<FieldConflict> Conflicts
    {
        get
        {
            if (Current == null) return Array.Empty<FieldConflict>();
            if (_conflicts == null || _conflictsFor != Current)
            {
                _conflicts = RecordConflicts.Find(Engine, Current.Type, Current.Id);
                _conflictsFor = Current;
            }
            return _conflicts;
        }
    }
    private IReadOnlyList<FieldConflict>? _conflicts;
    private RecordDocument? _conflictsFor;

    // The record types that have records, and then those that have none: what the browser lists.
    public string[] Types() => Engine.Records.TypeNames.ToArray();

    // The ids of a type, sorted, optionally only those that contain `search` (ignoring case).
    public RecordId[] Ids(string type, string search = "") =>
        Engine.Records.Ids(type)
            .Where(id => search.Length == 0 || id.ToString().Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();

    // ---- Console ------------------------------------------------------------------------------------

    public void Register(CVarRegistry cvars)
    {
        cvars.RegisterCommand("ed_rec_open", CVarFlags.DevOnly,
            "ed_rec_open <type> <id>: open a record in the record browser (see rec_list).", a =>
        {
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_rec_open <type> <id>   (rec_list names the types and ids)"); return; }
            var id = Engine.Records.Resolve(a[0], a[1]);
            if (!id.IsEmpty) Open(a[0], id);
        });

        cvars.RegisterCommand("ed_rec_set", CVarFlags.DevOnly,
            "ed_rec_set <path> <value>: set a field of the open record: a JSON literal (120, true, \"Bob\", [1,2,3]) or a bare word, which is a string.", a =>
        {
            if (!HasRecord(out var record)) return;
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_rec_set <path> <value>   e.g. ed_rec_set stats.health 120"); return; }
            // Joined back: `[1, 2, 3]` arrives as three tokens.
            string text = string.Join(' ', Enumerable.Range(1, a.Count - 1).Select(i => a[i]));
            if (record.Set(a[0], RecordDocument.ParseValue(text)))
                Log.Info(LogCat.Console, $"{a[0]} = {record.Get(a[0])?.ToJsonString() ?? "null"}");
        });

        cvars.RegisterCommand("ed_rec_get", CVarFlags.None,
            "ed_rec_get [path]: a field of the open record (or all of it), and the file that wrote it.", a =>
        {
            if (!HasRecord(out var record)) return;
            if (a.Count == 0) { Log.Info(LogCat.Console, $"{record.Title}\n{record.RawText}"); return; }
            var value = record.Get(a[0]);
            if (value == null && RecordPath.Parse(a[0]) is not { Count: > 0 }) { Log.Warn(LogCat.Console, $"'{a[0]}' is not a path"); return; }
            Log.Info(LogCat.Console, $"{a[0]} = {value?.ToJsonString() ?? "(not set)"}   <- {record.Provenance(a[0]) ?? "no file"}");
        });

        cvars.RegisterCommand("ed_rec_save", CVarFlags.DevOnly,
            "ed_rec_save: write the open record into its file, or a patch in the game's data when the file is not the game's.",
            _ => { if (HasRecord(out var record)) record.Save(); });

        cvars.RegisterCommand("ed_rec_undo", CVarFlags.DevOnly, "ed_rec_undo [count]: undo the open record's last edit (or that many).",
            a => Step(a, undo: true));
        cvars.RegisterCommand("ed_rec_redo", CVarFlags.DevOnly, "ed_rec_redo [count]: redo what was undone (or that many).",
            a => Step(a, undo: false));

        cvars.RegisterCommand("ed_rec_live", CVarFlags.DevOnly,
            "ed_rec_live [0|1]: show the open record's edits in the game as they are made, before a save (on by default).", a =>
        {
            if (a.Count > 0) Live = a[0] is "1" or "on" or "true" or "yes";
            Log.Info(LogCat.Console, $"ed_rec_live {(Live ? 1 : 0)}" + (Current is { PreviewError.Length: > 0 } open ? $" (not shown: {open.PreviewError})" : ""));
        });

        cvars.RegisterCommand("ed_rec_conflicts", CVarFlags.None,
            "ed_rec_conflicts: the open record's fields that two or more mods wrote, each mod's value, and which one won.", _ =>
        {
            if (!HasRecord(out var record)) return;
            var conflicts = Conflicts;
            Log.Info(LogCat.Console, conflicts.Count == 0
                ? $"{record.Type} {record.Id}: no conflicts between mods"
                : $"{record.Type} {record.Id}: {conflicts.Count} field(s) mods conflict over (the later mod wins; * is the write that stands):");
            foreach (var conflict in conflicts)
                foreach (var line in RecordConflicts.Lines(conflict)) Log.Info(LogCat.Console, "  " + line);
        });

        cvars.RegisterCommand("ed_rec_close", CVarFlags.DevOnly, "ed_rec_close: close the open record (unsaved edits are dropped).", _ => Close());
    }

    private void Step(ConsoleArgs a, bool undo)
    {
        if (!HasRecord(out var record)) return;
        int count = a.Count > 0 && int.TryParse(a[0], out int n) && n > 0 ? n : 1;
        for (int i = 0; i < count; i++)
        {
            string what = undo
                ? (record.History.CanUndo ? record.History.Entries[record.History.Position - 1].Description : "")
                : (record.History.CanRedo ? record.History.Entries[record.History.Position].Description : "");
            if (!(undo ? record.Undo() : record.Redo()))
            {
                Log.Info(LogCat.Console, undo ? "nothing to undo" : "nothing to redo");
                return;
            }
            Log.Info(LogCat.Console, $"{(undo ? "undid" : "redid")}: {what}");
        }
    }

    private bool HasRecord(out RecordDocument record)
    {
        record = Current!;
        if (Current != null) return true;
        Log.Warn(LogCat.Console, "no record open: ed_rec_open <type> <id>");
        return false;
    }
}
