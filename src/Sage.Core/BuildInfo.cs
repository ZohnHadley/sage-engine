#nullable enable
using System.Linq;
using System.Reflection;

namespace Sage.Core;

public enum BuildConfig { Debug, Development, Shipping }

// Which build configuration this binary was compiled in (build/Sage.Configurations.props,
// docs/design/01-host-and-modules.md §3.2). "Dev builds" = Debug + Development (SAGE_DEV).
public static class BuildInfo
{
#if SAGE_DEBUG
    public static readonly BuildConfig Config = BuildConfig.Debug;
#elif SAGE_DEVELOPMENT
    public static readonly BuildConfig Config = BuildConfig.Development;
#else
    public static readonly BuildConfig Config = BuildConfig.Shipping;
#endif

    public static readonly bool IsDevBuild = Config != BuildConfig.Shipping;

    // The MSBuild configuration this assembly was built in, which names the bin/ folder it went to.
    // The same word as Config: `Release` (what `dotnet publish` defaults to) is Shipping, writes to
    // bin/Shipping and reports "Shipping" here (build/Sage.Configurations.props, issue #294).
    public static readonly string ConfigurationName =
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SageBuildConfiguration")?.Value ?? Config.ToString();

    // The engine's SemVer, from git tags (build/Sage.Version.props, issue #31): 0.1.0 on the commit tagged
    // v0.1.0, 0.1.0-alpha.0.37+1a2b3c4 on the 37th commit before any tag. The assembly's informational
    // version, which MinVer writes; "0.0.0" only for an assembly built without it.
    public static readonly string EngineVersion =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    // The same, as the major.minor.patch that plugin, kit and game ranges are checked against
    // (`[RequiresPlugin("sage", "^0.1")]`, game.json's "sage"). A pre-release counts as the version it
    // leads to: 0.1.0-alpha.0.37 satisfies ">=0.1.0", so a build between tags loads what the tag will.
    public static readonly SemVersion EngineSemVersion = SemVersion.Parse(EngineVersion, "the engine's version");
}
