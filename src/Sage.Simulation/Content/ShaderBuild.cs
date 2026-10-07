#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Sage.Simulation;

// Compiling a mount's `.fx` outside the build (issue #400): the shaders a mod ships as source. Three users:
//   - a dev build, when it loads, compiles every effect in a folder mount's `shaders/` whose `.mgfxo` is missing
//     or older than the effect or a file it includes, and again when one is edited (ShaderHotCompile);
//   - `sage mods build <mod>` does the same to a mod folder, from the command line;
//   - `sage mods pack <mod>` compiles every effect into the package (Precompile, PackMod), so a packed mod
//     never needs mgfxc where it is played.
// An effect compiles to the `.mgfxo` beside it (`shaders/glow.fx` → `shaders/glow.mgfxo`), the name a material's
// `effect` gives, as the Sage.Sdk build does for a game's own shaders.
//
// The compiler is an `IShaderCompiler`: mgfxc for real (MgfxcCompiler), which runs natively on Windows and needs
// Wine elsewhere, and a fake in the tests, which run where mgfxc cannot. Everything around it — what is out of
// date, where the result goes, which headers an effect can include, what a failure leaves behind — is here.
//
// A mod's effect may include the engine's headers (`#include "common.fxh"`, the shared matrices and lights) and
// the game's: they are copied beside the mod's own files into a staging folder for the compile, the mod's files
// winning, and mgfxc's messages are given back with the staging folder replaced by the mod's.
internal interface IShaderCompiler
{
    // Whether this compiler can run here; `why` says why not.
    bool CanRun(out string why);

    // Compiles `source` (a `.fx`) to `output`. Blocking: mgfxc takes seconds, so callers run it off the frame.
    ShaderCompileResult Compile(string source, string output);
}

internal readonly record struct ShaderCompileResult(bool Ok, string Output);

// `dotnet mgfxc <fx> <out> /Profile:OpenGL`, the build's command (build/Sage.EngineContent.targets), run from the
// folder whose `.config/dotnet-tools.json` pins it: the checkout above this program, else the source's folder.
internal sealed class MgfxcCompiler : IShaderCompiler
{
    public static readonly MgfxcCompiler Instance = new();

    private readonly string? _toolsRoot =
        ShaderBuild.FindUp(Path.Combine(".config", "dotnet-tools.json"), directory: false) is { } manifest
            ? Path.GetDirectoryName(Path.GetDirectoryName(manifest)) : null;

    public bool CanRun(out string why)
    {
        why = "";
        if (OperatingSystem.IsWindows()) return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MGFXC_WINE_PATH"))) return true;
        why = "mgfxc needs Wine on this platform and MGFXC_WINE_PATH is not set";
        return false;
    }

    public ShaderCompileResult Compile(string source, string output)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _toolsRoot ?? Path.GetDirectoryName(source)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("mgfxc");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("/Profile:OpenGL");
        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            string text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            text += error.Result;
            return new ShaderCompileResult(process.ExitCode == 0 && File.Exists(output), text.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new ShaderCompileResult(false, $"dotnet mgfxc could not be started: {ex.Message}");
        }
    }
}

internal static class ShaderBuild
{
    // The folder of a mount (or a mod) whose effects are compiled.
    public const string Folder = "shaders";
    public const string CompiledExtension = ".mgfxo";

