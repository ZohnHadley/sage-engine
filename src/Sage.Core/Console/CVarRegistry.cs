#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sage.Core;

// All cvars and console commands (docs/design/02-core-services-and-logging.md §4.2). One registry
// per process, created by the host and passed in (it moves onto the Engine object in migration step 3).
// Console output goes to the log under LogCat.Console.
public sealed class CVarRegistry
{
    private readonly Dictionary<string, CVar> _cvars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConsoleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    private CVar<bool>? _cheats;

    // Statements waiting on a `wait` (02 §4.2). A console script is only a script once it can pace
    // itself: "walk forward, wait two seconds, swing, wait, screenshot" is the shape of every
    // automated check, and without this each command would have to grow its own delay argument
    // (which is what `screenshot <delay>` was).
    private readonly List<(string Statement, ExecSource Source)> _pending = new();
    private bool _waiting;        // a `wait` is holding the queue; seconds may be 0 ("next frame")
    private float _wait;          // seconds before the next pending statement runs
    private bool _draining;       // inside Drain: a nested exec belongs *next*, not last

    public IEnumerable<CVar> CVars => _cvars.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    public IEnumerable<ConsoleCommand> Commands => _commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public bool CheatsEnabled => _cheats?.Value ?? false;

    // Closed once config.cfg has been read (SageApp.Configure): a cvar registered after that would
    // never see its saved value. Commands stay open — a late command misses nothing.
    public RegistrationSeal CVarSeal { get; } = new("cvar", "its saved value in config.cfg was never applied");

    // Who registered each cvar and command (issue #12); set by the Engine.
    public RegistrationLedger? Ledger { get; set; }

    // ---- Registration ----------------------------------------------------------------------------

    public CVar<T> Register<T>(string name, T defaultValue, CVarFlags flags, string help) where T : notnull
        => Add(new CVar<T>(name, defaultValue, flags, help));

    public CVar<int> Register(string name, int defaultValue, CVarFlags flags, string help, int min, int max)
        => Add(new CVar<int>(name, defaultValue, flags, help, hasRange: true, min, max));

    public CVar<float> Register(string name, float defaultValue, CVarFlags flags, string help, float min, float max)
        => Add(new CVar<float>(name, defaultValue, flags, help, hasRange: true, min, max));

    public void RegisterCommand(string name, CVarFlags flags, string help, Action<ConsoleArgs> handler)
    {
        if (flags.HasFlag(CVarFlags.DevOnly) && !BuildInfo.IsDevBuild)
            return;   // DevOnly commands don't exist in Shipping
        CheckNameFree(name);
        _commands[name] = new ConsoleCommand(name, flags, help, handler);
        Ledger?.Record("command", name);
    }

    // DevOnly cvars in Shipping still work from code (they keep their default value) but aren't
    // registered: the console can't see or change them.
    private CVar<T> Add<T>(CVar<T> cvar) where T : notnull
    {
        if (cvar.Flags.HasFlag(CVarFlags.DevOnly) && !BuildInfo.IsDevBuild)
            return cvar;
        CVarSeal.Check(cvar.Name);
        CheckNameFree(cvar.Name);
        _cvars[cvar.Name] = cvar;
        Ledger?.Record("cvar", cvar.Name);
        if (cvar.Name.Equals("sv_cheats", StringComparison.OrdinalIgnoreCase) && cvar is CVar<bool> cheats)
            _cheats = cheats;
        return cvar;
    }

