#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sage.Simulation;

// Mods at boot (phase 4j, issue 4j-3; docs/design/17-modding.md). SageApp.Create finds the mods, resolves
// their order (ModLoadOrder) and mounts each active one after the game's content as `mods/<id>`, in the
// record namespace `<id>` (decision 1). This is what it found and what it decided, and the player's
// choices for the next start: `user://mods.json` is written by mod_enable / mod_disable / mod_move (and a
// mods screen), and nothing changes until then — the VFS has no unmount, and records, the ECS schema and
// saves assume one fixed set of mounts (decision 3).
//
// Where mods are found (decision 2): `<game>/<modsDirectory>/*/mod.json` and `user://mods/*/mod.json`, and a
// `.sagemod` package beside those folders (issue #397, mounted as a ZipMount, never hot reloaded); the
// same id in both is an error, and the user's copy is used. SageAppOptions.Mods names the folders instead
// (the host's -mods and -nomods, a test, a tool). A broken mod is refused with a reason; the game boots.
//
// Code mods (phase 9, issue #396): a mod whose mod.json names "assemblies" has them loaded by LoadCode, at
// Create, before any module's Init and before the first world (so before registries seal and the ECS schema
// is built), each mod into a collectible load context of its own (ModCodeContext) that shares the engine's
// assemblies. Its modules are added after the game's, so they run after it unless their [RequiresPlugin]
// says otherwise, and register under their own plugin id (the mod's id, or `<mod id>.<name>`): the
// RegistrationLedger says what a mod's code registered. A mod whose code cannot load, has no module, names a
// plugin outside its id, or requires a plugin or an engine version this app lacks is refused like any
// broken mod. Code is trusted and not sandboxed; it is flagged "contains code" wherever mods are listed, and
// nothing here downloads a mod. The contexts are unloaded when the app is disposed (UnloadCode).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0132", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // data mods (phase 4j): may change before 1.0
public sealed class ModManager
{
    // The mount name every mod gets: `mods/<id>`. Saves (4j-4) and the conflict report (4j-2) tell a mod's
    // mount from the game's by it.
    public const string MountPrefix = "mods/";

    private readonly GameManifest _game;
    private readonly IReadOnlyList<string> _reserved;
    private readonly List<ModManifest> _candidates;   // the readable manifests Resolve chose among
    private readonly List<RefusedMod> _unresolved;    // refused before Resolve: unreadable, or an id found twice
    private readonly List<string> _searched;          // the folders looked in (or the folders named)
    private readonly ModList _order;                  // the order this run resolved with
    private readonly List<RefusedMod> _codeRefused = new();     // code mods whose code could not load (LoadCode)
    private readonly List<ModCodeContext> _code = new();        // the loaded code mods' contexts, in load order

    // An app with no game and no mods.
    internal static ModManager None() => new(new GameManifest { Id = "sage" }, Array.Empty<string>(), new List<ModManifest>(),
        new List<RefusedMod>(), new List<string>(), ModLoadResult.Empty, new ModList(), listFile: null, named: false, order: new ModList());

    private ModManager(GameManifest game, IReadOnlyList<string> reserved, List<ModManifest> candidates, List<RefusedMod> unresolved,
                       List<string> searched, ModLoadResult loaded, ModList choices, string? listFile, bool named, ModList order)
    {
        _order = order;
        _game = game;
        _reserved = reserved;
        _candidates = candidates;
        _unresolved = unresolved;
        _searched = searched;
        Loaded = loaded;
        Choices = choices;
        ListFile = listFile;
        Named = named;
    }

    // What this run loaded: the active mods in load order (the last one wins), the refused with a reason
    // each, the ones switched off, and notes. Engine.Mods is the same object.
    public ModLoadResult Loaded { get; private set; }

    // Every mod.json that could be read, whatever became of the mod, by id.
    public IReadOnlyList<ModManifest> Found => _candidates;

    // `user://mods.json`, where the player's choices are kept; null when the app keeps none (a test, a tool).
    public string? ListFile { get; }

    // The player's choices as they stand: what the next start will use. Changed by Enable, Disable and Move.
    public ModList Choices { get; }

    // Whether this run's mods were named (-mods, -nomods, SageAppOptions.Mods) rather than found. Choices
    // still apply to a start that finds them.
    public bool Named { get; }

