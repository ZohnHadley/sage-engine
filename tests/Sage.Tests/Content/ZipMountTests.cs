#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Zip mounts and `.sagemod` packages (issue #397): a zip reads as the folder it was made from (case, order,
// patterns); a hostile archive is refused with a reason, one test per rule; `sage mods pack` writes what loads;
// a packed mod is found beside the folders and mounted, and a refused one is reported, not fatal; a game and
// its mods load identically as folders and as zips; and hot reload watches no zip.
public class ZipMountTests
{
    public ZipMountTests() { _ = TestEnv.UserRoot; }

    private static void Write(string folder, string relative, string text)
    {
        string file = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    // A zip with these entries, named exactly as given (a hostile archive names whatever it likes).
    private static string Zip(params (string Name, string Text)[] entries) => Zip(entries, null);

    private static string Zip((string Name, string Text)[] entries, Action<ZipArchiveEntry>? touch, CompressionLevel level = CompressionLevel.Optimal)
    {
        string file = Path.Combine(TestEnv.NewTempDir(), "test.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name, level);
                touch?.Invoke(entry);
                using var w = new StreamWriter(entry.Open());
                w.Write(text);
            }
        return file;
    }

    private static string Refusal(string file, ZipLimits? limits = null) =>
        Assert.Throws<InvalidDataException>(() => new ZipMount("z", file, "z", limits ?? ZipLimits.Default).Dispose()).Message;

    private static string ReadAll(IMount mount, VirtualPath path)
    {
        using var reader = new StreamReader(mount.Open(path));
        return reader.ReadToEnd();
    }

    [Fact]
    public void AZipReadsAsTheFolderItWasMadeFrom_CaseOrderAndPatterns()
    {
        string folder = TestEnv.NewTempDir();
        Write(folder, "Data/Items.json", "items");
        Write(folder, "Data/a_b.json", "underscore");
        Write(folder, "Data/AB.json", "ab");
        Write(folder, "Data/sub/deep.json", "deep");
        Write(folder, "Textures/Wall.PNG", "png");
        Write(folder, "readme.txt", "top");
        string zipFile = Path.Combine(TestEnv.NewTempDir(), "content.zip");
        ZipFile.CreateFromDirectory(folder, zipFile);

        var loose = new FolderMount("f", folder, "g");
        using var zip = new ZipMount("z", zipFile, "g");
        Assert.Equal(6, zip.Count);

        foreach (var (dir, pattern, recursive) in new (string?, string, bool)[]
                 { (null, "*", true), (null, "*", false), ("data", "*.json", true), ("data", "*.json", false), ("DATA", "a_*", false), ("textures", "*.PNG", true), ("nope", "*", true) })
        {
            VirtualPath? d = dir == null ? null : VirtualPath.Parse(dir);
            Assert.Equal(loose.Enumerate(d, pattern, recursive).ToList(), zip.Enumerate(d, pattern, recursive).ToList());
        }
        foreach (var path in loose.Enumerate(null, "*", true))
        {
            Assert.True(zip.Exists(path));
            Assert.Equal(ReadAll(loose, path), ReadAll(zip, path));
        }
        Assert.True(zip.Exists(VirtualPath.Parse("DATA/ITEMS.JSON")));       // case-insensitive, as a folder is
        Assert.False(zip.Exists(VirtualPath.Parse("data")));                  // a folder is not a file
        Assert.Throws<FileNotFoundException>(() => zip.Open(VirtualPath.Parse("missing.json")));
        // Read only, and never a file on disk: no hot reload, no editor writes.
        Assert.Null(zip.PhysicalPath(VirtualPath.Parse("data/items.json")));
        Assert.Null(zip.WritablePath(VirtualPath.Parse("data/new.json")));
    }

    // Zip-slip and its cousins: each refused, naming the archive, the entry and the rule.
    [Theory]
    [InlineData("../evil.json", "zip-slip")]
    [InlineData("data/../../evil.json", "zip-slip")]
    [InlineData("data\\..\\evil.json", "zip-slip")]
    [InlineData("/etc/passwd", "absolute path")]
    [InlineData("\\windows\\evil.json", "absolute path")]
    [InlineData("C:/evil.json", "absolute path (a drive)")]
    [InlineData("data/./x.json", "'.' segment")]
    [InlineData("data//x.json", "empty segment")]
    [InlineData("data/x\u0001.json", "control character")]
    [InlineData("data/a:b.json", "':'")]
    public void AnEntryWhosePathLeavesTheArchiveIsRefusedWithTheRule(string name, string rule)
    {
        string file = Zip(("mod.json", "{}"), (name, "x"));
        string message = Refusal(file);
        Assert.StartsWith(Path.GetFullPath(file), message);
        Assert.Contains("refused", message);
        Assert.Contains(rule, message);
    }

    [Fact]
    public void TwoEntriesThatAreOnePathIgnoringCaseAreRefused()
    {
        string message = Refusal(Zip(("Data/A.json", "1"), ("data/a.json", "2")));
        Assert.Contains("'Data/A.json' and 'data/a.json' are the same path", message);
    }

    [Fact]
    public void ASymbolicLinkEntryIsRefused()
    {
        string file = Zip(new[] { ("link", "/etc/passwd") }, e => e.ExternalAttributes = unchecked((int)(0xA1FFu << 16)));
        Assert.Contains("entry 'link' is a symbolic link", Refusal(file));
    }

    [Fact]
    public void TooManyEntriesAreRefused()
    {
        string file = Zip(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4"));
        Assert.Contains("it has 4 entries, more than the 3 allowed", Refusal(file, ZipLimits.Default with { MaxEntries = 3 }));
        using var fine = new ZipMount("z", file, "z", ZipLimits.Default with { MaxEntries = 4 });
        Assert.Equal(4, fine.Count);
    }

    [Fact]
    public void AFileOverTheSizeLimitIsRefused()
    {
        string file = Zip(("big.bin", new string('x', 11)));
        Assert.Contains("entry 'big.bin' unpacks to 11 bytes, more than the 10 allowed for one file",
            Refusal(file, ZipLimits.Default with { MaxEntryBytes = 10 }));
    }

    [Fact]
    public void FilesOverTheTotalLimitAreRefused()
    {
        string file = Zip(("a.bin", new string('x', 10)), ("b.bin", new string('y', 10)));
        Assert.Contains("its files unpack to more than the 15 bytes allowed in all",
            Refusal(file, ZipLimits.Default with { MaxTotalBytes = 15 }));
    }

    [Fact]
    public void AZipBombIsRefusedByItsRatio()
    {
        string file = Zip(("bomb.bin", new string('\0', 100_000)));
        string message = Refusal(file, ZipLimits.Default with { MaxRatio = 10, RatioFloorBytes = 1000 });
        Assert.Contains("entry 'bomb.bin' unpacks to 100000 bytes from", message);
        Assert.Contains("zip bomb", message);
        // Under the floor a small file may pack as well as it likes (JSON whitespace does).
        using var fine = new ZipMount("z", file, "z", ZipLimits.Default with { MaxRatio = 10, RatioFloorBytes = 200_000 });
        Assert.Equal(100_000, fine.Open(VirtualPath.Parse("bomb.bin")).Length);
    }

    // A header that understates a stored file's size: the read is refused rather than trusted.
    [Fact]
    public void AFileLongerThanItsHeaderSaysIsRefusedWhenRead()
    {
        string file = Zip(new[] { ("lie.txt", "0123456789ABCDEFGHIJ") }, null, CompressionLevel.NoCompression);
        byte[] bytes = File.ReadAllBytes(file);
        // The uncompressed size: offset 22 of the local header, 24 of the central directory's.
        Patch(bytes, new byte[] { 0x50, 0x4b, 0x03, 0x04 }, 22, 10);
        Patch(bytes, new byte[] { 0x50, 0x4b, 0x01, 0x02 }, 24, 10);
        File.WriteAllBytes(file, bytes);

        using var zip = new ZipMount("z", file, "z");
        var ex = Assert.Throws<InvalidDataException>(() => zip.Open(VirtualPath.Parse("lie.txt")));
        Assert.Contains("unpacks to more than the 10 bytes its header says", ex.Message);

        static void Patch(byte[] bytes, byte[] signature, int offset, int value)
        {
            for (int i = 0; i + 4 <= bytes.Length; i++)
                if (bytes.AsSpan(i, 4).SequenceEqual(signature))
                {
                    BitConverter.TryWriteBytes(bytes.AsSpan(i + offset, 4), value);
                    return;
                }
            throw new InvalidOperationException("signature not found");
        }
    }

    [Fact]
    public void AFileThatIsNotAZipIsRefusedAsDamaged()
    {
        string file = Path.Combine(TestEnv.NewTempDir(), "broken.sagemod");
        File.WriteAllText(file, "this is not a zip");
        Assert.Contains("is not a zip archive, or is damaged", Refusal(file));
    }

    // ---- sage mods pack ----

    private static string ModFolder(string parent, string id, string thing = "thing", string game = "modtest")
    {
        string dir = Path.Combine(parent, id);
        Write(dir, "mod.json", $$"""{ "id": "{{id}}", "version": "1.2.0", "game": "{{game}}" }""");
        Write(dir, "data/things.json", $$"""[ { "type": "prefab", "id": "{{thing}}", "name": "{{id}} {{thing}}" } ]""");
        return dir;
    }

    [Fact]
    public void PackWritesASagemodThatLoads_TheSameBytesEachTime_WithoutDotFiles()
    {
        string mod = ModFolder(TestEnv.NewTempDir(), "packed");
        Write(mod, ".git/config", "secret");
        Write(mod, ".hidden", "x");
        Write(mod, "Textures/Logo.png", "png");
        string outDir = TestEnv.NewTempDir();

        var (manifest, files, output) = ModPackage.Pack(mod, Path.Combine(outDir, "a.sagemod"));
        Assert.Equal("packed", manifest.Id);
        Assert.Equal(3, files);                                                  // mod.json, data, texture
        var loaded = ModManifest.Load(output);
        Assert.True(loaded.IsPackage);
        Assert.Equal("1.2.0", loaded.Version);
        Assert.Equal(output, loaded.Directory);
        using (var zip = new ZipMount("m", output, "packed"))
        {
            Assert.Equal(new[] { "data/things.json", "mod.json", "textures/logo.png" }, zip.Enumerate(null, "*", true).Select(p => p.Value));
            Assert.False(zip.Exists(VirtualPath.Parse(".git/config")));
        }
        Assert.Equal("packed-1.2.0.sagemod", ModPackage.DefaultFileName(manifest));

        File.SetLastWriteTimeUtc(Path.Combine(mod, "mod.json"), DateTime.UtcNow.AddDays(-3));
        var (_, _, again) = ModPackage.Pack(mod, Path.Combine(outDir, "b.sagemod"));
        Assert.Equal(File.ReadAllBytes(output), File.ReadAllBytes(again));
    }

    [Fact]
    public void PackRefusesABrokenModAndALinkWritingNothing()
    {
        string parent = TestEnv.NewTempDir();
        string broken = Path.Combine(parent, "broken");
        Write(broken, "mod.json", """{ "id": "Bad Id" }""");
        string output = Path.Combine(TestEnv.NewTempDir(), "x.sagemod");
        Assert.Contains("not a valid namespace", Assert.Throws<InvalidDataException>(() => ModPackage.Pack(broken, output)).Message);
        Assert.False(File.Exists(output));

        string mod = ModFolder(parent, "linked");
        try { File.CreateSymbolicLink(Path.Combine(mod, "data", "outside.json"), Path.Combine(parent, "broken", "mod.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // no symlinks here (Windows without the right)
        Assert.Contains("is a symbolic link", Assert.Throws<InvalidDataException>(() => ModPackage.Pack(mod, output)).Message);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".tmp"));
    }

    // ---- discovery ----

    private static string NewGame(string mounts = "\"content\"")
    {
        string dir = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "game.json"), $$"""{ "name": "Zip test", "id": "modtest", "mounts": [{{mounts}}], "version": "1.0.0" }""");
        Write(Path.Combine(dir, "content"), "data/rock.json", """[ { "type": "prefab", "id": "rock", "name": "rock" } ]""");
        return dir;
    }

    [Fact]
    public void ASagemodBesideTheModFoldersIsFoundMountedAndListed_AndAHostileOneIsRefusedWithTheReason()
    {
        string game = NewGame();
        string mods = Path.Combine(game, "mods");
        ModFolder(mods, "loose");
        string staging = TestEnv.NewTempDir();
        ModPackage.Pack(ModFolder(staging, "zipped"), Path.Combine(mods, "zipped.sagemod"));
        // A package with a zip-slip entry beside them.
        string hostile = Zip(("mod.json", """{ "id": "evil", "version": "1.0.0" }"""), ("../../escape.json", "[]"));
        File.Move(hostile, Path.Combine(mods, "evil.SAGEMOD"));

        using var app = HeadlessApp.ForGame(game).WithUserMods(null).Boot();

        Assert.Equal(new[] { "loose", "zipped" }, app.Engine.Mods.Active.Select(m => m.Id));
        var mount = Assert.IsType<ZipMount>(app.Vfs.Mounts.Single(m => m.Name == "mods/zipped"));
        Assert.Equal("zipped", mount.RecordNamespace);
        Assert.Equal("zipped thing", app.Records.Get<PrefabRecord>(new RecordId("zipped", "thing")).Name);
        var refused = Assert.Single(app.Engine.Mods.Refused);
        Assert.Equal("evil", refused.Id);
        Assert.Contains("its package can't be used", refused.Reason);
        Assert.Contains("zip-slip", refused.Reason);
        Assert.Contains(app.Engine.ModManager.ListLines(), l => l.Contains("zipped 1.2.0") && l.EndsWith("(packed)"));
    }

    [Fact]
    public void ASagemodNamedOnTheCommandLineValidates()
    {
        string game = NewGame();
        string pkg = Path.Combine(TestEnv.NewTempDir(), "named.sagemod");
        ModPackage.Pack(ModFolder(TestEnv.NewTempDir(), "named"), pkg);
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = game,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
            Mods = new[] { pkg },
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
        Assert.Equal(new[] { "named" }, report.Mods.Active.Select(m => m.Id));
    }

    // ---- game mounts ----

    [Fact]
    public void AGameMountThatIsAZipIsMountedUnderTheFoldersName_AndAHostileOneIsSkippedWithAnError()
    {
        string game = NewGame("\"content.zip\", \"extra.zip\"");
        ZipFile.CreateFromDirectory(Path.Combine(game, "content"), Path.Combine(game, "content.zip"));
        Directory.Delete(Path.Combine(game, "content"), recursive: true);
        File.Move(Zip(("/abs.json", "[]")), Path.Combine(game, "extra.zip"));

        using var log = new CaptureSink();
        using var app = HeadlessApp.ForGame(game).WithUserMods(null).Boot();

        Assert.IsType<ZipMount>(app.Vfs.Mounts.Single(m => m.Name == "modtest/content"));
        Assert.DoesNotContain(app.Vfs.Mounts, m => m.Name == "modtest/extra");
        Assert.Equal("rock", app.Records.Get<PrefabRecord>(new RecordId("modtest", "rock")).Name);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("game.json mount 'extra.zip' is not mounted") && e.Message.Contains("absolute path"));
    }