    private void CheckNameFree(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace))
            throw new ArgumentException($"Invalid cvar/command name '{name}'.", nameof(name));
        if (_cvars.ContainsKey(name) || _commands.ContainsKey(name))
            throw new InvalidOperationException($"A cvar or command named '{name}' is already registered.");
    }

    public CVar? Find(string name) => _cvars.TryGetValue(name, out var c) ? c : null;
    public ConsoleCommand? FindCommand(string name) => _commands.TryGetValue(name, out var c) ? c : null;

    // Names starting with `prefix`, for console completion.
    public IEnumerable<string> Complete(string prefix) =>
        _cvars.Keys.Concat(_commands.Keys)
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    // ---- Execution -------------------------------------------------------------------------------

    // Runs one or more statements ("a 1; b 2"). Returns false if any statement failed.
    // Runs `text`, which may be several statements. Anything after a `wait` is queued and run later
    // by Pump, so the return value means "everything up to the first wait succeeded".
    public bool Execute(string text, ExecSource source = ExecSource.Console)
    {
        var statements = CommandLine.SplitStatements(text).ToList();
        if (statements.Count == 0) return true;

        // A script that execs another script expects the inner one to run *here*, not after the rest
        // of the outer one — so a nested call goes to the front of the queue.
        var queued = statements.Select(s => (s, source));
        if (_draining) _pending.InsertRange(0, queued);
        else _pending.AddRange(queued);

        return Drain();
    }

    // True while a script is paced out over frames, so the host knows to keep pumping.
    public bool HasPendingStatements => _pending.Count > 0;

    // Called once a frame by the host. Ages the current `wait` and runs whatever is due.
    public void Pump(float dt)
    {
        if (_waiting)
        {
            _wait -= dt;
            if (_wait > 0f) return;
            _waiting = false;
            _wait = 0f;
        }
        if (_pending.Count > 0) Drain();
    }

    // `wait` with no argument means "next frame", the way Quake's does: the common case is letting
    // one tick happen between two commands. Zero seconds is still a wait — it is cleared by the next
    // Pump, not by the rest of this statement list, or `wait` on its own would do nothing at all.
    public void Wait(float seconds)
    {
        _waiting = true;
        _wait = MathF.Max(seconds, 0f);
    }

    private bool Drain()
    {
        if (_draining) return true;      // re-entered from a command: it already queued its work
        _draining = true;
        bool ok = true;
        try
        {
            while (!_waiting && _pending.Count > 0)
            {
                var (statement, source) = _pending[0];
                _pending.RemoveAt(0);
                ok &= ExecuteStatement(statement, source);
            }
        }
        finally
        {
            _draining = false;
        }
        return ok;
    }

    // Drops anything still queued: `wait` in a config file that never finishes should not outlive
    // the thing that started it, and a person typing at the console wants a way out.
    public void ClearPending()
    {
        _pending.Clear();
        _waiting = false;
        _wait = 0f;
    }

    private bool ExecuteStatement(string statement, ExecSource source)
    {
        var tokens = CommandLine.Tokenize(statement);
        if (tokens.Count == 0) return true;
        string name = tokens[0];
        var args = tokens.GetRange(1, tokens.Count - 1);

        if (_commands.TryGetValue(name, out var command))
        {
            if (!Allowed(command.Name, command.Flags, source)) return false;
            try
            {
                command.Handler(new ConsoleArgs(this, command.Name, args, source));
                return true;
            }
            catch (SageFatalException) { throw; }
            catch (Exception ex) when (!IsDeliberateCrash(command))
            {
                Log.Error(LogCat.Console, $"{command.Name}: {ex.Message}");
                return false;
            }
        }

        if (_cvars.TryGetValue(name, out var cvar))
        {
            if (args.Count == 0)
            {
                Log.Info(LogCat.Console, Describe(cvar));
                return true;
            }
            if (!Allowed(cvar.Name, cvar.Flags, source)) return false;
            if (cvar.Flags.HasFlag(CVarFlags.ReadOnly) && source != ExecSource.Code)
            {
                Log.Warn(LogCat.Console, $"{cvar.Name} is read-only");
                return false;
            }
            if (!cvar.TrySet(string.Join(' ', args), out string? error))
            {
                Log.Warn(LogCat.Console, error!);
                return false;
            }
            return true;
        }

        Log.Warn(LogCat.Console, $"Unknown command or cvar: {name}");
        return false;
    }

    private bool Allowed(string name, CVarFlags flags, ExecSource source)
    {
        if (source == ExecSource.Code || !flags.HasFlag(CVarFlags.Cheat) || CheatsEnabled)
            return true;
        Log.Warn(LogCat.Console, $"{name} is a cheat; set sv_cheats 1 first");
        return false;
    }

    // The `crash` command must actually crash (it tests the crash reporter).
    private static bool IsDeliberateCrash(ConsoleCommand command) => command.Name == "crash";

    // Runs a config/script file: one or more statements per line, // comments allowed.
    public bool ExecFile(string path, ExecSource source = ExecSource.Config)
    {
        if (!File.Exists(path))
        {
            Log.Warn(LogCat.Console, $"exec: {path} not found");
            return false;
        }
        Log.Debug(LogCat.Console, $"exec {path}");
        return Execute(File.ReadAllText(path), source);
    }

    // Writes every Archive cvar as `name "value"` (like Source's config.cfg). Written to a temp file
    // first, then moved into place, so a crash mid-write never corrupts the existing config.
    public void SaveArchived(string path)
    {
        var sb = new StringBuilder();
        sb.Append("// Sage config: Archive cvars, written on shutdown. Edit while the game isn't running.\n");
        foreach (var cvar in CVars.Where(c => c.Flags.HasFlag(CVarFlags.Archive)))
            sb.Append(cvar.Name).Append(" \"").Append(cvar.ValueString).Append("\"\n");

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string temp = path + ".tmp";
        File.WriteAllText(temp, sb.ToString());
        File.Move(temp, path, overwrite: true);
    }

    public string Describe(CVar cvar)
    {
        string flags = cvar.Flags == CVarFlags.None ? "" : $" [{cvar.Flags}]";
        string def = cvar.IsDefault ? "" : $" (default {cvar.DefaultString})";
        return $"{cvar.Name} = {cvar.ValueString}{def} ({cvar.TypeName}){flags} - {cvar.Help}";
    }

    // For crash reports.
    public string DumpNonDefault()
    {
        var changed = CVars.Where(c => !c.IsDefault).Select(c => $"{c.Name} = {c.ValueString} (default {c.DefaultString})").ToList();
        return changed.Count == 0 ? "(all defaults)" : string.Join('\n', changed);
    }
}
