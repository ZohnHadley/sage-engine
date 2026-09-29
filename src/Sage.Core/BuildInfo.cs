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
    // Usually the same word as Config, except `Release` (what `dotnet publish` defaults to): that
    // behaves as Shipping but writes to bin/Release, so paths must use this and not Config.
    public static readonly string ConfigurationName =
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SageBuildConfiguration")?.Value ?? Config.ToString();

    public static string EngineVersion =>
        typeof(BuildInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
