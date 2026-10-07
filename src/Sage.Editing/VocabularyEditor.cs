#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Sage.Editing;

// What the conditions and actions editor has open (issue #370): at most one VocabularyForm — a record
// field's or a wire's `requires` — and the `ed_vocab*` console commands that press every button of its panel
// (phase 10a decision 6). The panel and a test both drive this.
//
// It is a source of the problems panel (IProblemSource): the open form's checks on a record's value, live,
// before the record is saved and reloaded. A wire's `requires` the problems panel checks on its own, open or not.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class VocabularyEditor : IProblemSource
{
    private readonly RecordEditor? _records;

    public VocabularyEditor(Engine engine, RecordEditor? records = null)
    {
        Engine = engine;
        _records = records;
        // A form on a record goes when the record browser closes that record or opens another.
        if (records != null) records.Changed += () =>
        {
            if (Current?.Slot is RecordSlot slot && !ReferenceEquals(slot.Document, records.Current)) Close();
        };
    }

    public Engine Engine { get; }

    public VocabularyForm? Current { get; private set; }

    // Opened, closed, or the open value changed (an edit, an undo).
    public event Action? Changed;

    public void Open(VocabularyForm form)
    {
        Close();
        Current = form;
        form.Slot.Changed += OnChanged;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (Current == null) return;
        Current.Slot.Changed -= OnChanged;
        Current = null;
        Changed?.Invoke();
    }

    private void OnChanged() => Changed?.Invoke();

    // Opens the field at `path` of the record the record browser has open.
    public bool OpenRecord(string path, [NotNullWhen(false)] out string? error)
    {
        if (_records?.Current is not { } record) { error = "no record is open (ed_rec_open <type> <id>)"; return false; }
        return OpenRecord(record, path, out error);
    }

    public bool OpenRecord(RecordDocument record, string path, [NotNullWhen(false)] out string? error)
    {
        if (VocabularyForm.ForRecord(record, path, out var why) is not { } form) { error = why ?? $"cannot open '{path}'"; return false; }
        error = null;
        Open(form);
        return true;
    }

    public bool OpenWire(EditDocument document, Placement placement, int index, [NotNullWhen(false)] out string? error)
    {
        if (VocabularyForm.ForWire(document, placement, index, out var why) is not { } form) { error = why ?? $"cannot open wire {index + 1}"; return false; }
        error = null;
        Open(form);
        return true;
    }

    // The problems panel's rows: the open record form's checks (a wire's are the document's own).
    public IReadOnlyList<Problem> Problems() =>
        Current is { Slot: RecordSlot } form ? form.Problems() : Array.Empty<Problem>();

    // The fields of the object at `path` in a record (`""` for the record itself) that hold conditions,
    // actions or another vocabulary's entries, with their paths, written or not: where the record browser
    // offers the form, including for a field the record does not write yet (a dialogue option's `conditions`).
    public static IReadOnlyList<(string Name, string Path)> FieldsAt(RecordDocument record, string path)
    {
        var type = VocabularyForm.TypeAt(record, path);
        if (type == null || Metadata.KindOf(type, out _) != ValueKind.Object) return Array.Empty<(string, string)>();
        var fields = Metadata.Of(type).Fields;
        return fields.Where(f => VocabularyCatalog.Of(record.Engine, f, out _) != null)
                     .Select(f => (f.JsonName, RecordDocument.ChildPath(path, f.JsonName))).ToList();
    }

    // ---- Console ------------------------------------------------------------------------------------

    // `.` stands for the value itself, which a console argument cannot be empty to say.
    private static string PathArg(string text) => text == "." ? "" : text;

    public void Register(CVarRegistry cvars, Func<EditDocument?> document)
    {
        cvars.RegisterCommand("ed_vocab", CVarFlags.None,
            "ed_vocab [vocabulary] [search]: the vocabularies (condition, action, quest_objective, ...), or one's entries with their settings.", a =>
        {
            if (a.Count == 0)
            {
                foreach (var v in VocabularyCatalog.All(Engine))
                    Log.Info(LogCat.Console, $"  {v.Name}  ({v.Entries.Count} entries, key \"{v.Key}\"{(v.Default != null ? $", default {v.Default}" : "")})");
                return;
            }
            if (VocabularyCatalog.Named(Engine, a[0]) is not { } vocabulary)
            {
                Log.Warn(LogCat.Console, $"ed_vocab: no vocabulary '{a[0]}'" + Spelling.Suggest(a[0], VocabularyCatalog.All(Engine).Select(v => v.Name)));
                return;
            }
            foreach (var choice in VocabularyCatalog.Entries(Engine, vocabulary, a.Count > 1 ? a[1] : ""))
            {
                Log.Info(LogCat.Console, $"  {choice.Id}  ({choice.Owner})");
                foreach (var parameter in choice.Parameters) Log.Info(LogCat.Console, $"      {parameter.Describe()}");
            }
        });

        cvars.RegisterCommand("ed_vocab_rec", CVarFlags.DevOnly,
            "ed_vocab_rec <path>: open the form on a field of the open record that holds conditions or actions (nodes[0].options[0].conditions).", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "ed_vocab_rec <path>"); return; }
            if (!OpenRecord(a[0], out var error)) { Log.Warn(LogCat.Console, $"ed_vocab_rec: {error}"); return; }
            Show();
        });

        cvars.RegisterCommand("ed_vocab_wire", CVarFlags.DevOnly,
            "ed_vocab_wire <placement> <n>: open the form on the requires of a placement's wire n (as ed_wires numbers it).", a =>
        {
            if (a.Count < 2 || !int.TryParse(a[1], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            { Log.Warn(LogCat.Console, "ed_vocab_wire <placement> <n>"); return; }
            if (document() is not { IsOpen: true } doc) { Log.Warn(LogCat.Console, "ed_vocab_wire: no document is open"); return; }
            if (doc.Find(a[0]) is not { } placement) { Log.Warn(LogCat.Console, $"ed_vocab_wire: no placement called '{a[0]}' in {doc.Id}"); return; }
            if (!OpenWire(doc, placement, n - 1, out var error)) { Log.Warn(LogCat.Console, $"ed_vocab_wire: {error}"); return; }
            Show();
        });

        cvars.RegisterCommand("ed_vocab_show", CVarFlags.None, "ed_vocab_show: the open form's entries, their settings and its problems.", _ =>
        {
            if (Form("ed_vocab_show") != null) Show();
        });

        cvars.RegisterCommand("ed_vocab_pick", CVarFlags.DevOnly,
            "ed_vocab_pick <path|.> <id>: make the entry there that one (has_item, all, not, ...); one undo.", a =>
        {
            if (Form(a.Name) is not { } form) return;
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_vocab_pick <path|.> <id>"); return; }
            Done(a.Name, form.Choose(PathArg(a[0]), a[1], out var error), error);
        });

        cvars.RegisterCommand("ed_vocab_add", CVarFlags.DevOnly,
            "ed_vocab_add <listpath|.> <id>: add an entry to a list (. for a list value, of for an all/any); one undo.", a =>
        {
            if (Form(a.Name) is not { } form) return;
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_vocab_add <listpath|.> <id>"); return; }
            Done(a.Name, form.Add(PathArg(a[0]), a[1], out var error), error);
        });

        cvars.RegisterCommand("ed_vocab_param", CVarFlags.DevOnly,
            "ed_vocab_param <path|.> <setting> [value]: set a setting of the entry there, read by its type; no value puts back its default; one undo.", a =>
        {
            if (Form(a.Name) is not { } form) return;
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_vocab_param <path|.> <setting> [value]"); return; }
            string value = string.Join(' ', a.Args.Skip(2));
            Done(a.Name, form.SetParameter(PathArg(a[0]), a[1], value, out var error), error);
        });

        cvars.RegisterCommand("ed_vocab_remove", CVarFlags.DevOnly,
            "ed_vocab_remove <path|.>: take the entry there out (. clears the value); one undo.", a =>
        {
            if (Form(a.Name) is not { } form) return;
            if (a.Count < 1) { Log.Warn(LogCat.Console, "ed_vocab_remove <path|.>"); return; }
            Done(a.Name, form.Remove(PathArg(a[0]), out var error), error);
        });

        cvars.RegisterCommand("ed_vocab_close", CVarFlags.DevOnly, "ed_vocab_close: close the form (its edits stay, in their own undo history).",
            _ => Close());
    }

    private VocabularyForm? Form(string command)
    {
        if (Current is { } form && form.Slot.IsOpen) return form;
        Log.Warn(LogCat.Console, $"{command}: no form is open (ed_vocab_rec <path>, ed_vocab_wire <placement> <n>)");
        return null;
    }

    private void Done(string command, bool ok, string? error)
    {
        if (!ok) { Log.Warn(LogCat.Console, $"{command}: {error}"); return; }
        Show();
    }

    private void Show()
    {
        if (Current == null) return;
        foreach (string line in Current.Lines()) Log.Info(LogCat.Console, line);
    }
}
