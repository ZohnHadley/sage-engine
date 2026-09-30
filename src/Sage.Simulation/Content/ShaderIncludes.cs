#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Which effect files a changed shader file affects (issue 4h-3).
//
// An `.fx` includes `.fxh` files with `#include "common.fxh"`, and a header can include another, so
// editing `common.fxh` has to recompile every effect that reaches it — the same rule the build's
// `SageCompileShaders` target applies by recompiling everything. Pure text in, names out, so it is
// tested headless; the client's recompiler reads the files and runs mgfxc.
//
// Names are paths relative to the shaders folder with `/` separators ("lit.fx", "lib/noise.fxh").
// An include is resolved relative to the file that contains it, as the HLSL preprocessor does.
internal static class ShaderIncludes
{
    // The files `source` includes, as written (`#include "x"` and `#include <x>`), in order. Commented
    // out includes (`// #include`, and inside a block comment) are not includes.
    public static IReadOnlyList<string> Includes(string source)
    {
        var found = new List<string>();
        bool inBlock = false;
        foreach (string raw in source.Split('\n'))
        {
            string line = StripComments(raw, ref inBlock).Trim();
            if (!line.StartsWith('#')) continue;
            line = line[1..].TrimStart();
            if (!line.StartsWith("include", StringComparison.Ordinal)) continue;
            line = line[7..].TrimStart();
            if (line.Length < 2) continue;
            char close = line[0] == '"' ? '"' : line[0] == '<' ? '>' : '\0';
            if (close == '\0') continue;
            int end = line.IndexOf(close, 1);
            if (end > 1) found.Add(line[1..end]);
        }
        return found;
    }

    // Every `.fx` in `sources` (name → text) that is `changed`, or reaches it through includes, in name
    // order. A change to a file nothing includes and that is not an effect yields nothing. Cycles end.
    public static IReadOnlyList<string> Dependents(IReadOnlyDictionary<string, string> sources, string changed)
    {
        string target = Normalize(changed);
        var includesOf = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in sources)
        {
            string from = Normalize(name);
            var resolved = new List<string>();
            foreach (string inc in Includes(text)) resolved.Add(Resolve(from, inc));
            includesOf[from] = resolved;
        }

        var result = new List<string>();
        foreach (var (name, _) in sources)
        {
            string from = Normalize(name);
            if (!from.EndsWith(".fx", StringComparison.OrdinalIgnoreCase)) continue;
            if (Reaches(from, target, includesOf, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                result.Add(name);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static bool Reaches(string from, string target, Dictionary<string, List<string>> includesOf, HashSet<string> seen)
    {
        if (string.Equals(from, target, StringComparison.OrdinalIgnoreCase)) return true;
        if (!seen.Add(from) || !includesOf.TryGetValue(from, out var next)) return false;
        foreach (string n in next)
            if (Reaches(n, target, includesOf, seen)) return true;
        return false;
    }

    // `include` as written inside `from`, as a name relative to the shaders folder.
    private static string Resolve(string from, string include)
    {
        int slash = from.LastIndexOf('/');
        string dir = slash < 0 ? "" : from[..(slash + 1)];
        return Normalize(dir + include);
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (string p in path.Replace('\\', '/').Split('/'))
        {
            if (p.Length == 0 || p == ".") continue;
            if (p == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else parts.Add(p);
        }
        return string.Join('/', parts);
    }

    private static string StripComments(string line, ref bool inBlock)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (inBlock)
            {
                if (line[i] == '*' && i + 1 < line.Length && line[i + 1] == '/') { inBlock = false; i++; }
            }
            else if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlock = true; i++; }
            else if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            else sb.Append(line[i]);
        }
        return sb.ToString();
    }
}
