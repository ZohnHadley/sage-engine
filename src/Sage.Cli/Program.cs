#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

// `sage <verb> ...` (src/Sage.Cli/Sage.Cli.csproj).
//
//   sage validate <game> [--mods <dir> ...] [--game-mods] [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>]
//   sage schema <game> [<game> ...] [--out <dir>] [--mods <dir> ...] [--game-mods] [--mounts <dir>[=<namespace>] ...]
//               [--engine-content <dir>] [--client <Sage.Client.dll>]
//   sage mods <game> [--mods <dir> ...] [--engine-content <dir>]
//   sage package <game> --out <dir> [--host <dir>] [--config <name>] [--no-validate]
//   sage new <template> [-o <dir>] [-n <name>] [--game <game folder or id>] [--dry-run] [<dotnet new options> ...]
//   sage run <game> [--config <name>] [--host <dir>] [--dry-run] [-- <host arguments> ...]
//
// --mods names a mod (a folder with a mod.json) or a folder of mods; each mod.json is read, the mods are
// ordered as the game would order them (dependencies, loadAfter, loadBefore, then the order given) and
// mounted after the game's as `mods/<id>`; a mod that would be refused at boot is reported. --game-mods adds
// the mods in the game's own `modsDirectory`. --mounts stays for a bare patch folder with no mod.json.
// `mods` lists the order and the refusals and prints the content report (what each mod added and patched,
// where mods conflict, which assets are shadowed): it uses the game's own mods too, exits 1 on a refusal
// or a content error, and a conflict is a warning (phase 4j, 4j-5).
//
// `validate` boots the game headlessly and runs every content check (issue #22). `schema` boots each
// game the same way and writes JSON Schemas for its records, components and parts, with the ids its
// content (and the mounted mods) loaded, into --out (default: <game>/schemas) (issue #21).
//
// `package` (issue #293) writes a folder a player runs: the Shipping host with the game beside it in game/
// (GamePackage.cs says what goes in). --host is the host's build output (default: the host beside this
// program, as in the Player package, else this repository's src/Sage.Host/bin/<config>/net8.0); --config is
// the configuration the game was built in (default Shipping). The written folder's content is then checked as
// `validate` would, with the host's Content/ as engine content, unless --no-validate.
//
// `new` (issue #297) is `dotnet new` with the Sage.Templates templates by short name (game, game-data, game-client,
// mod-data), and the steps a mod or a client half needs to fit the game --game names: a mod's schemas written and the
// mod validated against the game, a client half's dll added to game.json. `run` builds a Sage.Sdk game and starts it
// (`dotnet run`), or starts the host on a game folder with no project. Commands.cs has both; --dry-run prints the
// command instead of running it. Their exit code is the command's.
//
// Exit codes: 0 clean (warnings allowed), 1 content errors, 2 a usage mistake.
return args.Length == 0 ? Usage()
    : args[0] == "validate" ? Validate(args[1..])
    : args[0] == "schema" ? Schema(args[1..])
    : args[0] == "mods" ? Mods(args[1..])
    : args[0] == "package" ? Package(args[1..])
    : args[0] == "new" ? New(args[1..])
    : args[0] == "run" ? Run(args[1..])
    : Usage();

static int Usage()
{
    Console.Error.WriteLine("usage: sage validate <game folder> [--mods <dir> ...] [--game-mods] [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>]");
    Console.Error.WriteLine("       sage schema <game folder> [<game folder> ...] [--out <dir>] [--mods <dir> ...] [--game-mods] [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>] [--client <Sage.Client.dll>]");
    Console.Error.WriteLine("       sage mods <game folder> [--mods <dir> ...] [--engine-content <dir>]");
    Console.Error.WriteLine("       sage package <game folder> --out <dir> [--host <dir>] [--config <name>] [--no-validate]");
    Console.Error.WriteLine("       sage new <template> [-o <dir>] [-n <name>] [--game <game folder or id>] [--dry-run] [<dotnet new options> ...]");
    Console.Error.WriteLine("       sage run <game folder> [--config <name>] [--host <dir>] [--dry-run] [-- <host arguments> ...]");
    return 2;
}

