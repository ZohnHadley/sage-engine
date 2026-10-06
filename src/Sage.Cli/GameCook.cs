#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sage.Cli;

// `sage cook <game>` (issue #302, docs/design/05 §7): every `.glb` in the game's mounts gets a `.sgmesh` beside
// it and every `.png`/`.jpg`/`.tga` a `.sgtex` (with its mip chain, issue #317) (CookedAssets says what they hold and when the client reads them).
// `sage package` runs it on the package it writes, so a shipped game loads cooked files; the loose ones stay
// beside them (the simulation reads skeletons and clips from the `.glb`, validation checks the paths, and a
// cooked file that cannot be read falls back to its loose one).
//
// A cooked file whose header names the loose file it would be made from (same length and hash) is up to date
// and left alone, so cooking again is cheap; `Force` cooks everything. `Clean` removes them instead.
//
// Each cook also measures what it bought: the CPU time to load the loose file as the client does (glTF parse,
// image decode and premultiply) against reading the cooked one, both from bytes in memory and each timed on a
// second run (the first paid for JIT), and the texture memory before and after.
internal sealed class CookOptions
{
    // Block-compress textures (BC1 opaque, BC3 with alpha) unless a path matches `Uncompressed`.
    public bool Compress { get; init; } = true;

    // Paths inside a mount ("textures/ui/**", "*.png") kept as exact premultiplied RGBA.
    public IReadOnlyList<string> Uncompressed { get; init; } = Array.Empty<string>();

    public bool Force { get; init; }

    public static CookOptions From(GameManifest.CookSettings settings, bool force = false) =>
        new() { Compress = settings.Compress, Uncompressed = settings.Uncompressed.ToList(), Force = force };
}

internal sealed class CookedFile
{
    public required string Path;            // the loose file, relative to the folder cooked, '/' separators
    public required string Kind;            // "mesh" or "texture"
    public required string Format;          // "mesh", "Rgba", "Bc1", "Bc3"
    public long LooseBytes, CookedBytes;    // on disk
    public long LooseMemory, CookedMemory;  // a texture's GPU bytes; a mesh's vertex and index bytes (equal)
    public double LooseMs, CookedMs;        // CPU time to load it, file bytes already in memory
}

internal sealed class CookResult
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<CookedFile> Cooked { get; } = new();
    public List<string> Written { get; } = new();   // the cooked files written, full paths
    public int UpToDate { get; set; }
    public int Removed { get; set; }

    public bool Ok => Errors.Count == 0;

    public string Summary()
    {
        int meshes = Cooked.Count(c => c.Kind == "mesh"), textures = Cooked.Count - meshes;
        var tex = Cooked.Where(c => c.Kind == "texture").ToList();
        return $"{meshes} mesh(es) and {textures} texture(s) cooked, {UpToDate} up to date; " +
               $"load {Cooked.Sum(c => c.LooseMs):F1} ms loose -> {Cooked.Sum(c => c.CookedMs):F1} ms cooked; " +
               $"texture memory {Kb(tex.Sum(c => c.LooseMemory))} -> {Kb(tex.Sum(c => c.CookedMemory))}";
    }

    private static string Kb(long bytes) => $"{bytes / 1024.0:F0} KB";
}

internal static class GameCook
{
    // Cooks (or cleans) every mount of the game in `gameDirectory`, in place.
    public static CookResult Run(string gameDirectory, bool force = false, bool clean = false)
    {
        var result = new CookResult();
        GameManifest manifest;
        try { manifest = GameManifest.Load(gameDirectory); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException)
        {
            result.Errors.Add(ex.Message);
            return result;
        }
        var options = CookOptions.From(manifest.Cook, force);
        foreach (string mount in manifest.Mounts)
        {
            string folder = Path.GetFullPath(Path.Combine(gameDirectory, mount));
            if (!Directory.Exists(folder))
            {
                result.Errors.Add($"game.json mounts \"{mount}\", and there is no folder {folder}.");
                continue;
            }
            if (clean) Clean(folder, result);
            else CookFolder(folder, options, result);
        }
        return result;
    }

    // Removes every cooked file under `folder`.
    public static void Clean(string folder, CookResult result)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(CookedAssets.MeshExtension, StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(CookedAssets.TextureExtension, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            File.Delete(file);
            result.Removed++;
        }
    }

    // Cooks every model and texture under `folder` (a mount's root: `Uncompressed` patterns are relative to it).
    public static void CookFolder(string folder, CookOptions options, CookResult result)
    {
        var uncompressed = options.Uncompressed.Select(Glob).ToList();
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            bool mesh = CookedAssets.IsCookableMesh(file);
            if (!mesh && !CookedAssets.IsCookableTexture(file)) continue;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException ex)
            {
                result.Errors.Add($"{file}: {ex.Message}");
                continue;
            }
            var stamp = SourceStamp.Of(bytes);
            string target = file + (mesh ? CookedAssets.MeshExtension : CookedAssets.TextureExtension);
            if (!options.Force && UpToDate(target, stamp, mesh))
            {
                result.UpToDate++;
                continue;
            }

