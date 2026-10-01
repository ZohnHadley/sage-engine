#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sage.Core;

// A mod that is not loaded, and why (said to the player as written).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
public readonly record struct RefusedMod(string Id, string? Folder, string Reason);

// The result of ModLoadOrder.Resolve: the active mods in load order (the last one wins), the refused,
// the ones the user switched off, and notes (a check that was skipped).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
public sealed class ModLoadResult
{
    public static readonly ModLoadResult Empty = new(Array.Empty<ModManifest>(), Array.Empty<RefusedMod>(), Array.Empty<ModManifest>(), Array.Empty<string>());

    public ModLoadResult(IReadOnlyList<ModManifest> active, IReadOnlyList<RefusedMod> refused, IReadOnlyList<ModManifest> disabled, IReadOnlyList<string> notes)
    {
        Active = active; Refused = refused; Disabled = disabled; Notes = notes;
    }

    public IReadOnlyList<ModManifest> Active { get; }
    public IReadOnlyList<RefusedMod> Refused { get; }
    public IReadOnlyList<ModManifest> Disabled { get; }
    public IReadOnlyList<string> Notes { get; }
}

// Which mods load, and in what order (phase 4j; decision 4). A stable topological sort over dependencies,
// loadAfter and loadBefore; among mods free to go next, the user's order decides, then the id. A broken mod
// is refused with a reason and the rest still load: this never throws for a mod's fault.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
public static class ModLoadOrder
{
    // reservedIds: namespaces a mod may not take besides "sage" and the game's id: the plugins' content
    // namespaces ("rpg"). engine is the version the `sage` ranges are checked against.
    public static ModLoadResult Resolve(IEnumerable<ModManifest> found, ModList? userList, GameManifest game, SemVersion engine,
                                        IEnumerable<string>? reservedIds = null)
    {
        userList ??= new ModList();
        var reserved = new HashSet<string>(StringComparer.Ordinal) { "sage", game.Id };
        if (reservedIds != null) foreach (string id in reservedIds) reserved.Add(id);

        var notes = new List<string>();
        var refused = new List<RefusedMod>();
        var disabled = new List<ModManifest>();
        var candidates = new Dictionary<string, ModManifest>(StringComparer.Ordinal);
        SemVersion? gameVersion = null;
        if (!string.IsNullOrWhiteSpace(game.Version)) gameVersion = SemVersion.Parse(game.Version, "game.json \"version\"");

        var seen = new Dictionary<string, ModManifest>(StringComparer.Ordinal);
        foreach (var mod in found.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            string? reason = Static(mod, game, gameVersion, engine, reserved, notes);
            if (seen.TryGetValue(mod.Id, out var first))
                reason = $"another mod with the id '{mod.Id}' was found first ({first.Directory})";
            else
                seen[mod.Id] = mod;
            if (reason != null) refused.Add(new RefusedMod(mod.Id, mod.Directory, reason));
            else if (userList.IsDisabled(mod.Id)) disabled.Add(mod);
            else candidates[mod.Id] = mod;
        }
        var disabledIds = disabled.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

        int Rank(string id)
        {
            int i = userList.Order.IndexOf(id);
            return i < 0 ? int.MaxValue : i;
        }

        // Each pass removes what cannot load; removing one can break a dependent, so go until nothing changes.
        List<ModManifest> sorted;
        while (true)
        {
            bool changed = false;
            foreach (var mod in candidates.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            {
                string? reason = DependencyProblem(mod, candidates, disabledIds, refused);
                if (reason == null) continue;
                refused.Add(new RefusedMod(mod.Id, mod.Directory, reason));
                candidates.Remove(mod.Id);
                changed = true;
            }
            if (changed) continue;

            sorted = Sort(candidates, Rank, out var stuck);
            if (stuck.Count > 0)
            {
                var cycles = stuck.OrderBy(m => m.Id, StringComparer.Ordinal)
                                  .Select(m => (Mod: m, Cycle: FindCycle(m.Id, candidates))).ToList();
                foreach (var (mod, cycle) in cycles)
                {
                    refused.Add(new RefusedMod(mod.Id, mod.Directory, $"it is part of a load order cycle: {string.Join(" -> ", cycle)}"));
                    candidates.Remove(mod.Id);
                }
                continue;
            }

            // Incompatible with an earlier mod (either one naming the other): the later one is out.
            var loaded = new List<ModManifest>();
            foreach (var mod in sorted)
            {
                var clash = loaded.FirstOrDefault(e => mod.Incompatible.Contains(e.Id) || e.Incompatible.Contains(mod.Id));
                if (clash == null) { loaded.Add(mod); continue; }
                refused.Add(new RefusedMod(mod.Id, mod.Directory, $"it is incompatible with '{clash.Id}', which loads before it"));
                candidates.Remove(mod.Id);
                changed = true;
                break;
            }
            if (!changed) break;
        }
        return new ModLoadResult(sorted, refused, disabled.OrderBy(m => m.Id, StringComparer.Ordinal).ToList(), notes);
    }

    // What is wrong with a mod on its own, before any other mod is looked at.
    private static string? Static(ModManifest mod, GameManifest game, SemVersion? gameVersion, SemVersion engine,
                                  HashSet<string> reserved, List<string> notes)
    {
        if (reserved.Contains(mod.Id))
            return $"'{mod.Id}' is the id of the engine, the game or a kit's content, so a mod may not take it";
        if (mod.AsksForCode)
            return "code mods are phase 9: a mod is data only for now (it names \"assemblies\" or \"kind\": \"code\")";
        if (!string.IsNullOrWhiteSpace(mod.Game) && mod.Game != "*" && !string.Equals(mod.Game, game.Id, StringComparison.Ordinal))
            return $"it is for the game '{mod.Game}', and this is '{game.Id}'";
        if (!string.IsNullOrWhiteSpace(mod.GameVersion))
        {
            if (gameVersion == null)
                notes.Add($"mod '{mod.Id}': \"gameVersion\" {mod.GameVersion} was not checked, because game.json has no \"version\"");
            else if (!VersionRange.Parse(mod.GameVersion, "\"gameVersion\"").Contains(gameVersion.Value))
                return $"it was made for game version {mod.GameVersion}, and this is {gameVersion.Value}";
        }
        if (!string.IsNullOrWhiteSpace(mod.Sage) && !VersionRange.Parse(mod.Sage, "\"sage\"").Contains(engine))
            return $"it was made for Sage {mod.Sage}, and this is Sage {engine}";
        return null;
    }

    private static string? DependencyProblem(ModManifest mod, Dictionary<string, ModManifest> candidates,
                                             HashSet<string> disabledIds, List<RefusedMod> refused)
    {
        foreach (var (dep, rangeText) in mod.Dependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (!candidates.TryGetValue(dep, out var found))
            {
                string why = disabledIds.Contains(dep) ? "it is switched off"
                           : refused.Any(r => r.Id == dep) ? $"it is refused: {refused.First(r => r.Id == dep).Reason}"
                           : "it is not installed";
                return $"it needs '{dep}', and {why}";
            }
            var range = VersionRange.Parse(rangeText ?? "", $"\"dependencies\" '{dep}'");
            if (!range.Contains(found.SemVersion))
                return $"it needs '{dep}' {range}, and the one installed is {found.Version}";
        }
        return null;
    }

    // Edges run from the mod that loads first to the one that loads later.
    private static Dictionary<string, HashSet<string>> Edges(Dictionary<string, ModManifest> mods)
    {
        var after = mods.Keys.ToDictionary(id => id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal); // id -> mods loading after it
        foreach (var mod in mods.Values)
        {
            foreach (string dep in mod.Dependencies.Keys.Concat(mod.LoadAfter))
                if (mods.ContainsKey(dep) && dep != mod.Id) after[dep].Add(mod.Id);
            foreach (string next in mod.LoadBefore)
                if (mods.ContainsKey(next) && next != mod.Id) after[mod.Id].Add(next);
        }
        return after;
    }

    private static List<ModManifest> Sort(Dictionary<string, ModManifest> mods, Func<string, int> rank, out List<ModManifest> stuck)
    {
        var after = Edges(mods);
        var waiting = mods.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var targets in after.Values) foreach (string t in targets) waiting[t]++;
        var ready = new SortedSet<(int, string)>(Comparer<(int, string)>.Create((a, b) =>
            a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : string.CompareOrdinal(a.Item2, b.Item2)));
        foreach (var (id, n) in waiting) if (n == 0) ready.Add((rank(id), id));
        var result = new List<ModManifest>();
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            result.Add(mods[next.Item2]);
            foreach (string t in after[next.Item2])
                if (--waiting[t] == 0) ready.Add((rank(t), t));
        }
        // What is left is in a cycle or waits on one; only the cycle's own members are the fault.
        var left = mods.Keys.Where(id => waiting[id] > 0).ToList();
        stuck = left.Where(id => FindCycle(id, mods).Count > 0).Select(id => mods[id]).ToList();
        if (stuck.Count == 0 && left.Count > 0) stuck = left.Select(id => mods[id]).ToList();
        return result;
    }

    // A path from id back to itself along "loads before" edges, or empty if it is not in a cycle.
    private static List<string> FindCycle(string start, Dictionary<string, ModManifest> mods)
    {
        var after = Edges(mods);
        var path = new List<string> { start };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool Walk(string at)
        {
            foreach (string next in after[at].OrderBy(x => x, StringComparer.Ordinal))
            {
                if (next == start) { path.Add(start); return true; }
                if (!seen.Add(next)) continue;
                path.Add(next);
                if (Walk(next)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
        return Walk(start) ? path : new List<string>();
    }
}