// `dotnet new sage-<template>`, then what the template needs from the game --game names.
static int New(string[] args)
{
    if (Sage.Cli.NewRequest.Parse(args, out string? error) is not { } request)
    {
        Console.Error.WriteLine($"sage new: {error}. The templates (Sage.Templates):");
        foreach (var (name, template, what) in Sage.Cli.NewRequest.Templates)
            Console.Error.WriteLine($"  {name,-12} {template,-17} {what}");
        return 2;
    }

    var command = request.Command();
    if (request.DryRun)
    {
        Console.WriteLine(command);
        if (request.ClientModulePath() is { } planned) Console.WriteLine($"then add \"{planned}\" to {Path.Combine(request.GameDirectory!, "game.json")}'s \"modules\": {{ \"add\" }}");
        if (request.IsModData && request.GameDirectory != null) Console.WriteLine($"then sage schema and sage validate {request.GameDirectory} --mods {request.OutputDirectory ?? "(the new folder)"}");
        return 0;
    }
    int code = Exec(command);
    if (code != 0)
    {
        Console.WriteLine($"ERROR dotnet new {request.Template} failed. Is the template installed? dotnet new install Sage.Templates " +
                          "(or a local feed's Sage.Templates.<version>.nupkg: tools/pack_sdk.sh in the engine's repository)");
        return code;
    }

    if (request.ClientModulePath() is { } module)
    {
        string manifest = Path.Combine(request.GameDirectory!, "game.json");
        string text = File.ReadAllText(manifest);
        string added = Sage.Cli.GameJsonModules.Add(text, module);
        if (added != text) File.WriteAllText(manifest, added);
        Console.WriteLine(added != text ? $"{manifest}: \"modules\": {{ \"add\" }} now loads \"{module}\"" : $"{manifest} already loads \"{module}\"");
        Console.WriteLine($"Build and start the game with: sage run {request.GameDirectory}");
    }

    if (request.IsModData && request.GameDirectory != null)
    {
        string? mod = request.OutputDirectory;
        if (mod == null)
        {
            Console.WriteLine("WARN  no -o or -n: name the mod's folder to have its schemas written and it validated against the game");
            return 0;
        }
        // The schemas its .vscode/settings.json maps data/ and mod.json onto, with the game's ids and the mod's own.
        int schema = Schema(new[] { request.GameDirectory, "--mods", mod, "--out", Path.Combine(mod, "schemas") });
        int validate = Validate(new[] { request.GameDirectory, "--mods", mod });
        return Math.Max(schema, validate);
    }
    return 0;
}

// The game in the host: `dotnet run` on its Sage.Sdk project (which builds it), or the host with -game.
static int Run(string[] args)
{
    if (Sage.Cli.RunRequest.Parse(args) is not { } request) return Usage();
    if (request.Plan(Options.FindHost, out string? error) is not { } command)
    {
        Console.WriteLine($"ERROR {error}");
        return 1;
    }
    Console.WriteLine(command);
    return request.DryRun ? 0 : Exec(command);
}

// Starts a program with this console, waits for it and returns its exit code.
static int Exec(Sage.Cli.ProcessPlan plan)
{
    var start = new ProcessStartInfo(plan.FileName) { UseShellExecute = false };
    foreach (string arg in plan.Arguments) start.ArgumentList.Add(arg);
    if (plan.WorkingDirectory != null) start.WorkingDirectory = plan.WorkingDirectory;
    try
    {
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return process.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception ex)
    {
        Console.WriteLine($"ERROR could not start {plan.FileName}: {ex.Message}");
        return 1;
    }
}

static int Validate(string[] args)
{
    if (Options.Parse(args, allowOut: false) is not { Games.Count: 1 } options) return Usage();
    string game = options.Games[0];
    var report = Headless(() => ContentValidation.Run(options.For(game)));

    foreach (string warning in report.Warnings) Console.WriteLine($"WARN  {warning}");
    foreach (string error in report.Errors) Console.WriteLine($"ERROR {error}");
    Console.WriteLine($"{Path.GetFullPath(game)}: {report.Records} records, {report.Errors.Count} error(s), {report.Warnings.Count} warning(s)" +
                      (options.EngineContent != null ? $" (engine content: {options.EngineContent})" : " (no engine content found)"));
    return report.Ok ? 0 : 1;
}

