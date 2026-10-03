#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// The save-version report (issue #285): what a load of a slot would have to do to it, read without loading
// it. A save carries two kinds of version (SaveSystem, "Versions"): the file format, which a format upgrader
// brings up to date, and each component's and saved resource's own, which its [Upgrade] methods do. The
// report lists both against this build — the format, and for every component and resource id in the slot
// its saved version, this build's version, how many entities have it and what a load does with it:
// nothing (Current), run its upgraders (Upgrade, with how many [Upgrade] steps that is), skip it because a
// newer game wrote it (Newer), or keep it as data this game has no type for (Unknown). And the plugins,
// content and mods that differ, as a load warns. `save_report <slot>` logs it; `save_report` gives one line
// a slot.
public sealed partial class SaveSystem
{
    // The report for one slot; null when there is no such save. A world file that cannot be read is named
    // in Problems, as the load would refuse it.
    [Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public SaveVersionReport? Report(string slot)
    {
        if (!Exists(slot)) return null;
        string directory = SlotDirectory(slot);
        var problems = new List<string>();
        JsonObject? header = null;
        try { header = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "header.json"))) as JsonObject; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { problems.Add($"header.json: {ex.Message}"); }
        if (header == null && problems.Count == 0) problems.Add("header.json is not a JSON object");

        int format = header?["formatVersion"] is JsonValue f && f.TryGetValue(out int n) ? n : 0;
        string engine = header?["engineVersion"] is JsonValue e && e.TryGetValue(out string? v) ? v ?? "" : "";
        bool readable = format >= OldestReadableFormat && format <= FormatVersion;

