#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Sage.Core;

public enum ExecSource
{
    Code,        // from engine/game code: no cheat or read-only checks
    Console,     // typed by the user
    Config,      // config.cfg / exec files
    LaunchArgs,  // +cvar value on the command line
}

public readonly struct ConsoleArgs
{
    public ConsoleArgs(CVarRegistry registry, string name, IReadOnlyList<string> args, ExecSource source)
    {
        Registry = registry;
        Name = name;
        Args = args;
        Source = source;
    }

    public CVarRegistry Registry { get; }
    public string Name { get; }
    public IReadOnlyList<string> Args { get; }
    public ExecSource Source { get; }
    public int Count => Args.Count;
    public string this[int index] => Args[index];

    // All arguments joined back together (for commands like `echo` that take free text).
    public string Rest => string.Join(' ', Args);
}

public sealed class ConsoleCommand
{
    internal ConsoleCommand(string name, CVarFlags flags, string help, Action<ConsoleArgs> handler)
    {
        Name = name;
        Flags = flags;
        Help = help;
        Handler = handler;
    }

    public string Name { get; }
    public CVarFlags Flags { get; }
    public string Help { get; }
    internal Action<ConsoleArgs> Handler { get; }
}

// Console line syntax, as in Quake/Source:
//   statements separated by ';'   tokens separated by whitespace   "double quotes" group
//   // starts a comment (outside quotes)
public static class CommandLine
{
    public static List<string> SplitStatements(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') inQuotes = !inQuotes;
            if (!inQuotes && c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                // Comment: skip to the end of this line; the newline itself ends the statement.
                while (i + 1 < text.Length && text[i + 1] != '\n' && text[i + 1] != '\r') i++;
                continue;
            }
            if (!inQuotes && (c == ';' || c == '\n' || c == '\r'))
            {
                Add(result, current);
                continue;
            }
            current.Append(c);
        }
        Add(result, current);
        return result;

        static void Add(List<string> into, StringBuilder sb)
        {
            string s = sb.ToString().Trim();
            if (s.Length > 0) into.Add(s);
            sb.Clear();
        }
    }

    public static List<string> Tokenize(string statement)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false, hasToken = false;
        foreach (char c in statement)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken) tokens.Add(current.ToString());
                current.Clear();
                hasToken = false;
                continue;
            }
            current.Append(c);
            hasToken = true;
        }
        if (hasToken) tokens.Add(current.ToString());
        return tokens;
    }

    public static string Quote(string value) =>
        value.Length == 0 || value.IndexOfAny(new[] { ' ', '\t', ';', '/' }) >= 0 ? $"\"{value}\"" : value;
}

// Launch arguments (docs/design/01 §5.1 step 1):
//   +<cvar> <value> / +<command> <args>   run after config.cfg, in order
//   -<option> [value]                      host options (e.g. -game <dir>, from migration step 5)
// A '+' always starts a new command. A token starting with '-' followed by a letter starts an
// option; anything else (including negative numbers) belongs to the current command or option.
public sealed class LaunchArgs
{
    public List<string> Commands { get; } = new();
    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static LaunchArgs Parse(IReadOnlyList<string> args)
    {
        var result = new LaunchArgs();
        StringBuilder? command = null;
        string? option = null;

        foreach (string raw in args)
        {
            if (raw.StartsWith('+') && raw.Length > 1)
            {
                Finish();
                command = new StringBuilder(raw.Substring(1));
            }
            else if (raw.Length > 1 && raw[0] == '-' && (char.IsLetter(raw[1]) || raw.Length > 2 && raw[1] == '-' && char.IsLetter(raw[2])))
            {
                // `-game` as the host has always spelled it, and `--dump-registry` as tools usually do.
                Finish();
                option = raw.Substring(raw[1] == '-' ? 2 : 1);
                result.Options[option] = null;
            }
            else if (command != null)
            {
                command.Append(' ').Append(CommandLine.Quote(raw));
            }
            else if (option != null && result.Options[option] == null)
            {
                result.Options[option] = raw;
            }
            // else: a stray token; ignored (the host warns about unknown options).
        }
        Finish();
        return result;

        void Finish()
        {
            if (command != null) result.Commands.Add(command.ToString());
            command = null;
            option = null;
        }
    }
}
