#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Sage.Core;

// How much a zip mount may hold (issue #397). The defaults are far above any real game's content and far
// below what a zip bomb unpacks to; a test lowers them to prove each rule without writing gigabytes.
internal sealed record ZipLimits
{
    public static readonly ZipLimits Default = new();

    // Files and folders in the central directory.
    public int MaxEntries { get; init; } = 200_000;
    // One file, unpacked. It is read into memory whole, so it must fit an array as well.
    public long MaxEntryBytes { get; init; } = 1L << 30;           // 1 GiB
    // Every file together, unpacked.
    public long MaxTotalBytes { get; init; } = 16L << 30;          // 16 GiB
    // Unpacked over packed, for a file over RatioFloorBytes: JSON packs ~10:1, a bomb ~1000:1 and more.
    public long MaxRatio { get; init; } = 500;
    public long RatioFloorBytes { get; init; } = 1L << 20;        // 1 MiB
}

// A zip archive as a mount (issue #397; 05 §3.1): a shipped game's `content.zip` or a mod's `.sagemod`. Read
// only, so it neither hot reloads nor is written by the editor (PhysicalPath and WritablePath are null), and
// it follows a folder mount's rules: paths are case-insensitive and enumerate in the order a folder's do.
//
// Hardened: the whole central directory is checked when the mount is made, and an archive that breaks a
// rule is refused with an InvalidDataException naming the file, the entry and the rule:
// - no absolute path (`/x`, `\x`, `C:x`), no `..` or `.` segment, no empty segment, no ':' or control character;
// - no symbolic link entry, and no two entries that are one path ignoring case;
// - at most ZipLimits.MaxEntries entries, MaxEntryBytes per file and MaxTotalBytes in all, and no file that
//   unpacks to more than MaxRatio times its packed size (a zip bomb);
// - a file that unpacks to more than its header said is refused when it is read.
// Nothing is ever extracted to disk, so the paths are a mount's names, never a place to write; the rules
// exist so an archive cannot name a file outside itself if a tool ever does unpack one.
//
// The file is held open until Dispose (the engine disposes its mounts), and reads are serialised: each
// Open reads the whole entry into memory, under a lock, and hands back a read-only MemoryStream.
public sealed class ZipMount : IMount, IDisposable
{
    private readonly FileStream _file;
    private readonly ZipArchive _archive;
    private readonly ZipLimits _limits;
    private readonly object _gate = new();
    // Virtual path -> entry, in a folder mount's enumeration order (case-insensitive ordinal).
    private readonly SortedDictionary<string, ZipArchiveEntry> _files = new(StringComparer.OrdinalIgnoreCase);

    public ZipMount(string name, string file, string recordNamespace) : this(name, file, recordNamespace, ZipLimits.Default) { }

