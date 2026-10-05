#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Sage.Cli;

// A program to start: what `sage new` and `sage run` hand to `dotnet` (issue #297). Planned here, started by
// Program.cs, so what each verb would run is tested without running it.
internal sealed record ProcessPlan(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null)
{
    // As a person would type it, for --dry-run and for the line printed before a run.
    public override string ToString() => string.Join(" ", new[] { FileName }.Concat(Arguments).Select(Quote));

    private static string Quote(string arg) => arg.Length > 0 && arg.All(c => !char.IsWhiteSpace(c) && c != '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    // The `dotnet` that runs this program (DOTNET_HOST_PATH under `dotnet run`/MSBuild), else the one on PATH.
    public static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
}

// `sage new <template> [-o <dir>] [-n <name>] [--game <game folder or id>] [--dry-run] [<dotnet new options> ...]`:
// `dotnet new sage-<template>`, the templates of the Sage.Templates package. A short name (game, game-data,
// game-client, mod-data) or the full one (sage-game, ...). Whatever this does not know goes to `dotnet new` as it
// is (--feed, --sdkVersion, ...). --game is for the two templates that belong to a game:
//   mod-data     a game folder: its id is the mod's "game", its path the one the mod's .vscode/tasks.json checks
//                against, and after `dotnet new` the mod's schemas are written (sage schema <game> --mods <mod>
//                --out <mod>/schemas, what its .vscode/settings.json maps data/ and mod.json onto) and the mod
//                validated against the game. A bare id is only the mod's "game".
//   game-client  a game folder: the client half goes in <game>/Client (unless -o says where), is named after the
//                game's folder (unless -n), and game.json's "modules": { "add" } gets its dll.
internal sealed class NewRequest
{
    public static readonly IReadOnlyList<(string Short, string Template, string What)> Templates = new[]
    {
        ("game", "sage-game", "a game with a simulation half and a client half"),
        ("game-data", "sage-game-data", "a game with no C#: game.json and records"),
        ("game-client", "sage-game-client", "a client half (a HUD, screens) for a game that has none, in its Client/ folder"),
        ("mod-data", "sage-mod-data", "a data mod: mod.json, records, schemas mapped for VS Code"),
    };

    public string Template { get; private init; } = "";
    public string? Output { get; private set; }
    public string? Name { get; private set; }
    // The game folder --game named (with a game.json), or null.
    public string? GameDirectory { get; private set; }
    // The game's id: game.json's, or --game itself when it is not a folder.
    public string? GameId { get; private set; }
    public bool DryRun { get; private set; }
    public List<string> PassThrough { get; } = new();

    public bool IsModData => Template == "sage-mod-data";
    public bool IsGameClient => Template == "sage-game-client";

    // Null and a message for a usage mistake.
    public static NewRequest? Parse(string[] args, out string? error)
    {
        error = null;
        if (args.Length == 0 || args[0].StartsWith("-", StringComparison.Ordinal))
        {
            error = "name a template";
            return null;
        }
        string named = args[0];
        var entry = Templates.FirstOrDefault(t => t.Short == named || t.Template == named);
        if (entry.Template == null)
        {
            error = $"there is no template \"{named}\"";
            return null;
        }

        var request = new NewRequest { Template = entry.Template };
        string? game = null;
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            bool hasValue = i + 1 < args.Length;
            switch (arg)
            {
                case "-o" or "--output" when hasValue: request.Output = args[++i]; break;
                case "-n" or "--name" when hasValue: request.Name = args[++i]; break;
                case "--game" when hasValue: game = args[++i]; break;
                case "--dry-run": request.DryRun = true; break;
                case "-o" or "--output" or "-n" or "--name" or "--game":
                    error = $"{arg} needs a value";
                    return null;
                default: request.PassThrough.Add(arg); break;
            }
        }

        if (game != null)
        {
            if (!request.IsModData && !request.IsGameClient)
            {
                error = $"--game is for mod-data and game-client, not {entry.Short}";
                return null;
            }
            if (File.Exists(Path.Combine(game, "game.json")))
            {
                request.GameDirectory = Path.GetFullPath(game);
                try
                {
                    request.GameId = GameManifest.Load(game).Id;
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or FormatException)
                {
                    error = $"{Path.Combine(game, "game.json")}: {ex.Message}";
                    return null;
                }
            }
            else if (request.IsGameClient)
            {
                error = $"--game names the game folder the client half is for, and {Path.GetFullPath(game)} has no game.json";
                return null;
            }
            else request.GameId = game;
        }