    // Something was changed since boot that only a restart applies.
    public bool RestartNeeded { get; private set; }

    // The folders mods were looked for in, or the folders named.
    public IReadOnlyList<string> Searched => _searched;

    // What the next start would load with Choices, from the mods found now.
    public ModLoadResult Next() => Resolve(_candidates, Choices);

    // Switch a mod on or off for the next start, and write the list.
    public void Enable(string id)
    {
        Require(id);
        Choices.Disabled.RemoveAll(d => d == id);
        Remember(id);
        Save();
    }

    public void Disable(string id)
    {
        Require(id);
        if (!Choices.IsDisabled(id)) Choices.Disabled.Add(id);
        Remember(id);
        Save();
    }

    // Put a mod at `position` (1 is first) in the player's order, for the next start. The order is a
    // preference: a dependency, loadAfter or loadBefore still decides where they apply (decision 4).
    public void Move(string id, int position)
    {
        Require(id);
        var order = PlayerOrder();
        order.Remove(id);
        order.Insert(Math.Clamp(position - 1, 0, order.Count), id);
        Choices.Order.Clear();
        Choices.Order.AddRange(order);
        Save();
    }

    // The player's order with every mod found in it: the saved order first, then the rest as this run
    // loaded them, then by id.
    public List<string> PlayerOrder()
    {
        var known = _candidates.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var order = Choices.Order.Where(known.Contains).ToList();
        foreach (var mod in Loaded.Active) if (!order.Contains(mod.Id)) order.Add(mod.Id);
        foreach (string id in known.OrderBy(i => i, StringComparer.Ordinal)) if (!order.Contains(id)) order.Add(id);
        return order;
    }

    private void Require(string id)
    {
        if (_candidates.Any(m => m.Id == id)) return;
        string mods = _candidates.Count == 0 ? "none were found" : "mods: " + string.Join(", ", _candidates.Select(m => m.Id));
        throw new ArgumentException($"no mod '{id}' was found ({mods})");
    }

    // A mod the list didn't name joins it where it loads now, so a change doesn't reorder the rest.
    private void Remember(string id)
    {
        if (Choices.Order.Contains(id)) return;
        var order = PlayerOrder();
        Choices.Order.Clear();
        Choices.Order.AddRange(order);
    }

    private void Save()
    {
        if (ListFile == null)
            throw new InvalidOperationException("this app keeps no mod list (no user folder), so a change could not apply at a next start");
        Choices.Save(ListFile);
        RestartNeeded = true;
    }

    // ---- boot ----

    internal static ModManager Discover(SageAppOptions options, GameManifest? game, IEnumerable<string> reservedIds)
    {
        var manifest = game ?? new GameManifest { Id = "sage" };
        var reserved = reservedIds.Distinct(StringComparer.Ordinal).ToList();
        var searched = new List<string>();
        var unresolved = new List<RefusedMod>();
        var found = new List<ModManifest>();
        bool named = options.Mods != null;

        if (options.Mods is { } folders)
        {
            foreach (string folder in folders)
            {
                searched.Add(Path.GetFullPath(folder));
                Read(folder, found, unresolved);
            }
        }
        else
        {
            // The player's own mods first: of the same id twice, the first copy found is used.
            if (options.UserModsDirectory is { } user) Scan(user, searched, found, unresolved);
            if (game != null && !string.IsNullOrWhiteSpace(game.ModsDirectory))
                Scan(Path.Combine(game.Directory, game.ModsDirectory), searched, found, unresolved);
        }

        // The same id twice: an error, and the first copy (the player's, when it is one) is used.
        var candidates = new List<ModManifest>();
        foreach (var mod in found)
        {
            var first = candidates.FirstOrDefault(m => m.Id == mod.Id);
            if (first == null) { candidates.Add(mod); continue; }
            string reason = $"the id '{mod.Id}' is also the mod in {first.Directory}, whose copy is used";
            Log.Error(LogCat.Mods, $"Mod '{mod.Id}' is in two places ({first.Directory} and {mod.Directory}); {first.Directory} is used");
            unresolved.Add(new RefusedMod(mod.Id, mod.Directory, reason));
        }

        // The player's choices, unless the mods were named: then they load in the order given, all of them.
        ModList choices = new();
        if (options.ModListFile is { } listFile && File.Exists(listFile))
        {
            choices = ModList.Load(listFile, out string? warning);
            if (warning != null) Log.Warn(LogCat.Mods, warning);
        }
        var order = named ? new ModList { Order = candidates.Select(m => m.Id).ToList() } : choices;
        var manager = new ModManager(manifest, reserved, candidates, unresolved, searched, ModLoadResult.Empty, choices, options.ModListFile, named, order);
        manager.Loaded = manager.Resolve(candidates, order);
        return manager;
    }

