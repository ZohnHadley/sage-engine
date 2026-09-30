#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Sage.Core;

// A module's identity as a plugin (REDESIGN §3.3, issue #12): a stable string id and a version, so a
// game, another plugin or later a mod can name it without compiling against the assembly it lives in.
//
//   [Plugin("sage.gameplay.items", "0.1.0")]
//   [RequiresPlugin("sage.gameplay.combat", ">=0.1")]
//   public sealed class ItemsModule : IModule { … }
//
// A module without [Plugin] still loads: its id is its class name and its version 0.0.0, which is what
// every module was before this existed.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public PluginAttribute(string id, string version)
    {
        Id = id;
        Version = version;
    }

    public string Id { get; }
    public string Version { get; }
}

// A dependency by id with a version range (VersionRange), for a plugin that does not reference the
// other's assembly. `IModule.Dependencies` (by C# type) still works for modules that do.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RequiresPluginAttribute : Attribute
{
    public RequiresPluginAttribute(string id, string range = "*")
    {
        Id = id;
        Range = range;
    }

    public string Id { get; }
    public string Range { get; }
}

// Content a plugin ships inside its assembly (issue #98): the embedded resources whose logical names
// start with `content/`, mounted as one read-only mount (AssemblyContentMount) with this record
// namespace — after engine content and before the game's mounts, so a game patches a kit's records and
// strings as it patches the engine's. Mounted only while the plugin is loaded. The project embeds them:
//
//   <EmbeddedResource Include="content/**/*" LogicalName="content/%(RecursiveDir)%(Filename)%(Extension)" />
//
//   [Plugin("sage.kits.rpg", "0.1.0"), PluginContent("rpg")]
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PluginContentAttribute : Attribute
{
    public PluginContentAttribute(string recordNamespace) { RecordNamespace = recordNamespace; }

    // What a bare record id in the plugin's content means: `inventory` in it is `rpg:inventory`.
    public string RecordNamespace { get; }
}

// major.minor.patch. A missing minor or patch is 0 ("1.2" is 1.2.0). Pre-release and build suffixes
// are accepted and ignored for ordering, which is all the loader needs today.
public readonly record struct SemVersion(int Major, int Minor, int Patch) : IComparable<SemVersion>
{
    public static readonly SemVersion Zero = new(0, 0, 0);

    public static SemVersion Parse(string text, string where)
    {
        string core = text.Trim();
        int suffix = core.IndexOfAny(new[] { '-', '+' });
        if (suffix >= 0) core = core[..suffix];
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 3 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new FormatException($"{where}: '{text}' is not a version (expected major.minor.patch, e.g. 0.1.0).");
        int At(int i) => i < parts.Length ? int.Parse(parts[i], CultureInfo.InvariantCulture) : 0;
        return new SemVersion(At(0), At(1), At(2));
    }

    public int CompareTo(SemVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

// A set of versions, written the way npm and NuGet users expect:
//   *            anything
//   1.2.3        exactly that (also =1.2.3)
//   >=0.1 <0.2   every comparator must hold (space- or comma-separated): > >= < <= =
//   ^0.1.0       compatible: same major, or for 0.x the same minor
//   ~1.2         same major and minor
public sealed class VersionRange
{
    private readonly List<(string Op, SemVersion Version)> _terms;
    private readonly string _text;

    private VersionRange(string text, List<(string, SemVersion)> terms)
    {
        _text = text;
        _terms = terms;
    }

    public static readonly VersionRange Any = new("*", new());

    public static VersionRange Parse(string text, string where)
    {
        string trimmed = text.Trim();
        if (trimmed is "" or "*") return Any;
        var terms = new List<(string, SemVersion)>();
        foreach (string raw in trimmed.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string op = raw.StartsWith(">=") || raw.StartsWith("<=") ? raw[..2]
                      : raw[0] is '>' or '<' or '=' or '^' or '~' ? raw[..1]
                      : "=";
            string rest = op == "=" && raw[0] != '=' ? raw : raw[op.Length..];
            var version = SemVersion.Parse(rest, where);
            switch (op)
            {
                case "^":
                    terms.Add((">=", version));
                    terms.Add(("<", version.Major > 0 ? new SemVersion(version.Major + 1, 0, 0)
                                                     : new SemVersion(0, version.Minor + 1, 0)));
                    break;
                case "~":
                    terms.Add((">=", version));
                    terms.Add(("<", new SemVersion(version.Major, version.Minor + 1, 0)));
                    break;
                default:
                    terms.Add((op, version));
                    break;
            }
        }
        return new VersionRange(trimmed, terms);
    }

    public bool Contains(SemVersion v) => _terms.All(t => t.Op switch
    {
        ">" => v > t.Version,
        ">=" => v >= t.Version,
        "<" => v < t.Version,
        "<=" => v <= t.Version,
        _ => v == t.Version,
    });

    public override string ToString() => _text;
}

// Who registered what (issue #12). Every registry records each registration against the plugin whose
// Init or Start was running — the host, before any plugin runs — so the `plugins` command can say what a
// plugin contributes, and so a second registration of a name can say whose it replaced. It is the data a
// mod conflict report is made of (REDESIGN §4.4).
public sealed class RegistrationLedger
{
    private readonly Dictionary<(string Kind, string Name), string> _owners = new();

    // The plugin whose code is running now; set by ModuleManager around each module's lifecycle calls.
    public string Owner { get; set; } = "host";

    public void Record(string kind, string name) => _owners[(kind, name.ToLowerInvariant())] = Owner;

    public string? OwnerOf(string kind, string name) =>
        _owners.TryGetValue((kind, name.ToLowerInvariant()), out var owner) ? owner : null;

    public IEnumerable<(string Kind, string Name)> By(string owner) =>
        _owners.Where(kv => kv.Value == owner).Select(kv => kv.Key).OrderBy(k => k.Kind).ThenBy(k => k.Name);
}