        // (kind, id, saved version) → entities (or worlds, for a resource) that have it.
        var counts = new Dictionary<(string Kind, string Id, int Version), int>();
        void Count(string kind, string id, JsonNode? entry)
        {
            int version = entry is JsonObject wrapper && wrapper["version"] is JsonValue sv && sv.TryGetValue(out int saved) ? saved : 0;
            var key = (kind, id, version);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        void Entities(JsonArray? list)
        {
            if (list == null) return;
            foreach (var node in list)
                if (node is JsonObject entity && entity["components"] is JsonObject components)
                    foreach (var (id, entry) in components) Count("component", id, entry);
        }

        if (readable)
        {
            foreach (string file in Directory.EnumerateFiles(directory, "world_*").Where(IsWorldFile).OrderBy(x => x, StringComparer.Ordinal))
            {
                try
                {
                    if (JsonNode.Parse(ReadWorldFile(file)) is not JsonObject root) { problems.Add($"{Path.GetFileName(file)} is not a JSON object"); continue; }
                    UpgradeWorld(root, format);   // a format 1 file names components by C# type: read it as a load would
                    Entities(root["entities"] as JsonArray);
                    if (root["dormant"] is JsonObject dormant)
                        foreach (var (_, cell) in dormant) Entities(cell?["entities"] as JsonArray);
                    if (root["resources"] is JsonObject resources)
                        foreach (var (name, entry) in resources) Count("resource", name, entry);
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException
                                              or InvalidOperationException or FormatException)
                {
                    problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        var entries = new List<SaveVersionEntry>(counts.Count);
        foreach (var ((kind, id, saved), count) in counts.OrderBy(c => c.Key.Kind, StringComparer.Ordinal)
                                                          .ThenBy(c => c.Key.Id, StringComparer.Ordinal).ThenBy(c => c.Key.Version))
        {
            Type? type = null;
            int? current = null;
            if (kind == "component")
            {
                if (_engine.Components.TryComponent(id, out var t)) type = t;
                else if (_engine.Components.TryFormerComponent(id, typeNames: false, out var former)) type = former.Type;
                if (type != null) current = _engine.Components.DeclarationOf(type)?.Version;
            }
            else if (_resources.TryGetValue(id, out var resource))
            {
                type = resource.Type;
                current = resource.Version;
            }

            var status = current is not { } now ? SaveVersionStatus.Unknown
                : saved == now ? SaveVersionStatus.Current
                : saved < now ? SaveVersionStatus.Upgrade
                : SaveVersionStatus.Newer;
            int steps = status == SaveVersionStatus.Upgrade && type != null
                ? Upgraders.Of(type).Keys.Count(from => from >= saved && from < current!.Value)
                : 0;
            entries.Add(new SaveVersionEntry(kind, id, saved, current, count, status, steps));
        }

        return new SaveVersionReport(Path.GetFileName(directory), format, engine, readable, entries,
                                     header != null ? Mismatches(header) : new List<string>(), problems);
    }

    private void RegisterReportCommands(CVarRegistry cvars)
    {
        cvars.RegisterCommand("save_report", CVarFlags.None,
            "save_report [slot]: what loading a save would upgrade, skip or keep (no slot: one line a save).", a =>
        {
            if (a.Count > 0)
            {
                if (Report(a[0]) is not { } report) { Log.Warn(LogCat.Console, $"No save '{a[0]}' in {Root}"); return; }
                foreach (string line in report.Lines()) Log.Info(LogCat.Console, line);
                return;
            }
            Rescan();
            if (Slots.Count == 0) { Log.Info(LogCat.Console, $"no saves in {Root}"); return; }
            foreach (var slot in Slots)
                if (Report(slot.Name) is { } report) Log.Info(LogCat.Console, report.Summary);
        });
    }
}

// What a load does with one saved component or resource version (issue #285).
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public enum SaveVersionStatus
{
    Current,   // saved at this build's version: read as it is
    Upgrade,   // older: its [Upgrade] methods bring it up to date as it loads
    Newer,     // a newer game wrote it: skipped, the entity keeps what its prefab gives it
    Unknown,   // this game has no such component or resource: kept as data, and written back on the next save
}

// One component or saved resource id at one saved version, in a SaveVersionReport. `Count`: the entities
// that have it (a resource: the worlds). `CurrentVersion`: this build's, null when it has no such type.
// `UpgradeSteps`: the [Upgrade] methods a load runs on it (a step with none is a version bump that needed
// no rewrite).
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly record struct SaveVersionEntry(string Kind, string Id, int SavedVersion, int? CurrentVersion, int Count,
                                               SaveVersionStatus Status, int UpgradeSteps);

// A slot's versions against this build (SaveSystem.Report, issue #285).
[Experimental("SAGE0131", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class SaveVersionReport
{
    internal SaveVersionReport(string slot, int formatVersion, string engineVersion, bool canLoad,
                               IReadOnlyList<SaveVersionEntry> entries, IReadOnlyList<string> mismatches, IReadOnlyList<string> problems)
    {
        Slot = slot;
        FormatVersion = formatVersion;
        EngineVersion = engineVersion;
        CanLoad = canLoad && problems.Count == 0;
        Entries = entries;
        Mismatches = mismatches;
        Problems = problems;
    }

    public string Slot { get; }

    // The file format it was written in, and whether a load upgrades it (older than SaveSystem.FormatVersion).
    public int FormatVersion { get; }
    public bool FormatUpgrade => FormatVersion < SaveSystem.FormatVersion;

    // The engine version that wrote it.
    public string EngineVersion { get; }

    // This build reads its format, and every file of it could be read.
    public bool CanLoad { get; }

    public IReadOnlyList<SaveVersionEntry> Entries { get; }

    // Plugins, content and mods that differ from what is loaded now, as a load warns.
    public IReadOnlyList<string> Mismatches { get; }

    // Files that could not be read: a load would refuse the save.
    public IReadOnlyList<string> Problems { get; }

    // Nothing to upgrade, skip or keep: the save is exactly this build's.
    public bool IsCurrent => !FormatUpgrade && FormatVersion == SaveSystem.FormatVersion && Problems.Count == 0
                             && Entries.All(e => e.Status == SaveVersionStatus.Current);

    public int Count(SaveVersionStatus status) => Entries.Where(e => e.Status == status).Sum(e => e.Count);

    // One line: the slot, its format and what a load does.
    public string Summary
    {
        get
        {
            string format = !CanLoad ? $"format {FormatVersion}, cannot load"
                : FormatUpgrade ? $"format {FormatVersion} -> {SaveSystem.FormatVersion}"
                : $"format {FormatVersion}";
            if (IsCurrent) return $"{Slot}: {format}, current";
            var parts = new List<string> { format };
            foreach (var status in new[] { SaveVersionStatus.Upgrade, SaveVersionStatus.Newer, SaveVersionStatus.Unknown })
            {
                int ids = Entries.Count(e => e.Status == status);
                if (ids > 0) parts.Add($"{ids} {status.ToString().ToLowerInvariant()}");
            }
            if (Mismatches.Count > 0) parts.Add($"{Mismatches.Count} mismatch(es)");
            return $"{Slot}: {string.Join(", ", parts)}";
        }
    }

    // The whole report, a line each, for the console.
    public IEnumerable<string> Lines()
    {
        yield return $"{Summary} (engine {(EngineVersion.Length > 0 ? EngineVersion : "unknown")}; this build reads " +
                     $"formats {SaveSystem.OldestReadableFormat} to {SaveSystem.FormatVersion})";
        foreach (string problem in Problems) yield return $"  cannot read {problem}";
        foreach (var e in Entries)
        {
            string now = e.CurrentVersion is { } c ? $"v{c}" : "-";
            string what = e.Status switch
            {
                SaveVersionStatus.Upgrade => e.UpgradeSteps > 0 ? $"upgrade ({e.UpgradeSteps} step(s))" : "upgrade (no rewrite)",
                SaveVersionStatus.Newer => "newer: skipped",
                SaveVersionStatus.Unknown => "unknown: kept as data",
                _ => "current",
            };
            yield return $"  {e.Kind,-9} {e.Id,-32} saved v{e.SavedVersion,-3} now {now,-4} x{e.Count,-5} {what}";
        }
        foreach (string mismatch in Mismatches) yield return $"  {mismatch}";
    }

    public override string ToString() => Summary;
}