        if (request.IsGameClient && request.GameDirectory != null)
        {
            request.Output ??= Path.Combine(request.GameDirectory, "Client");
            request.Name ??= Path.GetFileName(Path.TrimEndingDirectorySeparator(request.GameDirectory));
        }
        return request;
    }

    // The folder `dotnet new` writes: -o, else (the template prefers one) a folder named after -n here.
    public string? OutputDirectory => Output != null ? Path.GetFullPath(Output) : Name != null ? Path.GetFullPath(Name) : null;

    // `dotnet new <template> ...`.
    public ProcessPlan Command()
    {
        var args = new List<string> { "new", Template };
        if (Output != null) args.AddRange(new[] { "-o", Output });
        if (Name != null) args.AddRange(new[] { "-n", Name });
        if (GameId != null && IsModData) args.AddRange(new[] { "--game", GameId });
        // Where the game is, from where the new folder is: the mod's tasks check against it, the client half
        // builds into it.
        if (GameDirectory != null && OutputDirectory is { } output)
            args.AddRange(new[] { "--gameDir", Path.GetRelativePath(output, GameDirectory).Replace('\\', '/') });
        args.AddRange(PassThrough);
        return new ProcessPlan(ProcessPlan.Dotnet, args);
    }

    // The client half's dll as game.json names it: <output>/bin/{config}/<Name>.Client.dll, relative to the game.
    public string? ClientModulePath() =>
        IsGameClient && GameDirectory != null && OutputDirectory is { } output
            ? Path.GetRelativePath(GameDirectory, Path.Combine(output, "bin", "{config}", (Name ?? Path.GetFileName(output)) + ".Client.dll")).Replace('\\', '/')
            : null;
}

// `sage run <game> [--config <name>] [--host <dir>] [--dry-run] [-- <host arguments> ...]`: the game in the host,
// built first when it is a Sage.Sdk game (issue #297).
//   - a Sage.Sdk project in <game>/Client (a client half added with sage-game-client): `dotnet run` on it; it
//     references the game folder's own project, so both are built, and runs the host on the game folder;
//   - else a Sage.Sdk project in <game> (the templates, games/Hello): `dotnet run` on it;
//   - else (a game folder with no project of the SDK's: data only, a packaged game/, the Sandbox, which the
//     solution builds): the host of --config (--host, else found as `sage package` finds it) with -game <game>.
// What follows `--` goes to the host: `+map e1m1`, `-edit level`, `-nomods`. --config defaults to this program's
// own configuration (the Player package keeps a `sage` beside each configuration's host).
internal sealed class RunRequest
{
    public string Game { get; private init; } = "";
    public string Configuration { get; private set; } = BuildInfo.ConfigurationName;
    public string? Host { get; private set; }
    public bool DryRun { get; private set; }
    public List<string> HostArguments { get; } = new();

    public static RunRequest? Parse(string[] args)
    {
        string? game = null;
        var request = new RunRequest();
        var hostArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--": hostArgs.AddRange(args[(i + 1)..]); i = args.Length; break;
                case "--config" when i + 1 < args.Length: request.Configuration = args[++i]; break;
                case "--host" when i + 1 < args.Length: request.Host = args[++i]; break;
                case "--dry-run": request.DryRun = true; break;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal) || game != null) return null;
                    game = args[i];
                    break;
            }
        }
        if (game == null) return null;
        var parsed = new RunRequest { Game = Path.GetFullPath(game), Configuration = request.Configuration, Host = request.Host, DryRun = request.DryRun };
        parsed.HostArguments.AddRange(hostArgs);
        return parsed;
    }

    // The Sage.Sdk project `dotnet run` starts, or null when the host is started directly.
    public static string? SdkProject(string game)
    {
        foreach (string folder in new[] { Path.Combine(game, "Client"), game })
        {
            if (!Directory.Exists(folder)) continue;
            var projects = Directory.GetFiles(folder, "*.csproj").Where(UsesSageSdk).ToList();
            if (projects.Count == 1) return projects[0];
        }
        return null;
    }

    // `<Project Sdk="Sage.Sdk/...">`, or the SDK's files imported by path (games/Hello).
    private static bool UsesSageSdk(string project)
    {
        string text = File.ReadAllText(project);
        return text.Contains("Sdk=\"Sage.Sdk", StringComparison.Ordinal) || text.Contains("Sage.Sdk\\Sdk\\Sdk.props", StringComparison.Ordinal)
            || text.Contains("Sage.Sdk/Sdk/Sdk.props", StringComparison.Ordinal);
    }

    // What to start, or an error. `findHost` finds the host of a configuration when --host was not given.
    public ProcessPlan? Plan(Func<string, string?> findHost, out string? error)
    {
        error = null;
        if (!File.Exists(Path.Combine(Game, "game.json")))
        {
            error = $"{Game} has no game.json: name a game folder";
            return null;
        }

        if (Host == null && SdkProject(Game) is { } project)
        {
            var args = new List<string> { "run", "--project", project, "-c", Configuration };
            if (HostArguments.Count > 0)
            {
                args.Add("--");
                args.AddRange(HostArguments);
            }
            return new ProcessPlan(ProcessPlan.Dotnet, args);
        }

        string? host = Host ?? findHost(Configuration);
        if (host == null || !File.Exists(Path.Combine(host, "Sage.Host.dll")))
        {
            error = host == null
                ? $"no {Configuration} host found (beside this program, or src/Sage.Host/bin/{Configuration}/net8.0 in a repository above here): " +
                  $"build it with dotnet build src/Sage.Host -c {Configuration}, or pass --host <dir>"
                : $"{Path.GetFullPath(host)} has no Sage.Host.dll";
            return null;
        }
        var hostArgs = new List<string> { Path.Combine(Path.GetFullPath(host), "Sage.Host.dll"), "-game", Game };
        hostArgs.AddRange(HostArguments);
        return new ProcessPlan(ProcessPlan.Dotnet, hostArgs, Path.GetFullPath(host));
    }
}