            try
            {
                var cooked = mesh ? CookMesh(relative, bytes, stamp, target)
                    : CookTexture(relative, bytes, stamp, target, options.Compress && !uncompressed.Any(g => g.IsMatch(relative)));
                if (cooked == null)
                {
                    result.Warnings.Add($"{file}: not a model this engine reads, so it is not cooked (it loads loose, and fails there too)");
                    continue;
                }
                cooked.LooseBytes = bytes.Length;
                cooked.CookedBytes = new FileInfo(target).Length;
                result.Cooked.Add(cooked);
                result.Written.Add(target);
            }
            catch (InvalidDataException ex)
            {
                result.Warnings.Add($"{file}: {ex.Message}; not cooked (it loads loose)");
            }
            catch (IOException ex)
            {
                result.Errors.Add($"{target}: {ex.Message}");
            }
        }
    }

    private static CookedFile? CookMesh(string relative, byte[] bytes, SourceStamp stamp, string target)
    {
        var geometry = MeshGeometry.ReadGlb(new MemoryStream(bytes, writable: false), relative);
        if (geometry == null) return null;
        double looseMs = Time(() => MeshGeometry.ReadGlb(new MemoryStream(bytes, writable: false), relative));

        var cooked = new MemoryStream();
        CookedMesh.Write(cooked, geometry, stamp);
        File.WriteAllBytes(target, cooked.ToArray());

        var read = CookedMesh.Read(new MemoryStream(cooked.ToArray()), out _);
        double cookedMs = Time(() => CookedMesh.Read(new MemoryStream(cooked.ToArray()), out _));
        long memory = read.Parts.Sum(p => (long)p.VertexCount * (p.Skinned != null ? SkinnedMeshVertex.Size : MeshVertex.Size) + p.Indices.Length * 4L);
        return new CookedFile
        {
            Path = relative, Kind = "mesh", Format = "mesh", LooseMemory = memory, CookedMemory = memory, LooseMs = looseMs, CookedMs = cookedMs,
        };
    }

    private static CookedFile CookTexture(string relative, byte[] bytes, SourceStamp stamp, string target, bool compress)
    {
        var rgba = CookedTexture.DecodeImage(bytes, out int width, out int height);
        CookedTexture.Premultiply(rgba);
        // The client's loose load: decode, premultiply and the mip chain (issue #317), all uncompressed.
        long looseMemory = 0;
        for (int level = 0; level < TextureMips.LevelCount(width, height); level++)
            looseMemory += CookedTexture.LevelBytes(CookedTextureFormat.Rgba, width, height, level);

        var texture = CookedTexture.FromRgba(rgba, width, height, compress);
        double looseMs = Time(() =>
        {
            var decoded = CookedTexture.DecodeImage(bytes, out int w, out int h);
            CookedTexture.Premultiply(decoded);
            TextureMips.Generate(decoded, w, h);
        });
        var cooked = new MemoryStream();
        CookedTexture.Write(cooked, texture, stamp);
        File.WriteAllBytes(target, cooked.ToArray());

        var read = CookedTexture.Read(new MemoryStream(cooked.ToArray()), out _);
        double cookedMs = Time(() => CookedTexture.Read(new MemoryStream(cooked.ToArray()), out _));
        return new CookedFile
        {
            Path = relative, Kind = "texture", Format = read.Format.ToString(),
            LooseMemory = looseMemory, CookedMemory = read.GpuBytes, LooseMs = looseMs, CookedMs = cookedMs,
        };
    }

    // A load's CPU time, measured on its second run: the first, just made above, paid for JIT compilation.
    private static double Time(Action load)
    {
        var clock = Stopwatch.StartNew();
        load();
        return clock.Elapsed.TotalMilliseconds;
    }

    private static bool UpToDate(string target, SourceStamp stamp, bool mesh)
    {
        if (!File.Exists(target)) return false;
        try
        {
            using var stream = File.OpenRead(target);
            SourceStamp cooked;
            if (mesh) CookedMesh.Read(stream, out cooked);
            else CookedTexture.Read(stream, out cooked);
            return cooked == stamp;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return false;   // unreadable or an older format: cooked again
        }
    }

    // "textures/ui/**" → any path under textures/ui; "*" stays inside one folder; "?" is one character.
    internal static Regex Glob(string pattern)
    {
        string regex = Regex.Escape(pattern.Replace('\\', '/').TrimStart('/'))
            .Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
        return new Regex("^" + regex + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
