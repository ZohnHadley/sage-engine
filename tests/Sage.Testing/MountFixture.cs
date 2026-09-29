#nullable enable
using System.Collections.Generic;
using System.IO;

namespace Sage.Testing;

// Content folders a test writes on the spot and mounts (docs/design/05 §3.1): a mod, a game, the
// engine's own data, whichever the test is about. Its own VFS for tests of the VFS itself; hand it to
// HeadlessAppBuilder.Mount to put the same folders in an app's.
public sealed class MountFixture
{
    private readonly List<FolderMount> _mounts = new();

    public readonly string Root = TestEnv.NewTempDir();
    public readonly VirtualFileSystem Vfs = new();

    // Every mount made so far, in order (later wins).
    public IReadOnlyList<FolderMount> Mounts => _mounts;

    public string Dir(string mount) => Path.Combine(Root, mount);

    public void Write(string mount, string relative, string text)
    {
        string file = Path.Combine(Dir(mount), relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    public FolderMount Mount(string mount, string ns)
    {
        Directory.CreateDirectory(Dir(mount));
        var m = new FolderMount(mount, Dir(mount), ns);
        Vfs.Mount(m);
        _mounts.Add(m);
        return m;
    }
}
