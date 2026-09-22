#nullable enable
using System;
using System.IO;

namespace sage_engine;

// The writable per-user root (docs/design/05-assets-and-vfs.md §3.1, "user://"): config, logs,
// crash reports, later saves. Read-only content mounts are never written to.
//   dev builds:  <repo>/user/<gameId>/   (found by walking up to Sage.sln; gitignored)
//   otherwise:   %LOCALAPPDATA%/Sage/<gameId>/  (or the platform equivalent)
// Until the VFS exists (migration step 5) this is a plain directory path.
public static class UserPaths
{
    private static string? _root;

    public static string GameId { get; private set; } = "sage";

    public static string Root => _root ??= Resolve(GameId);
    public static string Logs => Path.Combine(Root, "logs");
    public static string ConfigFile => Path.Combine(Root, "config.cfg");

    // gameId becomes the game's id from game.json once games are modules (migration step 5).
    // overrideRoot is for tests and tools.
    public static void Initialize(string gameId, string? overrideRoot = null)
    {
        GameId = gameId;
        _root = overrideRoot ?? Resolve(gameId);
        Directory.CreateDirectory(_root);
    }

    private static string Resolve(string gameId)
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
