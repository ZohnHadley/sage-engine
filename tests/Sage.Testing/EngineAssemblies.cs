#nullable enable
using System.Collections.Generic;
using System.Reflection;

namespace Sage.Testing;

// The base engine's assemblies (REDESIGN §3.1, issue #24), for a test that reads every declaration the
// engine makes: one that looked only at typeof(Engine).Assembly would quietly check a quarter of them.
public static class EngineAssemblies
{
    public static Assembly Core => typeof(RecordStore).Assembly;
    public static Assembly Simulation => typeof(Engine).Assembly;
    public static Assembly Physics3D => typeof(PhysicsSpace).Assembly;
    public static Assembly Gameplay => typeof(BasePlugins).Assembly;

    // Kernel first, in reference order.
    public static IReadOnlyList<Assembly> Base { get; } = new[] { Core, Simulation, Physics3D, Gameplay };
}