    internal ZipMount(string name, string file, string recordNamespace, ZipLimits limits)
    {
        Name = name;
        ArchivePath = Path.GetFullPath(file);
        RecordNamespace = recordNamespace;
        _limits = limits;
        try
        {
            _file = new FileStream(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{ArchivePath}: can't be opened: {ex.Message}", ex);
        }
        try
        {
            _archive = new ZipArchive(_file, ZipArchiveMode.Read, leaveOpen: false);
            Check();
        }
        catch (InvalidDataException ex) when (!ex.Message.StartsWith(ArchivePath, StringComparison.Ordinal))
        {
            _file.Dispose();
            throw new InvalidDataException($"{ArchivePath}: is not a zip archive, or is damaged: {ex.Message}", ex);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public string Name { get; }
    public string RecordNamespace { get; }

    // The archive on disk (a full path).
    public string ArchivePath { get; }

    // How many files it carries.
    public int Count => _files.Count;

    private void Check()
    {
        var entries = _archive.Entries;
        if (entries.Count > _limits.MaxEntries)
            throw Refuse($"it has {entries.Count} entries, more than the {_limits.MaxEntries} allowed");
        long total = 0;
        foreach (var entry in entries)
        {
            string name = entry.FullName;
            if (Unsafe(name) is { } why) throw Refuse($"entry '{Printable(name)}' {why}");
            // A symbolic link, by the Unix mode a zip made on Unix keeps in the high half of the attributes.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw Refuse($"entry '{name}' is a symbolic link");
            bool folder = name.EndsWith('/') || name.EndsWith('\\');
            if (folder)
            {
                if (entry.Length != 0) throw Refuse($"entry '{name}' is a folder with data in it");
                continue;
            }
            if (entry.Length < 0 || entry.Length > _limits.MaxEntryBytes)
                throw Refuse($"entry '{name}' unpacks to {entry.Length} bytes, more than the {_limits.MaxEntryBytes} allowed for one file");
            total += entry.Length;
            if (total > _limits.MaxTotalBytes)
                throw Refuse($"its files unpack to more than the {_limits.MaxTotalBytes} bytes allowed in all");
            if (entry.Length > _limits.RatioFloorBytes && entry.Length > _limits.MaxRatio * Math.Max(1, entry.CompressedLength))
                throw Refuse($"entry '{name}' unpacks to {entry.Length} bytes from {entry.CompressedLength}, " +
                             $"more than {_limits.MaxRatio} times its packed size (a zip bomb?)");
            VirtualPath path;
            try { path = VirtualPath.Parse(name); }
            catch (ArgumentException ex) { throw Refuse($"entry '{name}' is not a valid path: {ex.Message}"); }
            if (_files.TryGetValue(path.Value, out var other))
                throw Refuse($"entries '{other.FullName}' and '{name}' are the same path (paths ignore case)");
            _files[path.Value] = entry;
        }
    }

    // Why an entry's name is not a safe relative path, or null.
    internal static string? Unsafe(string name)
    {
        if (name.Length == 0) return "has no name";
        foreach (char c in name)
            if (char.IsControl(c)) return "has a control character in its name";
        if (name[0] is '/' or '\\') return "is an absolute path";
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':') return "is an absolute path (a drive)";
        if (name.Contains(':')) return "has a ':' in its name";
        string trimmed = name.EndsWith('/') || name.EndsWith('\\') ? name[..^1] : name;
        foreach (string segment in trimmed.Split('/', '\\'))
        {
            if (segment == "..") return "climbs out of the archive with '..' (zip-slip)";
            if (segment == ".") return "has a '.' segment";
            if (segment.Length == 0) return "has an empty segment";
        }
        return null;
    }

    private static string Printable(string name) =>
        new(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    private InvalidDataException Refuse(string why) => new($"{ArchivePath}: refused: {why}");

    public bool Exists(VirtualPath path) => _files.ContainsKey(path.Value);

    public Stream Open(VirtualPath path)
    {
        if (!_files.TryGetValue(path.Value, out var entry))
            throw new FileNotFoundException($"'{path}' not found in mount {Name}");
        int length = (int)entry.Length;   // at most MaxEntryBytes, checked when mounted
        var bytes = new byte[length];
        lock (_gate)
        {
            using var stream = entry.Open();
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(bytes, read, length - read);
                if (n == 0) break;
                read += n;
            }
            if (read != length)
                throw new InvalidDataException($"{ArchivePath}: entry '{entry.FullName}' ends after {read} bytes, and its header says {length}");
            if (stream.ReadByte() >= 0)
                throw new InvalidDataException($"{ArchivePath}: refused: entry '{entry.FullName}' unpacks to more than the {length} bytes its header says");
        }
        return new MemoryStream(bytes, writable: false);
    }

    public string? PhysicalPath(VirtualPath path) => null;

    public string? WritablePath(VirtualPath path) => null;

    public IEnumerable<VirtualPath> Enumerate(VirtualPath? directory, string searchPattern, bool recursive)
    {
        string prefix = directory is { } d ? d.Value + "/" : "";
        foreach (string file in _files.Keys)
        {
            if (!file.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rest = file[prefix.Length..];
            if (!recursive && rest.Contains('/')) continue;
            if (AssemblyContentMount.Matches(rest[(rest.LastIndexOf('/') + 1)..], searchPattern)) yield return VirtualPath.Parse(file);
        }
    }

    public void Dispose()
    {
        lock (_gate) _archive.Dispose();
    }

    public override string ToString() => $"{Name} ({ArchivePath}, {Count} file(s))";
}
