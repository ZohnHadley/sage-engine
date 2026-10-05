#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sage.Core;

// The console's line editor, minus the keys (issue #299): Up/Down history and Tab completion. It
// is headless so tests drive it; the ImGui window only forwards the key presses and shows the result.
public sealed class ConsoleInput
{
    public const int MaxHistory = 100;

    private readonly CVarRegistry _cvars;
    private readonly Func<IEnumerable<string>>? _recordIds;
    private readonly List<string> _history = new();
    private int _cursor;            // == _history.Count when not browsing
    private string _draft = "";     // what was typed before the first Up, restored by Down

    // `recordIds` lists every record id ("ns:name") for completing arguments; null = none.
    public ConsoleInput(CVarRegistry cvars, Func<IEnumerable<string>>? recordIds = null)
    {
        _cvars = cvars;
        _recordIds = recordIds;
    }

    public IReadOnlyList<string> History => _history;

    // A submitted line. Blank lines and an immediate repeat of the previous one are not kept.
    public void Submit(string line)
    {
        line = line.Trim();
        if (line.Length > 0 && (_history.Count == 0 || _history[^1] != line))
        {
            _history.Add(line);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }
        _cursor = _history.Count;
        _draft = "";
    }

    // Up: the previous line (stays on the oldest). `current` is what the box holds now.
    public string Previous(string current)
    {
        if (_history.Count == 0) return current;
        if (_cursor == _history.Count) _draft = current;
        if (_cursor > 0) _cursor--;
        return _history[_cursor];
    }

    // Down: the next line, then the draft that was being typed.
    public string Next(string current)
    {
        if (_cursor >= _history.Count) return current;
        _cursor++;
        return _cursor == _history.Count ? _draft : _history[_cursor];
    }

    // Tab. The first word completes against commands and cvars, later words against enum/bool values
    // of a cvar and record ids. `Line` is the text to put in the box (extended to the candidates' common
    // prefix, plus a space when exactly one matched a command or cvar); `Candidates` lists the matches
    // when there is more than one.
    public (string Line, IReadOnlyList<string> Candidates) Complete(string line)
    {
        int start = line.LastIndexOf(' ') + 1;
        string word = line[start..];
        string head = line[..start];
        bool first = start == 0 || line[..start].Trim().Length == 0;

        List<string> matches;
        if (first)
            matches = _cvars.Complete(word).ToList();
        else
        {
            string name = line.TrimStart().Split(' ', 2)[0];
            matches = ArgumentCandidates(name, word).ToList();
        }
        if (matches.Count == 0) return (line, Array.Empty<string>());
        if (matches.Count == 1)
            return (head + matches[0] + (first ? " " : ""), matches);

        string common = CommonPrefix(matches);
        return (head + (common.Length > word.Length ? common : word), matches);
    }

    private IEnumerable<string> ArgumentCandidates(string name, string word)
    {
        var values = Enumerable.Empty<string>();
        if (_cvars.Find(name) is { } cvar)
            values = ValueSuggestions(cvar);
        var ids = _recordIds?.Invoke() ?? Enumerable.Empty<string>();
        return values.Concat(ids)
            .Where(v => v.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ValueSuggestions(CVar cvar)
    {
        var type = cvar.GetType().GenericTypeArguments.FirstOrDefault();
        if (type == typeof(bool)) return new[] { "0", "1", "true", "false", "on", "off" };
        if (type is { IsEnum: true }) return Enum.GetNames(type);
        return Array.Empty<string>();
    }

    private static string CommonPrefix(List<string> items)
    {
        string prefix = items[0];
        foreach (string s in items)
        {
            int n = 0;
            while (n < prefix.Length && n < s.Length && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(s[n])) n++;
            prefix = prefix[..n];
        }
        return prefix;
    }
}