    // Hot reload watches folders only: a zip mount has none to watch.
    [Fact]
    public void RecordHotReloadSkipsZipMounts()
    {
        string folder = TestEnv.NewTempDir();
        Write(folder, "data/a.json", "[]");
        string zipFile = Path.Combine(TestEnv.NewTempDir(), "c.zip");
        ZipFile.CreateFromDirectory(folder, zipFile);
        var vfs = new VirtualFileSystem();
        using var zip = new ZipMount("z", zipFile, "z");
        vfs.Mount(zip);
        Assert.Equal(0, Watchers(vfs));
        Assert.Null(vfs.VirtualPathOf(Path.Combine(folder, "data", "a.json")));   // no disk file maps to a zip
        vfs.Mount(new FolderMount("f", folder, "f"));
        Assert.Equal(1, Watchers(vfs));                                           // the folder's data/, not the zip's
    }

    private static int Watchers(VirtualFileSystem vfs)
    {
        using var reload = new RecordHotReload(new RecordStore(), vfs);
        var field = typeof(RecordHotReload).GetField("_watchers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return ((System.Collections.ICollection)field.GetValue(reload)!).Count;
    }

    // ---- parity ----

    // Acceptance (issue #397): tests/games/mods loads identically as folders and with its content and both mods
    // zipped: the same mods in the same order, the same mounts, every record's merged fields, every file each
    // path opens, and the same content report and conflicts.
    [Fact]
    public void AGameAndItsModsLoadIdenticallyAsFoldersAndAsZips()
    {
        string source = Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "mods");
        string loose = Copy(source, Path.Combine(TestEnv.NewTempDir(), "game"));
        string packed = Copy(source, Path.Combine(TestEnv.NewTempDir(), "game"));

        ZipFile.CreateFromDirectory(Path.Combine(packed, "content"), Path.Combine(packed, "content.zip"));
        Directory.Delete(Path.Combine(packed, "content"), recursive: true);
        string json = File.ReadAllText(Path.Combine(packed, "game.json"));
        Assert.Contains("\"mounts\": [\"content\"]", json);
        File.WriteAllText(Path.Combine(packed, "game.json"), json.Replace("\"mounts\": [\"content\"]", "\"mounts\": [\"content.zip\"]"));
        foreach (string mod in Directory.GetDirectories(Path.Combine(packed, "mods")))
        {
            ModPackage.Pack(mod, mod + ModManifest.PackageExtension);
            Directory.Delete(mod, recursive: true);
        }

        var a = Snapshot(loose);
        var b = Snapshot(packed);
        Assert.Contains(b.Mounts, m => m.Contains("ZipMount"));
        Assert.Equal(new[] { "better_blades", "rival_trade" }, a.Active);
        Assert.Equal(a.Active, b.Active);
        Assert.Equal(a.Mounts.Select(m => m.Split('|')[0]), b.Mounts.Select(m => m.Split('|')[0]));
        Assert.Equal(a.Records, b.Records);
        Assert.Equal(a.Files, b.Files);
        Assert.Equal(a.Report.Select(l => l.Replace(loose, "<game>")), b.Report.Select(l => l.Replace(packed, "<game>")));
        Assert.NotEmpty(a.Records);
        Assert.Contains(a.Report, l => l.Contains("better_blades") && l.Contains("rival_trade"));
    }

    private sealed record Snap(string[] Active, string[] Mounts, List<string> Records, List<string> Files, List<string> Report);

    private static Snap Snapshot(string game)
    {
        using var app = HeadlessApp.ForGame(game).WithEngineContent().WithUserMods(null).Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var records = new List<string>();
        foreach (string type in app.Records.TypeNames)
            foreach (var id in app.Records.Ids(type))
                records.Add($"{type} {id} {app.Records.RawJson(type, id)?.ToJsonString()}");
        var files = app.Vfs.Enumerate(null, "*").Select(x => x.Path).Distinct()
            .Select(p =>
            {
                var mount = app.Vfs.Which(p)!;
                using var s = mount.Open(p);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return $"{p} {mount.Name} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray()))}";
            }).ToList();
        return new Snap(app.Engine.Mods.Active.Select(m => m.Id).ToArray(),
                        app.Vfs.Mounts.Select(m => $"{m.Name}|{m.GetType().Name}").ToArray(),
                        records, files, ContentReport.Build(app.Records, app.Vfs).Lines().ToList());
    }

    private static string Copy(string from, string to)
    {
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        return to;
    }
}
