#nullable enable
using System.Collections.Generic;
using Sage.Physics3D;   // PhysicsModule: this list is the one place gameplay names the backend (issue #30)

namespace Sage.Gameplay;

// The base engine's plugins, in the order a host installs them (REDESIGN §0.5, §3.1, issue #24):
// physics before anything that sweeps it, streaming and maps, then every gameplay feature
// (GameplayModules.All, R15). **The** list — the host, a headless server, `sage validate` and the
// tests all pass it as SageAppOptions.AvailablePlugins, so none can ship a set the others don't
// (ModuleSetTests; the LightsModule bug of 2026-09-25). A game picks from it in game.json. It lives
// here, the top of the base, because only this assembly can name every one of them.
public static class BasePlugins
{
    public static IModule[] All()
    {
        var modules = new List<IModule>
        {
            new PhysicsModule(),
            new StreamingModule(),   // terrain rings and origin rebasing (R6, F14)
            new MapModule(),         // brush levels imported from TrenchBroom (15 §3, F16)
        };
        modules.AddRange(GameplayModules.All());
        return modules.ToArray();
    }
}
