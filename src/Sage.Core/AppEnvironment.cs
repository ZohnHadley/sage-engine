#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Sage.Core;

// What used to be process-wide and is now one app's (issue #49): its log (Logger: sinks, category levels,
// rate limits, frame/tick), its user folder (UserPaths) and its crash report's sections (CrashReporter).
// The statics `Log`, `LogCat`'s levels, `UserPaths` and `CrashReporter` read the current one, so their
// call sites are unchanged; two apps in one process — an editor hosting a play session — each have
// their own.
//
// Which one is current flows with the code that runs (an AsyncLocal: tasks and threads started from an
// app's code are that app's too). SageApp makes its environment current when it is created, for the
// rest of the creating code, and again around each of its steps and each world tick. Code outside any
// app uses the process's own, `Process`, which is what a host configures before it makes its app.
public sealed class AppEnvironment : IDisposable
{
    private static readonly AsyncLocal<AppEnvironment?> Ambient = new();

    private readonly object _lock = new();
    private readonly List<(string Name, Func<string> Provider)> _crashSections = new();
    private string? _gameId;     // null: the parent's
    private string? _userRoot;   // null: the parent's, or resolved from the game id

    private AppEnvironment(Logger logger, AppEnvironment? parent)
    {
        Logger = logger;
        Parent = parent;
    }

    // The process's own: the host's, and everything's outside an app.
    public static AppEnvironment Process { get; } = new(new Logger(), null);

    public static AppEnvironment Current => Ambient.Value ?? Process;

    // A new environment under `parent` (the current one when null): its own logger, starting at the
    // parent's levels and handing its lines to the parent's sinks too; the parent's user folder until
    // SetUserFolder says otherwise; no crash sections of its own.
    public static AppEnvironment CreateChild(AppEnvironment? parent = null)
    {
        parent ??= Current;
        return new AppEnvironment(new Logger(parent.Logger), parent);
    }

    // The environment for an app being made (SageApp): a child of the current one — a test's own log, the
    // process's — or, when the current one is another app's, a child of that app's parent: two apps made
    // one after the other are side by side, neither seeing the other's lines.
    public static AppEnvironment CreateForApp()
    {
        var parent = Current;
        while (parent.IsApp && parent.Parent != null) parent = parent.Parent;
        return new AppEnvironment(new Logger(parent.Logger), parent) { IsApp = true };
    }

    // True for an environment CreateForApp made.
    internal bool IsApp { get; private init; }

    internal AppEnvironment? Parent { get; }
    public Logger Logger { get; }
    public bool IsProcess => ReferenceEquals(this, Process);

    // Makes this environment current until the scope is disposed (then the one before is again). A
    // scope that is already current costs a read.
    public Scope Enter()
    {
        var before = Ambient.Value;
        if (!ReferenceEquals(before, this)) Ambient.Value = this;
        return new Scope(this, before);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly AppEnvironment? _entered;
        private readonly AppEnvironment? _before;

        internal Scope(AppEnvironment entered, AppEnvironment? before) { _entered = entered; _before = before; }

        // Only when this scope's environment is still the current one: a scope left in another order, or
        // in other code, changes nothing.
        public void Dispose()
        {
            if (_entered != null && ReferenceEquals(Ambient.Value, _entered) && !ReferenceEquals(_before, _entered))
                Ambient.Value = _before;
        }
    }

    // ---- The user folder (docs/design/05-assets-and-vfs.md §3.1, "user://") ------------------------

    // Until SetUserFolder is called on it, a child environment has its parent's.
    public string GameId
    {
        get
        {
            lock (_lock) if (_gameId != null) return _gameId;
            return Parent?.GameId ?? "sage";
        }
    }

    public string UserRoot
    {
        get
        {
            lock (_lock) if (_userRoot != null) return _userRoot;
            if (Parent != null) return Parent.UserRoot;
            string resolved = UserPaths.Resolve(GameId);
            lock (_lock) return _userRoot ??= resolved;
        }
    }

    // gameId becomes the game's id from game.json. overrideRoot is for tests and tools.
    public void SetUserFolder(string gameId, string? overrideRoot = null)
    {
        string root = overrideRoot ?? UserPaths.Resolve(gameId);
        lock (_lock)
        {
            _gameId = gameId;
            _userRoot = root;
        }
        Directory.CreateDirectory(root);
    }

    // ---- Crash reports ------------------------------------------------------------------------------

    // The path of the last crash report this environment wrote.
    public string? LastCrashReport { get; internal set; }

    // An extra crash report section: "CVars" (host), "GPU" (client), later "Modules"/"Mods".
    public void AddCrashSection(string name, Func<string> provider)
    {
        lock (_lock) _crashSections.Add((name, provider));
    }

    internal (string Name, Func<string> Provider)[] CrashSections()
    {
        lock (_lock) return _crashSections.ToArray();
    }

    // Disposes the logger (flushed first). The process's own is never disposed.
    public void Dispose()
    {
        if (IsProcess) return;
        Logger.Dispose();
    }
}
