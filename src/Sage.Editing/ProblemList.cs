#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Editing;

public enum ProblemSeverity
{
    Warning,
    Error,
}

// One line of the problems panel (issue #227): what is wrong, in which file, and what to select or open
// to deal with it. `Record` is set for a problem about a record (the record browser opens it);
// `Placement` for one about a placement of the open document (the selection takes it).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record Problem(ProblemSeverity Severity, string File, int Line, string Message,
    RecordId Record = default, string PlacementId = "", Placement? Placement = null);

[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed record ProblemGroup(string File, IReadOnlyList<Problem> Problems);

// Something else the problems panel lists, checked live (issue #370: the conditions form's value).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public interface IProblemSource
{
    IReadOnlyList<Problem> Problems();
    event Action? Changed;
}

// The editor's problems panel, without the drawing (issue #227, 15 §10c): what is wrong with the content
// and the open document, grouped by file.
//
// Three sources, none of which boots anything: the record store's last load (`RecordStore.LoadErrors` and
// `LoadWarnings`, which carry the "file:line:col" the loader found), the content report's conflicts
// between mods (warnings, as `mod_conflicts` says), and checks on the open document itself — a placement
// whose prefab is missing, a wire whose target is nobody in the document, two placements with one name
// or id. The first two change when records load, so they are read again on `Records.Reloaded`; the
// document's are cheap, and read again on every change to it (a command, an undo, a save).
//
// `sage validate` is the same checks run from a cold boot, which this does not repeat: it reads what the
// running engine has already found.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public sealed class ProblemList : IDisposable
{
    private readonly Engine _engine;
    private readonly EditDocument? _document;
    private readonly List<Problem> _content = new();
    private readonly List<Problem> _doc = new();
    private readonly List<Problem> _all = new();
    private readonly List<IProblemSource> _sources = new();
    private List<ProblemGroup> _groups = new();

    public ProblemList(Engine engine, EditDocument? document = null)
    {
        _engine = engine;
        _document = document;
        engine.Records.Reloaded += OnReloaded;
        if (document != null) document.Changed += OnDocumentChanged;
        Refresh();
    }

    public void Dispose()
    {
        _engine.Records.Reloaded -= OnReloaded;
        if (_document != null) _document.Changed -= OnDocumentChanged;
        foreach (var source in _sources) source.Changed -= Rebuild;
        _sources.Clear();
    }

    // Lists what `source` finds too, read again whenever it changes (issue #370).
    public void Add(IProblemSource source)
    {
        _sources.Add(source);
        source.Changed += Rebuild;
        Rebuild();
    }

    public IReadOnlyList<Problem> Problems => _all;
    public IReadOnlyList<ProblemGroup> Groups => _groups;
    public int Errors { get; private set; }
    public int Warnings { get; private set; }

    // "2 errors, 1 warning", or "no problems": the status bar's text.
    public string Summary =>
        Errors + Warnings == 0 ? "no problems"
        : $"{Errors} error{(Errors == 1 ? "" : "s")}, {Warnings} warning{(Warnings == 1 ? "" : "s")}";

    // The list changed.
    public event Action? Changed;

    // Reads everything again: the record store's last load, the content report and the document.
    public void Refresh()
    {
        RefreshContent();
        RefreshDocument();
        Rebuild();
    }

    private void OnReloaded() => Refresh();
    private void OnDocumentChanged() { RefreshDocument(); Rebuild(); }

    private void RefreshContent()
    {
        _content.Clear();
        foreach (var p in ContentProblems.Build(_engine.Records, _engine.Vfs))
            _content.Add(new Problem(p.IsError ? ProblemSeverity.Error : ProblemSeverity.Warning, p.File, p.Line, p.Message, p.Record));
    }

    private void RefreshDocument()
    {
        _doc.Clear();
        if (_document is not { IsOpen: true } doc) return;
        // Named as the loader names a file ("mount:path"), so a document's rows sit in its file's group.
        var writes = _engine.Records.Writes("placements", doc.Id);
        string file = writes.Count > 0 ? writes[0].File : doc.Path.Length > 0 ? doc.Path : $"{doc.Id} (not saved)";
        var places = doc.Placements;
        for (int i = 0; i < places.Count; i++)
        {
            var p = places[i];
            string label = p.Name.Length > 0 ? p.Name : p.Id.Length > 0 ? p.Id : $"#{i}";
            string id = p.Id.Length > 0 ? p.Id : p.Name;
            int self = i;

            if (p.Prefab.Id.IsEmpty) Add(ProblemSeverity.Error, $"placement '{label}' names no prefab");
            else if (!_engine.Records.Exists("prefab", p.Prefab.Id)) Add(ProblemSeverity.Error, $"placement '{label}': prefab {p.Prefab.Id} doesn't exist");

            if (p.Name.Length > 0 && places.Where((_, j) => j != self).Any(o => string.Equals(o.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                Add(ProblemSeverity.Warning, $"placement '{label}': another placement has the same name, so a wire to it is ambiguous");
            if (p.Id.Length > 0 && places.Where((_, j) => j != self).Any(o => string.Equals(o.Id, p.Id, StringComparison.OrdinalIgnoreCase)))
                Add(ProblemSeverity.Error, $"placement '{label}': id '{p.Id}' is used twice, so a save cannot tell them apart");

            for (int w = 0; w < p.Outputs.Count; w++)
            {
                var wire = p.Outputs[w];
                if (wire.Target.Length == 0) Add(ProblemSeverity.Warning, $"placement '{label}': a wire from {wire.Output} has no target");
                else if (wire.Target[0] != '!' && doc.Find(wire.Target) == null)
                    Add(ProblemSeverity.Warning, $"placement '{label}': wire {wire.Output} -> {wire.Target}.{wire.Input}: no placement called '{wire.Target}' in {doc.Id}");
                // What its `requires` names (issue #370): a record that is not there, a number out of range.
                if (VocabularyForm.RequiresField.Get!(wire) != null && VocabularyForm.ForWire(doc, p, w, out _) is { } form)
                    foreach (var problem in form.Validate())
                        Add(ProblemSeverity.Error, $"placement '{label}': wire {w + 1} ({wire.Output} -> {wire.Target}.{wire.Input}) requires: {problem}");
            }

            void Add(ProblemSeverity severity, string text) =>
                _doc.Add(new Problem(severity, file, 0, text, default, id, p));
        }
    }

    private void Rebuild()
    {
        _all.Clear();
        // The open document is checked live (RefreshDocument), so what the last load said about its own record
        // would only repeat that, and go stale after an edit; the live checks stand for it until it is saved
        // and the records reload.
        var open = _document is { IsOpen: true } ? _document.Id : default;
        _all.AddRange(open.IsEmpty ? _content : _content.Where(p => p.Record != open));
        _all.AddRange(_doc);
        foreach (var source in _sources) _all.AddRange(source.Problems());
        Errors = _all.Count(p => p.Severity == ProblemSeverity.Error);
        Warnings = _all.Count - Errors;
        _groups = _all.GroupBy(p => p.File, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ProblemGroup(g.Key, g.OrderBy(p => p.Line).ToList()))
            .ToList();
        Changed?.Invoke();
    }

    // What a click on a row does: a placement's is selected, a record's is handed to `openRecord` (the
    // record browser). False when the row is about neither.
    public static bool Activate(Problem problem, EditorSelection? selection, Action<RecordId>? openRecord)
    {
        if (problem.Placement != null && selection != null && selection.Document.IndexOf(problem.Placement) >= 0)
        {
            selection.Select(problem.Placement);
            return true;
        }
        if (!problem.Record.IsEmpty && openRecord != null)
        {
            openRecord(problem.Record);
            return true;
        }
        return false;
    }

    // The list as `ed_problems` prints it: each file, then its problems with their line.
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string> { Summary + (_all.Count == 0 ? "" : ":") };
        foreach (var group in _groups)
        {
            lines.Add(group.File);
            foreach (var p in group.Problems)
                lines.Add($"  {(p.Severity == ProblemSeverity.Error ? "error" : "warning")}{(p.Line > 0 ? $" line {p.Line}" : "")}: {p.Message}");
        }
        return lines;
    }
}

// `ed_problems`: the same list, typed.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class ProblemCommands
{
    public static void Register(CVarRegistry cvars, Engine engine, Func<EditDocument?> document)
    {
        cvars.RegisterCommand("ed_problems", CVarFlags.None,
            "ed_problems: the content's errors and warnings, mod conflicts and the open document's problems, by file.", _ =>
        {
            using var list = new ProblemList(engine, document());
            foreach (string line in list.Lines()) Log.Info(LogCat.Console, line);
        });
    }
}
