#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sage_engine;

// `sage <verb> ...` (src/Sage.Cli/Sage.Cli.csproj).
//
//   sage validate <game> [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>]
//   sage schema <game> [<game> ...] [--out <dir>] [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>]
//               [--client <Sage.Client.dll>]
//
// `validate` boots the game headlessly and runs every content check (issue #22). `schema` boots each
// game the same way and writes JSON Schemas for its records, components and parts, with the ids its
// content (and the mounted mods) loaded, into --out (default: <game>/schemas) (issue #21).
//
// Exit codes: 0 clean (warnings allowed), 1 content errors, 2 a usage mistake.
return args.Length == 0 ? Usage()
    : args[0] == "validate" ? Validate(args[1..])
    : args[0] == "schema" ? Schema(args[1..])
    : Usage();

static int Usage()
{
    Console.Error.WriteLine("usage: sage validate <game folder> [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>]");
    Console.Error.WriteLine("       sage schema <game folder> [<game folder> ...] [--out <dir>] [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>] [--client <Sage.Client.dll>]");
    return 2;
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

    public ValidateOptions For(string game, Action<Engine>? inspect = null) => new()
    {
        GameDirectory = game, EngineContentDirectory = EngineContent, Mounts = Mounts, Inspect = inspect,
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
