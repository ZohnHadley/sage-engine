#nullable enable
using System;
using System.IO;

namespace Sage.Testing;

// UserPaths and the log are process-wide, so tests share one temporary user folder and capture log
// output with a sink (CaptureSink).
public static class TestEnv
{
    private static readonly object Lock = new();
    private static string? _root;

    // The user folder every test in the process shares, made on first use. Touch it before anything
    // that writes there (saves, logs, config): HeadlessApp does.
    public static string UserRoot
    {
        get
        {
            lock (Lock)
            {
                if (_root == null)
                {
                    _root = Path.Combine(Path.GetTempPath(), "sage-tests-" + Guid.NewGuid().ToString("N"));
                    UserPaths.Initialize("tests", _root);
                }
                return _root;
            }
        }
    }

    // A fresh folder under the user root, for one test's files.
    public static string NewTempDir()
    {
        string dir = Path.Combine(UserRoot, "tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Messages must be unique per test: the log collapses consecutive identical messages globally.
    public static string Unique(string text) => $"{text} [{Guid.NewGuid():N}]";

    // The nearest folder above the test assembly that holds `marker` (a solution file, say): how a test
    // finds its repository's games and engine content without depending on where it was run from.
    public static string FolderAbove(string marker)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, marker)) || Directory.Exists(Path.Combine(dir.FullName, marker)))
                return dir.FullName;
        throw new DirectoryNotFoundException($"No folder above {AppContext.BaseDirectory} holds {marker}.");
    }
}