    // A code mod whose code could not load stays out of this run's order and the next start's, refused with
    // why: the next start checks it again.
    private ModLoadResult Resolve(List<ModManifest> candidates, ModList order)
    {
        var broken = _codeRefused.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var result = ModLoadOrder.Resolve(candidates.Where(m => !broken.Contains(m.Id)), order, _game, BuildInfo.EngineSemVersion, _reserved);
        if (_unresolved.Count == 0 && _codeRefused.Count == 0) return result;
        return new ModLoadResult(result.Active, _unresolved.Concat(_codeRefused).Concat(result.Refused).ToList(), result.Disabled, result.Notes);
    }

    // ---- code mods (phase 9, issue #396) ----

    // Whether a mod is a code mod: mod.json names assemblies.
    public static bool ContainsCode(ModManifest mod) => mod.AsksForCode;

    // The assemblies this run loaded for a mod, empty for a data mod (or one whose code was refused).
    public IReadOnlyList<System.Reflection.Assembly> CodeOf(string id) =>
        _code.FirstOrDefault(c => c.Mod.Id == id)?.Loaded ?? (IReadOnlyList<System.Reflection.Assembly>)Array.Empty<System.Reflection.Assembly>();

    // The active code mods' load contexts, for a test to see them unload.
    internal IReadOnlyList<ModCodeContext> CodeContexts => _code;

    // Loads every active code mod's assemblies and returns their modules, for the app to add after its own
    // (`host`: every module it has added, the game's included). A mod whose code cannot be used is refused,
    // its context unloaded, and the order resolved again without it (a mod that needs it goes too). Called
    // once, by SageApp.Create, before any module's Init.
    internal IReadOnlyList<IModule> LoadCode(IReadOnlyList<IModule> host, HostKind hostKind)
    {
        while (true)
        {
            var failed = new List<RefusedMod>();
            var loaded = new List<ModCodeContext>();
            foreach (var mod in Loaded.Active.Where(m => m.AsksForCode))
            {
                var context = new ModCodeContext(mod);
                string? why = Try(() => context.LoadAll()) ?? CheckPlugins(context, host);
                if (why == null && EcsSchema.Built && context.EcsTypes().FirstOrDefault() is { } component)
                    why = $"it declares ECS components or tags ({component.Name}), and this process built its component schema before the mod " +
                          "was loaded (a second app in one process): start the game anew to load it";
                if (why != null) { failed.Add(new RefusedMod(mod.Id, mod.Directory, why)); context.Release(); }
                else loaded.Add(context);
            }
            if (failed.Count == 0) failed.AddRange(CheckRequirements(loaded, host));
            if (failed.Count == 0)
            {
                _code.AddRange(loaded);
                var modules = new List<IModule>();
                foreach (var context in loaded)
                {
                    var paths = string.Join(", ", context.Mod.Assemblies);
                    Log.Info(LogCat.Mods, $"Mod '{context.Mod.Id}' contains code ({paths}): loaded with the game's full trust, not sandboxed → " +
                                          string.Join(", ", context.Modules.Select(m => PluginInfo.Of(m).Id)));
                    foreach (var module in context.Modules)
                    {
                        if (SageApp.KindBelongs(module.Kind, hostKind)) modules.Add(module);
                        else Log.Info(LogCat.Modules, $"{module.Name} (mod '{context.Mod.Id}') is a {module.Kind} module; a {hostKind} host doesn't load it");
                    }
                }
                return modules;
            }
            foreach (var context in loaded) context.Release();
            foreach (var refused in failed)
                if (!_codeRefused.Any(r => r.Id == refused.Id)) _codeRefused.Add(refused);
            Loaded = Resolve(_candidates, _order);
        }
    }

