#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using sage_engine;

// `sage <verb> ...` (src/Sage.Cli/Sage.Cli.csproj). One verb so far; `sage schema` is planned (#21).
//
//   sage validate <game> [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>]
//
// Exit codes: 0 clean (warnings allowed), 1 content errors, 2 a usage mistake.
return args.Length > 0 && args[0] == "validate" ? Validate(args[1..]) : Usage();

static int Usage()
{
    Console.Error.WriteLine("usage: sage validate <game folder> [--mounts <dir>[=<namespace>] ...] [--engine-content <dir>]");
    return 2;
}

static int Validate(string[] args)
{
    string? game = null, engineContent = null;
    var mounts = new List<(string, string)>();
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--engine-content" when i + 1 < args.Length:
                engineContent = args[++i];
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
                    mounts.Add((dir, ns));
                }
                break;
            default:
                if (args[i].StartsWith("--", StringComparison.Ordinal) || game != null) return Usage();
                game = args[i];
                break;
        }
    }
    if (game == null) return Usage();
    engineContent ??= FindEngineContent();

    // A throwaway user folder: validating writes no config, saves or logs into the game's.
    string user = Path.Combine(Path.GetTempPath(), "sage-validate-" + Guid.NewGuid().ToString("N"));
    UserPaths.Initialize("validate", user);
    Log.Initialize(new LogOptions { Stdout = false });
    ValidationReport report;
    try
    {
        report = ContentValidation.Run(new ValidateOptions { GameDirectory = game, EngineContentDirectory = engineContent, Mounts = mounts });
    }
    finally
    {
        Log.Shutdown();
        try { Directory.Delete(user, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    foreach (string warning in report.Warnings) Console.WriteLine($"WARN  {warning}");
    foreach (string error in report.Errors) Console.WriteLine($"ERROR {error}");
    Console.WriteLine($"{Path.GetFullPath(game)}: {report.Records} records, {report.Errors.Count} error(s), {report.Warnings.Count} warning(s)" +
                      (engineContent != null ? $" (engine content: {engineContent})" : " (no engine content found)"));
    return report.Ok ? 0 : 1;
}

// A host's Content/ beside this program, or the repository's engine_content/ above it or the current folder.
static string? FindEngineContent()
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
