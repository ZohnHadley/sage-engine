#nullable enable
using System;
using System.IO;

namespace Sage.Core;

// The writable per-user root (docs/design/05-assets-and-vfs.md §3.1, "user://"): config, logs,
// crash reports, later saves. Read-only content mounts are never written to.
//   dev builds:  <repo>/user/<gameId>/   (found by walking up to Sage.sln; gitignored)
//   otherwise:   %LOCALAPPDATA%/Sage/<gameId>/  (or the platform equivalent)
// Until the VFS exists (migration step 5) this is a plain directory path.
public static class UserPaths
{
    // The current app's (AppEnvironment.Current, issue #49): the process's own outside any app.
    public static string GameId => AppEnvironment.Current.GameId;

    public static string Root => AppEnvironment.Current.UserRoot;
    public static string Logs => Path.Combine(Root, "logs");
    public static string ConfigFile => Path.Combine(Root, "config.cfg");
    public static string AutoexecFile => Path.Combine(Root, "autoexec.cfg");   // optional, run after config.cfg (#299)
    public static string Screenshots => Path.Combine(Root, "screenshots");

    // Sets the current environment's folder: the process's, when a host calls it before making its app.
    // gameId is the game's id from game.json; overrideRoot is for tests and tools.
    public static void Initialize(string gameId, string? overrideRoot = null)
        => AppEnvironment.Current.SetUserFolder(gameId, overrideRoot);

    internal static string Resolve(string gameId)
    {
        if (BuildInfo.IsDevBuild)
        {
            string? repo = FindRepoRoot(AppContext.BaseDirectory);
            if (repo != null)
                return Path.Combine(repo, "user", gameId);
        }
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            local = AppContext.BaseDirectory;
        return Path.Combine(local, "Sage", gameId);
    }

    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sage.sln")))
                return dir.FullName;
        }
        return null;
    }
}
