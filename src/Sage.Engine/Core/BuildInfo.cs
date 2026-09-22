#nullable enable
namespace sage_engine;

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

    public static string EngineVersion =>
        typeof(BuildInfo).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
