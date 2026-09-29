#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Sage.Simulation;

// What the engine knows about one module as a plugin.
public sealed record PluginInfo(string Id, SemVersion Version, ModuleKind Kind, IReadOnlyList<(string Id, VersionRange Range)> Requires)
{
    public static PluginInfo Of(IModule module)
    {
        var type = module.GetType();
        var plugin = type.GetCustomAttribute<PluginAttribute>();
        var requires = type.GetCustomAttributes<RequiresPluginAttribute>()
            .Select(r => (r.Id, VersionRange.Parse(r.Range, $"{type.Name}'s [RequiresPlugin(\"{r.Id}\")]")))
            .ToList();
        return new PluginInfo(
            plugin?.Id ?? module.Name,
            plugin != null ? SemVersion.Parse(plugin.Version, $"{type.Name}'s [Plugin]") : SemVersion.Zero,
            module.Kind,
            requires);
    }

    // `sage.gameplay.*` matches `sage.gameplay` and everything under it; anything else must match exactly.
    public bool Matches(string pattern) =>
        pattern.EndsWith(".*", StringComparison.Ordinal)
            ? Id.Equals(pattern[..^2], StringComparison.OrdinalIgnoreCase)
              || Id.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : Id.Equals(pattern, StringComparison.OrdinalIgnoreCase);
}
