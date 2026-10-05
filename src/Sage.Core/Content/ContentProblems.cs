#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sage.Core;

// One content problem the loader found: where (the "mount:path" of the file and its line, 0 when the
// message names none), what, and the record it is about when the message says so.
public sealed record ContentProblem(bool IsError, string File, int Line, string Message, RecordId Record = default)
{
    public const string NoFile = "(no file)";

    // "error mount:path:12: message", as the `problems` command prints it.
    public override string ToString() =>
        $"{(IsError ? "error" : "warning")} {File}{(Line > 0 ? ":" + Line.ToString(CultureInfo.InvariantCulture) : "")}: {Message}";
}

// What is wrong with the content, with no editor and no boot (issue #301, REDESIGN §4.2): the record
// store's last load (its errors and warnings, which carry the "file:line:col" the loader found) and the
// conflicts between mods from the content report, as warnings. These are the entries `sage validate`
// prints for the records, read from the running engine instead of a cold boot. The editor's problems
// panel (`ProblemList`) is made of the same list; the `problems` console command and the dev
// overlay's badge read it without the editor.
public static class ContentProblems
{
    // "mount:path:line:col: " (the column optional) at the start of a loader message, then maybe "type id: ".
    private static readonly Regex Where = new(@"^(?<file>\S+?\.[A-Za-z0-9]+):(?<line>\d+)(?::\d+)?: ", RegexOptions.Compiled);
    private static readonly Regex Subject = new(@"^(?<type>[a-z_][a-z_0-9]*) (?<id>[^\s:]+:[^\s:]+): ", RegexOptions.Compiled);

    // Errors first, then warnings, then mod conflicts, each in the order the loader said them.
    public static IReadOnlyList<ContentProblem> Build(RecordStore records, VirtualFileSystem? vfs)
    {
        var list = new List<ContentProblem>();
        foreach (string message in records.LoadErrors) list.Add(FromMessage(true, message));
        foreach (string message in records.LoadWarnings) list.Add(FromMessage(false, message));
        if (vfs != null)
        {
            try
            {
                foreach (var conflict in ContentReport.Build(records, vfs).Conflicts)
                    list.Add(new ContentProblem(false, conflict.Winner.Name, 0, $"conflict: {conflict.Line}", conflict.Id));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Warn(LogCat.Records, $"problems: the content report failed: {ex.Message}");
            }
        }
        return list;
    }

    // "2 errors, 1 warning", or "no problems".
    public static string Summary(int errors, int warnings) =>
        errors + warnings == 0 ? "no problems"
        : $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}";

    // The `problems` command's output: the summary, then each problem, worst and first-reported first.
    public static IReadOnlyList<string> Lines(IReadOnlyList<ContentProblem> problems)
    {
        int errors = problems.Count(p => p.IsError);
        var lines = new List<string> { Summary(errors, problems.Count - errors) + (problems.Count == 0 ? "" : ":") };
        foreach (var p in problems) lines.Add("  " + p);
        return lines;
    }

    private static ContentProblem FromMessage(bool isError, string message)
    {
        string file = ContentProblem.NoFile;
        int line = 0;
        var at = Where.Match(message);
        if (at.Success)
        {
            file = at.Groups["file"].Value;
            line = int.Parse(at.Groups["line"].Value, CultureInfo.InvariantCulture);
            message = message[at.Length..];
        }
        RecordId record = default;
        var subject = Subject.Match(message);
        if (subject.Success)
        {
            try { record = RecordId.Parse(subject.Groups["id"].Value, ""); }
            catch (FormatException) { }
        }
        return new ContentProblem(isError, file, line, message, record);
    }
}