// The load order, the refusals and the content report. Conflicts are warnings; a refused mod or a content
// error is exit code 1.
static int Mods(string[] args)
{
    if (Options.Parse(args, allowOut: false) is not { Games.Count: 1 } options) return Usage();
    string game = options.Games[0];
    var report = Headless(() => ContentValidation.Run(options.For(game, gameMods: true)));

    foreach (string line in report.ModLines) Console.WriteLine(line);
    Console.WriteLine();
    foreach (string line in report.ReportLines) Console.WriteLine(line);
    Console.WriteLine();
    // The refusals are listed above; the log's lines about them would say them twice.
    foreach (string warning in report.Warnings.Where(w => !w.StartsWith("Mods: Mod '", StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"WARN  {warning}");
    foreach (string error in report.Errors) Console.WriteLine($"ERROR {error}");
    int refused = report.Mods.Refused.Count;
    Console.WriteLine($"{Path.GetFullPath(game)}: {report.Mods.Active.Count} mod(s) active, {refused} refused, {report.Conflicts} conflict(s), " +
                      $"{report.Errors.Count} error(s)");
    return refused == 0 && report.Ok ? 0 : 1;
}

// Boots each game, collects what it declares and loads into one catalog, and writes the schemas. The
// files are written even when content has errors (the ids of a record that failed are missing), and
// the errors are printed and make the exit code 1, as `validate` would.
static int Schema(string[] args)
{
    if (Options.Parse(args, allowOut: true) is not { Games.Count: > 0 } options) return Usage();
    var catalog = new SchemaCatalog();
    int errors = 0;
    // The engine's client (its `audio` and `particles` parts), which nothing headless loads.
    if (options.Client == null || !catalog.AddAssemblyFile(options.Client))
        Console.WriteLine("WARN  no Sage.Client.dll found (build the host, or pass --client): the engine's client parts accept any options");
    foreach (string game in options.Games)
    {
        var report = Headless(() => ContentValidation.Run(options.For(game, catalog.Add)));
        foreach (string error in report.Errors) Console.WriteLine($"ERROR {error}");
        errors += report.Errors.Count;
        if (!report.Ok) continue;

        // The game's client half is not loaded headless; its declarations are read from its assembly,
        // if it has been built, so its parts are described too.
        foreach (string module in GameManifest.Load(game).ModuleAssemblies)
            if (!catalog.AddAssemblyFile(module))
                Console.WriteLine($"WARN  {module} is not built (or cannot be read): the parts it declares accept any options");
    }

    string output = options.Out ?? Path.Combine(options.Games[0], "schemas");
    var written = RecordSchemas.WriteTo(catalog, output);
    Console.WriteLine($"{Path.GetFullPath(output)}: {written.Count} schema file(s) for {catalog.RecordTypes.Count} record types, " +
                      $"{catalog.Components.Count} components and {catalog.Parts.Count} prefab parts; {errors} content error(s)");
    return errors == 0 ? 0 : 1;
}

// The game and a Shipping host, into one folder a player runs; then that folder's content validated.
static int Package(string[] args)
{
    string? game = null, output = null, host = null, config = "Shipping";
    bool validate = true;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out" when i + 1 < args.Length: output = args[++i]; break;
            case "--host" when i + 1 < args.Length: host = args[++i]; break;
            case "--config" when i + 1 < args.Length: config = args[++i]; break;
            case "--no-validate": validate = false; break;
            default:
                if (args[i].StartsWith("--", StringComparison.Ordinal) || game != null) return Usage();
                game = args[i];
                break;
        }
    }
    if (game == null || output == null) return Usage();
    host ??= Options.FindHost(config);
    if (host == null)
    {
        Console.WriteLine($"ERROR no {config} host found (beside this program, or src/Sage.Host/bin/{config}/net8.0 in a repository above here): " +
                          $"build it with dotnet build src/Sage.Host -c {config}, or pass --host <dir>");
        return 1;
    }

    var result = Sage.Cli.GamePackage.Run(new Sage.Cli.PackageOptions { GameDirectory = game, OutputDirectory = output, HostDirectory = host, Configuration = config });
    foreach (string error in result.Errors) Console.WriteLine($"ERROR {error}");
    if (!result.Ok) return 1;
    Console.WriteLine($"{Path.GetFullPath(output)}: {result.Files.Count} file(s), the {config} host from {Path.GetFullPath(host)} and the game in game/");
    if (!validate) return 0;

    string packaged = Path.Combine(output, "game");
    string content = Path.Combine(output, "Content");
    var report = Headless(() => ContentValidation.Run(new ValidateOptions
    {
        GameDirectory = packaged, EngineContentDirectory = Directory.Exists(content) ? content : null, AvailablePlugins = BasePlugins.All(),
    }));
    foreach (string warning in report.Warnings) Console.WriteLine($"WARN  {warning}");
    foreach (string error in report.Errors) Console.WriteLine($"ERROR {error}");
    Console.WriteLine($"{Path.GetFullPath(packaged)}: {report.Records} records, {report.Errors.Count} error(s), {report.Warnings.Count} warning(s)");
    return report.Ok ? 0 : 1;
}