    public static bool IsSource(string file) =>
        file.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".fxh", StringComparison.OrdinalIgnoreCase);

    // Every `.fx` and `.fxh` under `dir`, name (relative, `/`) → text. A file caught mid-write is left out:
    // the next change reads it.
    public static Dictionary<string, string> ReadSources(string? dir)
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dir == null || !Directory.Exists(dir)) return texts;
        foreach (string file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            if (!IsSource(file)) continue;
            string name = Path.GetRelativePath(dir, file).Replace('\\', '/');
            if (name.Split('/').Any(part => part.StartsWith('.'))) continue;
            try { texts[name] = File.ReadAllText(file); }
            catch (IOException) { }
        }
        return texts;
    }

    // The effects (`.fx`) among `sources`, in name order.
    public static IReadOnlyList<string> Effects(IReadOnlyDictionary<string, string> sources) =>
        sources.Keys.Where(n => n.EndsWith(".fx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    // `shaders/glow.fx`'s compiled file beside it, `shaders/glow.mgfxo`.
    public static string CompiledPath(string dir, string effect) =>
        Path.Combine(dir, Path.ChangeExtension(effect, CompiledExtension).Replace('/', Path.DirectorySeparatorChar));

    // Whether `effect`'s compiled file beside it is missing, or older than the effect or a file of `dir` it
    // includes. A header from elsewhere (the engine's) is the build's to watch, not this.
    public static bool IsStale(string dir, string effect, IReadOnlyDictionary<string, string> sources)
    {
        string compiled = CompiledPath(dir, effect);
        if (!File.Exists(compiled)) return true;
        var built = File.GetLastWriteTimeUtc(compiled);
        foreach (string name in ShaderIncludes.Reached(sources, effect))
        {
            string file = Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) > built) return true;
        }
        return false;
    }

    // The effects under `dir` that need compiling, in name order.
    public static IReadOnlyList<string> Stale(string dir)
    {
        var sources = ReadSources(dir);
        return Effects(sources).Where(e => IsStale(dir, e, sources)).ToList();
    }

    // The headers (`.fxh`) of the folders in `dirs`, a later folder's winning: what an effect may include from
    // outside its own folder.
    public static Dictionary<string, string> Headers(IEnumerable<string?> dirs)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? dir in dirs)
            foreach (var (name, text) in ReadSources(dir))
                if (name.EndsWith(".fxh", StringComparison.OrdinalIgnoreCase)) headers[name] = text;
        return headers;
    }

    // Compiles `effect` (a name under `dir`) to `output`, with `headers` beside the folder's own sources (which
    // win), staged in a temp folder. The output is replaced only on success: a failed compile leaves the old
    // `.mgfxo`, if there was one, as it was. mgfxc's messages name the files in `dir`, not the staging copies.
    public static ShaderCompileResult Compile(IShaderCompiler compiler, string dir, string effect, string output,
                                              IReadOnlyDictionary<string, string>? headers = null)
    {
        string stage = Path.Combine(Path.GetTempPath(), $"sage-shaders-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stage);
            if (headers != null)
                foreach (var (name, header) in headers) Write(stage, name, header);
            foreach (string file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!IsSource(file)) continue;
                string to = Path.Combine(stage, Path.GetRelativePath(dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
            }
            string temp = Path.Combine(stage, $"out-{Guid.NewGuid():N}{CompiledExtension}");
            var result = compiler.Compile(Path.Combine(stage, effect.Replace('/', Path.DirectorySeparatorChar)), temp);
            string text = Unstage(result.Output, stage, dir);
            if (!result.Ok || !File.Exists(temp)) return new ShaderCompileResult(false, text.Length > 0 ? text : $"{effect}: the compiler wrote nothing");
            string? outDir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            File.Move(temp, output, overwrite: true);
            return new ShaderCompileResult(true, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShaderCompileResult(false, $"{effect}: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Write(string root, string name, string text)
    {
        string to = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.WriteAllText(to, text);
    }

    private static string Unstage(string text, string stage, string dir)
    {
        string real = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        return text.Replace(stage, real, StringComparison.OrdinalIgnoreCase).Trim();
    }

    // What a failed compile says (07 §8): the compiler's messages, and what is drawn meanwhile — the effect
    // already compiled, or, with none, `sage:error` (flat magenta) for every material that uses it.
    public static string FailureMessage(string where, string effect, string output, bool hadCompiled) =>
        $"{where}{effect} did not compile; " +
        (hadCompiled ? "keeping the shader already compiled" : "materials using it draw sage:error (magenta) until it does") +
        (output.Length > 0 ? ":\n" + output : "");

    // ---- mods: `sage mods build` and `sage mods pack` -------------------------------------------------------

    // The engine's headers for a mod's effects: `<engine content>/shaders` (a Player ships its sources there) and
    // the checkout's `engine_content/shaders` above this program.
    public static IReadOnlyDictionary<string, string> EngineHeaders(string? engineContent) =>
        Headers(new[] { engineContent == null ? null : Path.Combine(engineContent, Folder), FindUp(Path.Combine("engine_content", Folder), directory: true) });

    // `sage mods build`: compiles the out-of-date effects of a mod folder beside their sources. Returns each
    // effect tried, and whether it compiled; nothing when it is all up to date. Throws InvalidOperationException
    // when something needs compiling and the compiler cannot run here.
    public static IReadOnlyList<(string Effect, ShaderCompileResult Result)> BuildMod(string modRoot, IShaderCompiler compiler,
                                                                                    IReadOnlyDictionary<string, string>? headers)
    {
        string dir = Path.Combine(modRoot, Folder);
        var results = new List<(string, ShaderCompileResult)>();
        if (!Directory.Exists(dir)) return results;
        var stale = Stale(dir);
        if (stale.Count == 0) return results;
        if (!compiler.CanRun(out string why))
            throw new InvalidOperationException($"{string.Join(", ", stale.Select(e => $"{Folder}/{e}"))} need(s) compiling, but {why}");
        foreach (string effect in stale)
            results.Add(($"{Folder}/{effect}", Compile(compiler, dir, effect, CompiledPath(dir, effect), headers)));
        return results;
    }

    // `sage mods pack`'s compile: every effect of the mod, into `outDir`, by its path in the package
    // (`shaders/glow.mgfxo`) → the file. Where the compiler cannot run, an effect whose `.mgfxo` beside it is up
    // to date is packed as it is (counted in UpToDate, not returned), and one without is an error. Throws
    // InvalidDataException naming every effect that did not compile.
    public static (Dictionary<string, string> Files, int Compiled, int UpToDate, string? Why) Precompile(
        string modRoot, IShaderCompiler compiler, IReadOnlyDictionary<string, string>? headers, string outDir)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.Combine(modRoot, Folder);
        if (!Directory.Exists(dir)) return (files, 0, 0, null);
        var sources = ReadSources(dir);
        var effects = Effects(sources);
        if (effects.Count == 0) return (files, 0, 0, null);

        var errors = new List<string>();
        if (!compiler.CanRun(out string why))
        {
            int fresh = 0;
            foreach (string effect in effects)
            {
                if (IsStale(dir, effect, sources)) errors.Add($"{Folder}/{effect} has no up-to-date {Path.ChangeExtension(effect, CompiledExtension)} and {why}");
                else fresh++;
            }
            if (errors.Count > 0)
                throw new InvalidDataException(string.Join("\n", errors) +
                    "\n(compile it where mgfxc runs — Windows, or with MGFXC_WINE_PATH set — with `sage mods build`, then pack)");
            return (files, 0, fresh, why);
        }

        foreach (string effect in effects)
        {
            string package = $"{Folder}/{Path.ChangeExtension(effect, CompiledExtension)}";
            string output = Path.Combine(outDir, package.Replace('/', Path.DirectorySeparatorChar));
            var result = Compile(compiler, dir, effect, output, headers);
            if (result.Ok) files[package] = output;
            else errors.Add($"{Folder}/{effect} did not compile:\n{result.Output}");
        }
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        return (files, files.Count, 0, null);
    }

    // `sage mods pack <folder>`: the effects compiled (Precompile), then the folder packed with them (ModPackage).
    public static (ModManifest Mod, int Files, string Output, int Compiled, int UpToDate, string? Why) PackMod(
        string folder, string? output, IShaderCompiler compiler, IReadOnlyDictionary<string, string>? headers)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"no mod folder {Path.GetFullPath(folder)}");
        _ = ModManifest.Load(folder);   // a broken mod.json says so before anything compiles
        string stage = Path.Combine(Path.GetTempPath(), $"sage-modpack-{Guid.NewGuid():N}");
        try
        {
            var (files, compiled, upToDate, why) = Precompile(folder, compiler, headers, stage);
            var (mod, count, written) = ModPackage.Pack(folder, output, files);
            return (mod, count, written, compiled, upToDate, why);
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The nearest ancestor of this program that has `relative`, for a build run from a checkout.
    public static string? FindUp(string relative, bool directory)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir != null && i < 10; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (directory ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
