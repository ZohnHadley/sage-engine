#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Sage.Core;

// What a write to a record did (4j-2). A definition writes each of its top-level fields; a patch sets,
// adds to (`field+`) or removes from (`field-`) the deepest path it names, or disables the record.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
public enum RecordWriteOp : byte
{
    Define,
    Set,
    Add,
    Remove,
    Disable,
}

// One write as the loader keeps it: a path (dotted, "parts.inventory.items"; "" for the whole record),
// the record in the file that wrote it, and what it did. Small on purpose: every record carries one per
// top-level field it defines and one per leaf a patch writes, and nothing else. A path string is only
// built for a nested write; a top-level one reuses the JSON's own key.
internal readonly struct FieldWrite
{
    public readonly string Path;
    public readonly RecordSource Source;
    public readonly RecordWriteOp Op;

    public FieldWrite(string path, RecordSource source, RecordWriteOp op)
    {
        Path = path;
        Source = source;
        Op = op;
    }

    // Whether `path` is top-level field `field` or inside it ("stats.agility" is under "stats", and an
    // entry of a keyed list, "items[village:lantern].count", under "items").
    public static bool IsUnder(string path, string field) =>
        path.Length >= field.Length && field.Length > 0 &&
        string.Compare(path, 0, field, 0, field.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
        (path.Length == field.Length || path[field.Length] is '.' or '[');

    // Whether one path is the other or inside it, either way round: a set of "stats" and a set of
    // "stats.agility" write the same value. "" is the whole record and overlaps everything.
    public static bool Overlaps(string a, string b) =>
        a.Length == 0 || b.Length == 0 || (a.Length <= b.Length ? IsUnder(b, a) : IsUnder(a, b));

    public static string TopLevel(string path)
    {
        int dot = path.IndexOfAny(Separators);
        return dot < 0 ? path : path[..dot];
    }

    private static readonly char[] Separators = { '.', '[' };

    // Whether `add` is an add to a list that `path` is inside one entry of (issue #399): an entry added
    // to a keyed list and another entry edited in place by key are not the same write.
    public static bool IsIntoEntryOf(string path, string add)
    {
        return add.Length > 0 && path.Length > add.Length + 1 && path[add.Length] == '[' &&
               string.Compare(path, 0, add, 0, add.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }
}

// A write to a record as `RecordStore.Writes` gives it: which mount and file wrote which path, how,
// and — for a field the record inherited — the base it came through.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
public readonly struct RecordWrite
{
    private readonly FieldWrite _write;

    internal RecordWrite(FieldWrite write, RecordId via)
    {
        _write = write;
        Via = via;
    }

    // The value path written: "name", "parts.inventory.items"; "" when a patch disabled the record.
    public string Path => _write.Path;
    public RecordWriteOp Op => _write.Op;

    // The mount the file is in; null for a file read from no mount.
    public IMount? Mount => _write.Source.File.Mount;

    // "mount:path" of the file.
    public string File => _write.Source.File.Name;

    // "mount:path:line:column" of what wrote it, worked out when asked.
    public string At => _write.Source.At(_write.Op == RecordWriteOp.Disable ? "disabled" : _write.Path);

    // The base the record inherited this write from; empty when it is the record's own.
    public RecordId Via { get; }

    public override string ToString() =>
        $"{Op.ToString().ToLowerInvariant()} {(Path.Length == 0 ? "(record)" : Path)}  {At}" + (Via.IsEmpty ? "" : $" (via base {Via})");
}

// What kind of thing two mods both wrote.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
public enum ContentConflictKind
{
    Value,      // the same value path set by two mods (or set by one and added to by another)
    Disabled,   // one mod disabled a record another patched
    Asset,      // two mods ship the same asset path
}

// Two or more mods writing the same thing (decision 5): the last one in load order wins, and the
// player may not get what either author meant. A warning, never an error.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
public sealed class ContentConflict
{
    internal ContentConflict(ContentConflictKind kind, string type, RecordId id, string path, IReadOnlyList<IMount> writers, IMount winner, string line)
    {
        Kind = kind;
        Type = type;
        Id = id;
        Path = path;
        Writers = writers;
        Winner = winner;
        Line = line;
    }

    public ContentConflictKind Kind { get; }

    // The record's type and id; "asset" and an empty id for an asset.
    public string Type { get; }
    public RecordId Id { get; }

    // The value path ("name", "parts.inventory.items"), "" for a whole record, or the asset's path.
    public string Path { get; }

    // The mods that wrote it, in load order.
    public IReadOnlyList<IMount> Writers { get; }

    // The one whose write stands (for Disabled: the last to write anything).
    public IMount Winner { get; }

    // As the report prints it: "prefab mods:trader name: better_blades, rival_trade; rival_trade won".
    public string Line { get; }

    public override string ToString() => Line;
}

// An asset path more than one mount has: the last mount's file is the one loaded (05 §3.1).
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
public sealed class ShadowedAsset
{
    internal ShadowedAsset(VirtualPath path, IMount winner, IReadOnlyList<IMount> shadowed)
    {
        Path = path;
        Winner = winner;
        Shadowed = shadowed;
    }

    public VirtualPath Path { get; }
    public IMount Winner { get; }

    // The mounts whose copy is hidden, in mount order.
    public IReadOnlyList<IMount> Shadowed { get; }
}

// A record one mount patched, and what its patches wrote.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
public sealed class PatchedRecord
{
    internal PatchedRecord(string type, RecordId id, IMount? definedBy, IReadOnlyList<RecordWrite> writes)
    {
        Type = type;
        Id = id;
        DefinedBy = definedBy;
        Writes = writes;
    }

    public string Type { get; }
    public RecordId Id { get; }

    // The mount that defined the record. A mod patching what is not a mod's is an override, listed
    // and never a conflict (decision 5).
    public IMount? DefinedBy { get; }

    // This mount's writes to it, in order.
    public IReadOnlyList<RecordWrite> Writes { get; }
}

// One mount's part in the content: what it added, patched and shadowed, and what it got wrong.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
public sealed class ContentMountReport
{
    internal ContentMountReport(IMount mount) { Mount = mount; }

    public IMount Mount { get; }
    public bool IsMod => ContentReport.IsModMount(Mount);

    // "item mymod:falchion", in type then id order.
    public IReadOnlyList<string> Added => _added;
    internal readonly List<string> _added = new();
    public IReadOnlyList<PatchedRecord> Patched => _patched;
    internal readonly List<PatchedRecord> _patched = new();

    // "item sandbox:sword at file:line (first defined at file:line)": a definition of a record that
    // already existed, which is an error and is applied as a patch.
    public IReadOnlyList<string> Redefinitions => _redefinitions;
    internal readonly List<string> _redefinitions = new();

    // "item sandbox:bow at file:line": a patch of a record no mount before it defined, skipped.
    public IReadOnlyList<string> SkippedPatches => _skippedPatches;
    internal readonly List<string> _skippedPatches = new();

    // The assets this mount's files hide in earlier mounts.
    public IReadOnlyList<ShadowedAsset> Shadows => _shadows;
    internal readonly List<ShadowedAsset> _shadows = new();
}

// What every mount did to the content, and where mods conflict (4j-2, decision 5): built from the
// record store's merge provenance (`RecordStore.Writes`) and the VFS's shadowed files. The records
// are not touched. Built on demand from the last load, so it is as current as the records are; the
// console's `mod_conflicts` prints it.
//
// Which mounts are mods: a mount whose name starts with `mods/` (ModMountPrefix). Phase 4j's boot
// mounts each mod's folder as `mods/<id>`; anything else (the engine, a kit's content, the game, a
// `--mounts` folder) is not a mod, so what it patches is an override, never a conflict.
[Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (4j-2): may change before 1.0
public sealed class ContentReport
{
    public const string ModMountPrefix = "mods/";

    private readonly List<ContentMountReport> _mounts = new();
    private readonly List<ContentConflict> _conflicts = new();

    private ContentReport() { }

    public IReadOnlyList<ContentMountReport> Mounts => _mounts;
    public IReadOnlyList<ContentConflict> Conflicts => _conflicts;

    public static bool IsModMount(IMount? mount) =>
        mount != null && mount.Name.Length > ModMountPrefix.Length && mount.Name.StartsWith(ModMountPrefix, StringComparison.OrdinalIgnoreCase);

    // How the report names a mount: a mod by its id ("mods/better_blades" is "better_blades"), any
    // other mount by its name.
    public static string NameOf(IMount? mount) =>
        mount == null ? "(no mount)" : IsModMount(mount) ? mount.Name[ModMountPrefix.Length..] : mount.Name;

    // Files every mount's copy of is read and merged rather than shadowed: record files, string
    // tables and the manifests at a mount's root.
    internal static bool IsMerged(VirtualPath path)
    {
        string p = path.Value;
        if (p is "mod.json" or "game.json") return true;
        return p.EndsWith(".json", StringComparison.Ordinal) &&
               (p.StartsWith("data/", StringComparison.Ordinal) || p.StartsWith("strings/", StringComparison.Ordinal));
    }

    public static ContentReport Build(RecordStore records, VirtualFileSystem vfs)
    {
        var report = new ContentReport();
        var byMount = new Dictionary<IMount, ContentMountReport>();
        foreach (var mount in vfs.Mounts)
        {
            var m = new ContentMountReport(mount);
            report._mounts.Add(m);
            byMount[mount] = m;
        }
        ContentMountReport? For(IMount? mount) => mount != null && byMount.TryGetValue(mount, out var m) ? m : null;

        foreach (var (type, id, source, disabled, writes) in records.Provenance()
                     .OrderBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Id.ToString(), StringComparer.Ordinal))
        {
            var definedBy = source.File.Mount;
            For(definedBy)?._added.Add($"{type} {id}");

            // Each mount's writes after the definition, in order: what it patched.
            foreach (var group in writes.Where(w => w.Op != RecordWriteOp.Define).GroupBy(w => w.Source.File.Mount))
                For(group.Key)?._patched.Add(new PatchedRecord(type, id, definedBy, group.Select(w => new RecordWrite(w, default)).ToList()));

            report.FindConflicts(type, id, disabled, writes);
        }

        foreach (var (type, id, source, existing) in records.Redefinitions)
            For(source.File.Mount)?._redefinitions.Add($"{type} {id} at {source.At()} (first defined at {existing.At()})");
        foreach (var (type, id, source) in records.SkippedPatches)
            For(source.File.Mount)?._skippedPatches.Add($"{type} {id} at {source.At()}");

        foreach (var shadowed in vfs.Shadows())
        {
            if (IsMerged(shadowed.Path)) continue;
            For(shadowed.Winner)?._shadows.Add(shadowed);
            var mods = shadowed.Shadowed.Append(shadowed.Winner).Where(IsModMount).ToList();
            if (mods.Count >= 2)
                report._conflicts.Add(new ContentConflict(ContentConflictKind.Asset, "asset", default, shadowed.Path.Value, mods, shadowed.Winner,
                    $"asset {shadowed.Path}: {string.Join(", ", mods.Select(NameOf))}; {NameOf(shadowed.Winner)} won"));
        }
        return report;
    }

    // Decision 5: the same value path written by two mods, or one mod's disable against another's
    // patch. A definition is not a write that conflicts (patching a record is how a mod changes it),
    // and `+`/`-` merge with each other, so only a set overlapping another mod's write is one.
    private void FindConflicts(string type, RecordId id, bool disabled, List<FieldWrite> writes)
    {
        List<FieldWrite>? mods = null;
        foreach (var w in writes)
            if (w.Op != RecordWriteOp.Define && IsModMount(w.Source.File.Mount)) (mods ??= new()).Add(w);
        if (mods == null) return;

        var disablers = mods.Where(w => w.Op == RecordWriteOp.Disable).Select(w => w.Source.File.Mount!).Distinct().ToList();
        if (disablers.Count > 0)
        {
            var patchers = mods.Where(w => w.Op != RecordWriteOp.Disable).Select(w => w.Source.File.Mount!).Distinct().Except(disablers).ToList();
            if (patchers.Count > 0)
            {
                var writers = mods.Select(w => w.Source.File.Mount!).Distinct().ToList();
                _conflicts.Add(new ContentConflict(ContentConflictKind.Disabled, type, id, "", writers, mods[^1].Source.File.Mount!,
                    $"{type} {id}: disabled by {string.Join(", ", disablers.Select(NameOf))}, patched by {string.Join(", ", patchers.Select(NameOf))}; " +
                    (disabled ? "removed" : "kept")));
            }
        }

        var found = new List<(string Path, List<IMount> Writers, IMount Winner)>();
        foreach (var set in mods)
        {
            if (set.Op != RecordWriteOp.Set || found.Any(f => f.Path == set.Path)) continue;
            // An add to a keyed list doesn't touch an entry another mod edits by key (issue #399).
            var touching = mods.Where(w => w.Op != RecordWriteOp.Disable && FieldWrite.Overlaps(w.Path, set.Path) &&
                                           !(w.Op == RecordWriteOp.Add && FieldWrite.IsIntoEntryOf(set.Path, w.Path))).ToList();
            var writers = touching.Select(w => w.Source.File.Mount!).Distinct().ToList();
            if (writers.Count < 2) continue;
            // One conflict for one clash: a set of "stats" against "stats.agility" is said once, at the
            // shorter path.
            var winner = touching[^1].Source.File.Mount!;
            int same = found.FindIndex(f => FieldWrite.Overlaps(f.Path, set.Path) && f.Writers.SequenceEqual(writers));
            if (same < 0) found.Add((set.Path, writers, winner));
            else if (set.Path.Length < found[same].Path.Length) found[same] = (set.Path, writers, winner);
        }
        foreach (var (path, writers, winner) in found)
            _conflicts.Add(new ContentConflict(ContentConflictKind.Value, type, id, path, writers, winner,
                $"{type} {id} {(path.Length == 0 ? "(record)" : path)}: {string.Join(", ", writers.Select(NameOf))}; {NameOf(winner)} won"));
    }

    // The report as text: the conflicts, then each mount's part. With `mount` (a mount's name or a
    // mod's id), only that mount, and the conflicts it is in.
    public IReadOnlyList<string> Lines(string? mount = null)
    {
        bool Matches(IMount m) => mount == null || string.Equals(m.Name, mount, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(NameOf(m), mount, StringComparison.OrdinalIgnoreCase);
        var lines = new List<string>();
        var mounts = _mounts.Where(m => Matches(m.Mount)).ToList();
        if (mount != null && mounts.Count == 0)
        {
            lines.Add($"no mount called '{mount}' (vfs_mounts lists them)");
            return lines;
        }

        var conflicts = _conflicts.Where(c => mount == null || c.Writers.Any(Matches)).ToList();
        lines.Add(conflicts.Count == 0 ? "No conflicts between mods." : $"{conflicts.Count} conflict(s) between mods (the later mod wins):");
        foreach (var c in conflicts) lines.Add($"  {c.Line}");

        foreach (var m in mounts)
        {
            lines.Add($"{NameOf(m.Mount)}{(m.IsMod ? " (mod)" : "")}: {m.Added.Count} added, {m.Patched.Count} patched, " +
                      $"{m.Shadows.Count} asset(s) shadowed, {m.Redefinitions.Count} redefinition(s), {m.SkippedPatches.Count} skipped patch(es)");
            foreach (var group in m.Added.GroupBy(a => a[..a.IndexOf(' ')]))
                lines.Add($"  added {group.Key}: {string.Join(", ", group.Select(a => a[(a.IndexOf(' ') + 1)..]))}");
            foreach (var p in m.Patched)
            {
                string whose = p.DefinedBy == m.Mount ? "" : m.IsMod && !IsModMount(p.DefinedBy) ? $" (overrides {NameOf(p.DefinedBy)})" : $" (from {NameOf(p.DefinedBy)})";
                lines.Add($"  patched {p.Type} {p.Id}{whose}: " +
                          string.Join(", ", p.Writes.Select(w => $"{w.Op.ToString().ToLowerInvariant()} {(w.Path.Length == 0 ? "(record)" : w.Path)}")));
            }
            foreach (var r in m.Redefinitions) lines.Add($"  redefined {r} (an error; applied as a patch)");
            foreach (var s in m.SkippedPatches) lines.Add($"  skipped a patch of {s}, which isn't defined");
            foreach (var s in m.Shadows) lines.Add($"  shadows {s.Path} in {string.Join(", ", s.Shadowed.Select(NameOf))}");
        }
        return lines;
    }

    public override string ToString() => string.Join('\n', Lines());
}
