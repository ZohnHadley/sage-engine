#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sage.Core;

namespace Sage.Host;

// What the host's `-options` ask for (docs/design/01 §5.1 step 1), read from the launch arguments with
// every complaint as a warning to log, never a throw: a mistyped option should not stop a game. The
// boot itself stays in Program.cs; this is the part with rules, kept apart so the headless tests can
// run it (issue #298: they compile this file in, since a test may not reference the host).
internal sealed class HostOptions
{
    public static readonly string[] Known = { "game", "dump-registry", "mods", "nomods", "edit" };

    // -game <folder>: null when not given, and GameManifest.Locate decides.
    public string? Game { get; private init; }

    // Mods (phase 4j): null loads what is installed; `-mods <dir>[,<dir>]` exactly those folders, in that
    // order, as full paths; `-nomods` none at all (a clean run, whatever is installed).
    public IReadOnlyList<string>? Mods { get; private init; }

    // -dump-registry <file>: boot, write everything registered as JSON (RegistryDump, issue #18) and quit.
    // Resolved against the directory it was typed in, before anything changes it.
    public string? DumpRegistry { get; private init; }

    // -edit [placements-or-scene]: the editor (phase 10a, issue #219); "" opens it on the game's scene.
    // A Shipping host has no editor, which Program.cs says.
    public string? Edit { get; private init; }

    public IReadOnlyList<string> Warnings { get; private init; } = Array.Empty<string>();

    public static HostOptions From(LaunchArgs launch)
    {
        var warnings = new List<string>();
        foreach (var option in launch.Options.Keys.Where(o => !Known.Contains(o, StringComparer.OrdinalIgnoreCase)))
            warnings.Add($"Unknown launch option -{option} (ignored)");

        IReadOnlyList<string>? mods = null;
        if (launch.Options.ContainsKey("nomods"))
        {
            mods = Array.Empty<string>();
            if (launch.Options.ContainsKey("mods")) warnings.Add("-nomods and -mods together: -nomods wins, no mods load");
        }
        else if (launch.Options.TryGetValue("mods", out var modsOption))
        {
            if (string.IsNullOrWhiteSpace(modsOption)) warnings.Add("-mods needs folders, -mods <dir>[,<dir>] (ignored)");
            else mods = modsOption.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                  .Select(Path.GetFullPath).ToArray();
        }

        string? dump = launch.Options.TryGetValue("dump-registry", out var dumpPath) && !string.IsNullOrEmpty(dumpPath)
            ? Path.GetFullPath(dumpPath) : null;
        if (launch.Options.ContainsKey("dump-registry") && dump == null)
            warnings.Add("-dump-registry needs a file name (ignored)");

        return new HostOptions
        {
            Game = launch.Options.GetValueOrDefault("game"),
            Mods = mods,
            DumpRegistry = dump,
            Edit = launch.Options.TryGetValue("edit", out var edit) ? edit ?? "" : null,
            Warnings = warnings,
        };
    }
}