// A headless run: a throwaway user folder (no config, saves or logs written into the game's) and a log
// that prints nothing (what matters is collected by the run's own sink).
static ValidationReport Headless(Func<ValidationReport> run)
{
    string user = Path.Combine(Path.GetTempPath(), "sage-cli-" + Guid.NewGuid().ToString("N"));
    UserPaths.Initialize("validate", user);
    Log.Initialize(new LogOptions { Stdout = false });
    try
    {
        return run();
    }
    finally
    {
        Log.Shutdown();
        try { Directory.Delete(user, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

sealed class Options
{
    public List<string> Games { get; } = new();
    public List<(string, string)> Mounts { get; } = new();
    public string? EngineContent { get; private set; }
    public string? Out { get; private set; }
    public string? Client { get; private set; }

    public List<string> Mods { get; } = new();
    public bool GameMods { get; private set; }

    public ValidateOptions For(string game, Action<Engine>? inspect = null, bool gameMods = false) => new()
    {
        GameDirectory = game, EngineContentDirectory = EngineContent, Mounts = Mounts, Inspect = inspect,
        Mods = Mods, GameMods = GameMods || gameMods,
        AvailablePlugins = BasePlugins.All(),
    };

    public static Options? Parse(string[] args, bool allowOut)
    {
        var options = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--engine-content" when i + 1 < args.Length:
                    options.EngineContent = args[++i];
                    break;
                case "--out" when allowOut && i + 1 < args.Length:
                    options.Out = args[++i];
                    break;
                case "--client" when allowOut && i + 1 < args.Length:
                    options.Client = args[++i];
                    break;
                case "--game-mods":
                    options.GameMods = true;
                    break;
                case "--mods":
                    // Every argument up to the next option is a mod folder, or a folder of mods.
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        options.Mods.Add(args[++i]);
                    break;
                case "--mounts":
                    // Every argument up to the next option is a mount: "mods/extra" (its folder name is its
                    // namespace) or "mods/extra=extra".
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        string spec = args[++i];
                        int eq = spec.LastIndexOf('=');
                        string dir = eq < 0 ? spec : spec[..eq];
                        string ns = eq < 0 ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir))).ToLowerInvariant() : spec[(eq + 1)..];
                        options.Mounts.Add((dir, ns));
                    }
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) return null;
                    options.Games.Add(args[i]);
                    break;
            }
        }
        options.EngineContent ??= FindEngineContent();
        if (allowOut) options.Client ??= FindClient();
        return options;
    }

    // The host's build output for a configuration: beside this program (the Player package keeps `sage` in each
    // host folder), or this repository's src/Sage.Host/bin/<config>/net8.0 above the current folder or this program.
    public static string? FindHost(string configuration)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Sage.Host.dll"))) return AppContext.BaseDirectory;
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "src", "Sage.Host", "bin", configuration, "net8.0");
                if (File.Exists(Path.Combine(candidate, "Sage.Host.dll"))) return candidate;
            }
        return null;
    }

    // The engine's client assembly: beside this program (a packaged host), or the repository's host build
    // of this configuration, where MonoGame sits beside it.
    private static string? FindClient()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "Sage.Client.dll");
        if (File.Exists(beside)) return beside;
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "src", "Sage.Host", "bin", BuildInfo.ConfigurationName, "net8.0", "Sage.Client.dll");
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    // A host's Content/ beside this program, or the repository's engine_content/ above it or the current folder.
    private static string? FindEngineContent()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, "Content");
        if (Directory.Exists(beside)) return beside;
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "engine_content");
                if (Directory.Exists(candidate)) return candidate;
            }
        return null;
    }
}