// Adds one path to game.json's "modules": { "add": [...] } (sage new game-client --game), keeping the rest of the
// file as it was written: its comments, its order and its layout. The file is read with the comments skipped to
// find where the values are, and the new text is spliced in: after the last entry of an "add" there is, as the
// first key of a "modules" with no "add", or as a last key "modules" when there is none.
internal static class GameJsonModules
{
    private static readonly JsonReaderOptions Reading = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // The new text, or the text unchanged when the path is there already. Throws JsonException when it is not JSON.
    public static string Add(string text, string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, Reading);
        string entry = JsonSerializer.Serialize(path);
        string? property = null;
        long rootStart = -1, rootLastValueEnd = -1, modulesStart = -1, addStart = -1, addLastValueEnd = -1;
        bool inModules = false, inAdd = false, modulesEmpty = true;

        while (reader.Read())
        {
            int depth = reader.CurrentDepth;
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject when depth == 0:
                    rootStart = reader.BytesConsumed;
                    break;
                case JsonTokenType.PropertyName:
                    property = reader.GetString();
                    if (inModules && depth == 2) modulesEmpty = false;
                    break;
                case JsonTokenType.StartObject when depth == 1 && property == "modules":
                    inModules = true;
                    modulesStart = reader.BytesConsumed;
                    break;
                case JsonTokenType.StartArray when depth == 2 && inModules && property == "add":
                    inAdd = true;
                    addStart = reader.BytesConsumed;
                    break;
                case JsonTokenType.EndArray when depth == 2 && inAdd:
                    inAdd = false;
                    break;
                case JsonTokenType.String when depth == 3 && inAdd:
                    if (reader.GetString() == path) return text;
                    addLastValueEnd = reader.BytesConsumed;
                    break;
                case JsonTokenType.EndObject when depth == 1 && inModules:
                    inModules = false;
                    break;
            }
            // The end of each of the root's values, for adding a key after the last.
            if (depth == 1 && reader.TokenType is not JsonTokenType.PropertyName and not JsonTokenType.StartObject and not JsonTokenType.StartArray)
                rootLastValueEnd = reader.BytesConsumed;
        }
        if (rootStart < 0) throw new JsonException("game.json is not an object");

        if (addStart >= 0)
            return addLastValueEnd >= 0 ? Splice(bytes, addLastValueEnd, ", " + entry) : Splice(bytes, addStart, entry);
        if (modulesStart >= 0)
            return Splice(bytes, modulesStart, $" \"add\": [{entry}]" + (modulesEmpty ? " " : ","));
        string modules = $"\"modules\": {{ \"disable\": [], \"add\": [{entry}] }}";
        return rootLastValueEnd >= 0 ? Splice(bytes, rootLastValueEnd, ",\n  " + modules) : Splice(bytes, rootStart, "\n  " + modules + "\n");
    }

    private static string Splice(byte[] bytes, long at, string insert) =>
        Encoding.UTF8.GetString(bytes, 0, (int)at) + insert + Encoding.UTF8.GetString(bytes, (int)at, bytes.Length - (int)at);
}