    // The engine's and the mod's own words for what went wrong, or null.
    private static string? Try(Action load)
    {
        try { load(); return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } cause } ? cause : ex;
            return ReferenceEquals(ex, inner) && inner is InvalidDataException ? inner.Message : $"its code failed to load: {inner.Message}";
        }
    }

    // Every module of a code mod is a plugin of the mod's: [Plugin("<mod id>")] or [Plugin("<mod id>.<name>")],
    // so what it registers is the mod's in the ledger, and no mod can stand in for the game's plugins.
    private static string? CheckPlugins(ModCodeContext context, IReadOnlyList<IModule> host)
    {
        string id = context.Mod.Id;
        foreach (var module in context.Modules)
        {
            var type = module.GetType();
            if (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<PluginAttribute>(type) is not { } plugin)
                return $"its module {type.FullName} has no [Plugin(\"{id}\", version)], and a code mod's modules are plugins named for the mod";
            try { _ = PluginInfo.Of(module); }
            catch (FormatException ex) { return $"its module {type.FullName}: {ex.Message}"; }
            if (!plugin.Id.Equals(id, StringComparison.Ordinal) && !plugin.Id.StartsWith(id + ".", StringComparison.Ordinal))
                return $"its module {type.FullName} is the plugin '{plugin.Id}', and a code mod's plugins are named '{id}' or '{id}.<name>'";
            if (host.FirstOrDefault(m => PluginInfo.Of(m).Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase)) is { } taken)
                return $"its plugin '{plugin.Id}' is already {taken.GetType().FullName}'s";
            if (module is IGameModule)
                return $"its module {type.FullName} is an IGameModule, and a game has one, its own";
        }
        var twice = context.Modules.GroupBy(m => PluginInfo.Of(m).Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        return twice == null ? null : $"two of its modules are the plugin '{twice.Key}'";
    }

    // [RequiresPlugin] and IModule.Dependencies of the code mods' modules, against the app's plugins and the
    // other code mods': what would otherwise stop the boot (ModuleManager.Sort) refuses the mod instead.
    private static List<RefusedMod> CheckRequirements(List<ModCodeContext> loaded, IReadOnlyList<IModule> host)
    {
        var all = host.Concat(loaded.SelectMany(c => c.Modules)).ToList();
        var byId = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in all) byId.TryAdd(PluginInfo.Of(module).Id, PluginInfo.Of(module));
        var types = all.Select(m => m.GetType()).ToHashSet();
        var failed = new List<RefusedMod>();
        foreach (var context in loaded)
        {
            string? why = null;
            foreach (var module in context.Modules)
            {
                var info = PluginInfo.Of(module);
                foreach (var (id, range) in info.Requires)
                {
                    if (id.Equals(ModuleManager.EngineId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!range.Contains(BuildInfo.EngineSemVersion))
                            why ??= $"its plugin {info.Id} needs Sage {range}, and this is Sage {BuildInfo.EngineVersion}";
                    }
                    else if (!byId.TryGetValue(id, out var found))
                        why ??= $"its plugin {info.Id} requires the plugin {id} {range}, which this game does not load";
                    else if (!range.Contains(found.Version))
                        why ??= $"its plugin {info.Id} requires {id} {range}, and {id} {found.Version} is loaded";
                }
                foreach (var dependency in module.Dependencies.Where(d => !types.Contains(d)))
                    why ??= $"its plugin {info.Id} depends on the module {dependency.Name}, which this game does not load";
            }
            if (why != null) failed.Add(new RefusedMod(context.Mod.Id, context.Mod.Directory, why));
        }
        return failed;
    }

    // Unloads the code mods' load contexts: the app is disposed, and its modules have shut down. What the
    // contexts held is collected once nothing references the mods' types any more.
    internal void UnloadCode()
    {
        foreach (var context in _code) context.Release();
        _code.Clear();
    }

    // Every folder under `root` with a mod.json, and every `.sagemod` beside them (issue #397), by name. A
    // folder without a mod.json is not a mod, and is said so.
    private static void Scan(string root, List<string> searched, List<ModManifest> found, List<RefusedMod> unresolved)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return;
        searched.Add(root);
        var packages = Directory.GetFiles(root).Where(ModManifest.IsPackagePath);
        foreach (string folder in Directory.GetDirectories(root).Concat(packages).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!ModManifest.IsPackagePath(folder) && !File.Exists(Path.Combine(folder, "mod.json")))
            {
                Log.Warn(LogCat.Mods, $"{folder} has no mod.json, so it is not a mod (skipped)");
                continue;
            }
            Read(folder, found, unresolved);
        }
    }

    // A mod.json that cannot be read refuses the mod, by its folder's name: the game still boots.
    private static void Read(string folder, List<ModManifest> found, List<RefusedMod> unresolved)
    {
        try
        {
            found.Add(ModManifest.Load(folder));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            string full = Path.GetFullPath(folder);
            bool package = ModManifest.IsPackagePath(full);
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
            // A package that breaks a rule of ZipMount's (zip-slip, a limit) says which, by its message.
            unresolved.Add(new RefusedMod(package ? Path.GetFileNameWithoutExtension(name) : name, full,
                package ? $"its package can't be used: {ex.Message}" : $"its mod.json can't be read: {ex.Message}"));
        }
    }

    // Each active mod's folder or package, after everything else, in load order: the last one wins (05 §3.1).
    // A package was checked when it was found; one that has changed since and fails now is skipped, an error.
    internal void Mount(VirtualFileSystem vfs)
    {
        foreach (var mod in Loaded.Active)
        {
            if (!mod.IsPackage)
            {
                vfs.Mount(new FolderMount(MountPrefix + mod.Id, mod.Directory, mod.Id));
                continue;
            }
            try
            {
                vfs.Mount(new ZipMount(MountPrefix + mod.Id, mod.Directory, mod.Id));
            }
            catch (InvalidDataException ex)
            {
                Log.Error(LogCat.Mods, $"Mod '{mod.Id}' is not mounted: {ex.Message}");
            }
        }
    }

    // The Mods log at boot: one summary line, then a line per refusal and note.
    internal void LogSummary()
    {
        Log.Info(LogCat.Mods, Summary());
        foreach (var refused in Loaded.Refused)
            Log.Warn(LogCat.Mods, $"Mod '{refused.Id}' is not loaded: {refused.Reason}");
        foreach (string note in Loaded.Notes)
            Log.Info(LogCat.Mods, note);
    }

    public string Summary()
    {
        if (_candidates.Count == 0 && Loaded.Refused.Count == 0)
            return Named ? "Mods: none (named on the command line)" : $"Mods: none found{Where()}";
        var text = new StringBuilder($"Mods: {Loaded.Active.Count} active");
        if (Loaded.Active.Count > 0) text.Append(" (").Append(string.Join(", ", Loaded.Active.Select(m => $"{m.Id} {m.Version}"))).Append(')');
        text.Append($", {Loaded.Refused.Count} refused, {Loaded.Disabled.Count} switched off");
        if (Named) text.Append(" (named on the command line)");
        return text.ToString();
    }

    private string Where() => _searched.Count == 0 ? "" : $" (looked in {string.Join(", ", _searched)})";

    // `user://logs/mod_report.txt`, written once records have loaded: the load order, the switched off, the
    // refused and why, and the notes; then the content report (4j-2): conflicts between mods, and per mount
    // the records added and patched (a mod's override of the game marked), redefinitions, skipped patches
    // and shadowed assets — what `mod_conflicts` prints.
    internal void WriteBootReport(string path, RecordStore records, VirtualFileSystem vfs)
    {
        var text = new StringBuilder();
        text.AppendLine($"Mod report: {_game.Id}, Sage {BuildInfo.EngineVersion}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine(Summary());
        foreach (string line in ListLines()) text.AppendLine(line);
        text.AppendLine();
        foreach (string line in ContentReport.Build(records, vfs).Lines()) text.AppendLine(line);
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, text.ToString());
    }

    // What mod_list prints, and the report's list.
    public IEnumerable<string> ListLines()
    {
        if (_searched.Count > 0) yield return $"Looked in: {string.Join(", ", _searched)}";
        yield return $"Active, in load order ({Loaded.Active.Count}):";
        int n = 0;
        foreach (var mod in Loaded.Active)
            yield return $"  {++n}. {mod.Id} {mod.Version}{(mod.Name != mod.Id ? $" ({mod.Name})" : "")}{Code(mod)}  {mod.Directory}{(mod.IsPackage ? " (packed)" : "")}";
        if (Loaded.Disabled.Count > 0)
        {
            yield return $"Switched off ({Loaded.Disabled.Count}):";
            foreach (var mod in Loaded.Disabled) yield return $"  {mod.Id} {mod.Version}{Code(mod)}  {mod.Directory}";
        }
        if (Loaded.Refused.Count > 0)
        {
            yield return $"Refused ({Loaded.Refused.Count}):";
            foreach (var refused in Loaded.Refused) yield return $"  {refused.Id}: {refused.Reason}";
        }
        foreach (string note in Loaded.Notes) yield return $"Note: {note}";
    }

    // The flag a code mod carries wherever mods are listed.
    public const string ContainsCodeFlag = "[contains code: not sandboxed]";

    private static string Code(ModManifest mod) => mod.AsksForCode ? "  " + ContainsCodeFlag : "";

    // ---- console ----

    internal void RegisterCommands(CVarRegistry cvars)
    {
        cvars.RegisterCommand("mod_list", CVarFlags.None,
            "The mods: active in load order, switched off, and refused with the reason (phase 4j).", _ =>
        {
            Log.Info(LogCat.Console, Summary());
            foreach (string line in ListLines()) Log.Info(LogCat.Console, line);
            if (RestartNeeded) Log.Info(LogCat.Console, $"Changed since this start; the next start loads: {Describe(Next())}");
        });
        cvars.RegisterCommand("mod_order", CVarFlags.None,
            "The load order this run uses (the last mod wins), and the one the next start will.", _ =>
        {
            Log.Info(LogCat.Console, $"Load order: {Describe(Loaded)}");
            if (_candidates.Count > 0) Log.Info(LogCat.Console, $"Player's order: {string.Join(", ", PlayerOrder())}");
            if (RestartNeeded) Log.Info(LogCat.Console, $"Next start: {Describe(Next())}");
        });
        cvars.RegisterCommand("mod_enable", CVarFlags.None,
            "mod_enable <id>: switch a mod on in user://mods.json; applies at next start.", a =>
        {
            Enable(Id(a));
            Applies($"mod '{a[0]}' switched on");
        });
        cvars.RegisterCommand("mod_disable", CVarFlags.None,
            "mod_disable <id>: switch a mod off in user://mods.json; applies at next start.", a =>
        {
            Disable(Id(a));
            Applies($"mod '{a[0]}' switched off");
        });
        cvars.RegisterCommand("mod_move", CVarFlags.None,
            "mod_move <id> <n>: put a mod at place n (1 is first; later wins) in user://mods.json; applies at next start.", a =>
        {
            if (a.Count < 2 || !int.TryParse(a[1], out int position) || position < 1)
                throw new ArgumentException("usage: mod_move <id> <n>, n from 1 (first)");
            Move(a[0], position);
            Applies($"mod '{a[0]}' moved to {position}");
        });

        static string Id(ConsoleArgs a) => a.Count > 0 ? a[0] : throw new ArgumentException($"usage: {a.Name} <id>");
    }

    private void Applies(string what)
    {
        Log.Info(LogCat.Console, $"{what}; applies at next start ({ListFile}). Next start: {Describe(Next())}");
        if (Named) Log.Info(LogCat.Console, "This run's mods were named on the command line; the list is for a start without -mods.");
    }

    private static string Describe(ModLoadResult result) =>
        result.Active.Count == 0 ? "no mods" : string.Join(", ", result.Active.Select(m => m.Id));

    // ---- dev ----

    // Dev builds: a mod.json that changes (or a new mod) is mounted only at the next start, so say so.
    // Records and assets inside a mod hot reload as any folder mount's do (RecordHotReload, the client's).
    public IDisposable WatchManifests()
    {
        var watchers = new List<FileSystemWatcher>();
        var said = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in _searched)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                var w = new FileSystemWatcher(folder, "mod.json")
                {
                    IncludeSubdirectories = !Named,   // a mods folder: each mod's own, and new ones
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                void Changed(object _, FileSystemEventArgs e)
                {
                    if (said.TryAdd(e.FullPath, 0))
                        Log.Warn(LogCat.Mods, $"{e.FullPath} changed: restart to apply (mods are mounted once, at start)");
                }
                w.Changed += Changed; w.Created += Changed; w.Renamed += (s, e) => Changed(s, e);
                w.EnableRaisingEvents = true;
                watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
            {
                Log.Debug(LogCat.Mods, $"Can't watch {folder} for mod.json changes: {ex.Message}");
            }
        }
        return new Watchers(watchers);
    }

    private sealed class Watchers : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers;
        public Watchers(List<FileSystemWatcher> watchers) { _watchers = watchers; }
        public void Dispose()
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}
