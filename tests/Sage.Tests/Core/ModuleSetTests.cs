#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// The set of gameplay modules the engine ships (docs/design/01 §3.1, 16 "As built (F7)", TODO R15).
//
// **Why this file exists.** There were two lists of gameplay modules: `GameplayModules.AddGameplay`,
// which every test uses, and a hand-written sequence in `Sage.Host/Program.cs`, which is what actually
// ships. They agreed until 2026-09-25, when `LightsModule` was added to the first and not the second:
// every test registered the `light` prefab part, the game did not, and point lights did nothing in the
// only build anybody plays. The game said so — one `ERROR Records  sandbox:light: no prefab part
// 'light'` on every boot — and nobody was reading the log.
//
// The tests could not catch it, because the tests were the ones holding the good list. That is the
// shape of the bug worth generalising: **a check that runs against a different configuration from the
// one that ships is not a check of the thing that ships.** There is now one list, and these tests are
// about that list staying whole.
public class ModuleSetTests
{
    public ModuleSetTests() { _ = TestEnv.UserRoot; }

    // Every `IModule` the engine assembly defines is either in the list or deliberately outside it.
    //
    // The exclusions are named rather than inferred: a module the host installs on its own terms
    // (physics, streaming, maps) or one that belongs to the client. Anything else that appears and is
    // not added is the `LightsModule` mistake happening again, and this is what says so.
    [Fact]
    public void EveryGameplayModuleTheEngineDefinesIsInTheOneList()
    {
        Type[] installedElsewhere =
        {
            typeof(PhysicsModule),      // the host adds it before gameplay: characters sweep the space
            typeof(StreamingModule),    // terrain rings and origin rebasing (R6, F14)
            typeof(MapModule),          // brush levels (F16)
        };

        var defined = typeof(GameplayModules).Assembly.GetTypes()
            .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .Where(t => t.IsPublic)
            .Where(t => !installedElsewhere.Contains(t))
            .ToList();

        var listed = GameplayModules.All().Select(m => m.GetType()).ToHashSet();

        var missing = defined.Where(t => !listed.Contains(t)).Select(t => t.Name).OrderBy(n => n).ToList();

        Assert.True(missing.Count == 0,
            $"these modules exist and nothing installs them: {string.Join(", ", missing)}. "
          + "Add them to GameplayModules.All(), or name them in this test as installed elsewhere.");
    }

    // The list is what both callers walk, so it must not hand the same module twice: `Add` would
    // install two of everything, and a module's `OnWorldCreated` is not written to run twice.
    [Fact]
    public void TheListHasNoDuplicates()
    {
        var types = GameplayModules.All().Select(m => m.GetType()).ToList();

        Assert.Equal(types.Count, types.Distinct().Count());
    }

    // Fresh instances every call: the host and a test in the same process must not share module
    // objects, and a module holds state (`AttributesModule.Registries`) that belongs to one engine.
    [Fact]
    public void EachCallHandsBackNewModulesRatherThanSharedOnes()
    {
        var first = GameplayModules.All();
        var second = GameplayModules.All();

        for (int i = 0; i < first.Length; i++)
            Assert.NotSame(first[i], second[i]);
    }

    // The regression itself, stated as what a game author would notice: a prefab asking for a `light`
    // gets one. `AddGameplay` is the path the host now takes too, so this is a test of what ships.
    [Fact]
    public void TheLightPartIsRegisteredByTheModulesTheGameActuallyInstalls()
    {
        var cvars = new CVarRegistry();
        using var engine = new Engine(cvars, CoreCVars.Register(cvars));
        engine.Modules.Add(new PhysicsModule());       // as the host does: characters sweep the space
        engine.Modules.AddGameplay();
        engine.Modules.InitAll();

        Assert.Contains("light", engine.Prefabs.Names);
    }
}
