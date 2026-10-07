#nullable enable
using System;
using System.IO;

namespace Sage.Testing;

// Tests share one temporary user folder, the process environment's (each app's folder is its
// environment's, issue #49, and an app a test makes has the process's unless it says otherwise), and
// capture log output with a sink (CaptureSink).
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
                    AppEnvironment.Process.SetUserFolder("tests", _root);
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
