#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Friflo.Engine.ECS;
using Sage.Generators;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The assembly split (REDESIGN §3.1, issue #24), checked from the built assemblies rather than from the
// project files: what each base assembly references, what it says it is, where its declarations are
// registered from, and that every engine type the generators name still exists where they look.
public class AssemblyLayeringTests
{
    // Core ← Simulation ← Physics3D ← Gameplay, and nothing in the base looks up at the client, the
    // editor, a host or a kit. A reference the compiler did not need is not recorded, so this is what
    // the code actually uses.
    [Fact]
    public void NoBaseAssemblyReferencesALayerAboveIt()
    {
        string[] never = { "MonoGame.Framework", "Sage.Client", "Sage.Editor", "Sage.Host", "Sage.Cli", "Sage.Testing" };
        var above = new Dictionary<Assembly, string[]>
        {
            [EngineAssemblies.Core] = new[] { "Sage.Simulation", "Sage.Physics3D", "Sage.Gameplay", "Friflo.Engine.ECS", "BepuPhysics", "BepuUtilities" },
            [EngineAssemblies.Simulation] = new[] { "Sage.Physics3D", "Sage.Gameplay", "BepuPhysics", "BepuUtilities" },
            [EngineAssemblies.Physics3D] = new[] { "Sage.Gameplay" },
            [EngineAssemblies.Gameplay] = Array.Empty<string>(),
        };

        foreach (var (assembly, forbidden) in above)
        {
            var references = assembly.GetReferencedAssemblies().Select(r => r.Name!).ToList();
            foreach (string name in references)
            {
                string who = assembly.GetName().Name!;
                Assert.False(never.Contains(name) || forbidden.Contains(name), $"{who} references {name}, which is above it");
                Assert.False(name.StartsWith("Sage.Kits", StringComparison.Ordinal), $"{who} references the kit {name} (SAGE0025)");
            }
        }
        Assert.Contains("Sage.Core", EngineAssemblies.Simulation.GetReferencedAssemblies().Select(r => r.Name));
    }

    // src/Directory.Build.props makes every engine project simulation-only unless it opts out, and says
    // so in the assembly: a base assembly that lost the setting would lose SAGE0024 without a word.
    [Fact]
    public void EveryBaseAssemblyIsSimulationOnly()
    {
        foreach (var assembly in EngineAssemblies.Base)
            Assert.True(assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                                .Any(m => m.Key == "SageSimulationOnly" && m.Value == "true"),
                        $"{assembly.GetName().Name} is not built with SageSimulationOnly=true");
    }

    // The engine includes its own (Simulation) assembly's generated registrations, and each loaded
    // module's. So a declaration anywhere else only registers when a module of its plugin lives in the
    // same assembly; one owned by a plugin elsewhere would silently never register.
    [Fact]
    public void EveryDeclarationOutsideTheSimulationBelongsToAPluginInItsOwnAssembly()
    {
        foreach (var assembly in EngineAssemblies.Base.Where(a => a != EngineAssemblies.Simulation))
        {
            var plugins = assembly.GetTypes()
                .Where(t => typeof(IModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .Select(t => t.GetCustomAttribute<PluginAttribute>()?.Id ?? t.Name)
                .ToHashSet();
            var generated = new GeneratedRegistrations();
            generated.Include(assembly);
            foreach (string owner in generated.Owners)
                Assert.True(plugins.Contains(owner),
                    $"{assembly.GetName().Name} declares something for '{owner}', and no module of that plugin is in it: it would never register");
        }
        // And the check has something to check.
        var gameplay = new GeneratedRegistrations();
        gameplay.Include(EngineAssemblies.Gameplay);
        Assert.Contains("sage.gameplay.items", gameplay.Owners);
    }

    // Friflo builds one schema for the process from the loaded assemblies that reference it. Split, the
    // engine's components live in three of them, and each has to be found.
    [Fact]
    public void TheEcsSchemaHasTheComponentsOfEveryBaseAssembly()
    {
        using var app = HeadlessApp.Bare().Build();
        var schema = EntityStore.GetEntitySchema();
        var components = schema.ComponentTypeByType.Keys.Concat(schema.TagTypeByType.Keys).ToHashSet();

        Assert.Contains(typeof(PawnIntent), components);            // Sage.Simulation
        Assert.Contains(typeof(CharacterController), components);   // Sage.Physics3D
        Assert.Contains(typeof(Sage.Gameplay.Attributes), components);   // Sage.Gameplay
        foreach (var assembly in new[] { EngineAssemblies.Simulation, EngineAssemblies.Physics3D, EngineAssemblies.Gameplay })
            Assert.Contains(components, t => t.Assembly == assembly);
    }

    // The generators find the engine by name (SageTypes). A name that no longer resolves makes a
    // generator or an analyzer quietly do nothing, so each one is looked up here.
    [Fact]
    public void EveryTypeTheGeneratorsNameExists()
    {
        var where = EngineAssemblies.Base.Append(typeof(IComponent).Assembly).ToList();
        var names = typeof(SageTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToList();
        Assert.True(names.Count > 40, $"only {names.Count} names");

        foreach (var (field, value) in names)
        {
            if (value.Contains('<')) continue;   // RecordRef<T> as Roslyn displays it; RecordRefMetadata resolves it
            if (field is nameof(SageTypes.Core) or nameof(SageTypes.Simulation))
            {
                Assert.Contains(where.SelectMany(a => a.GetTypes()), t => t.Namespace == value);
                continue;
            }
            Assert.True(where.Any(a => a.GetType(value) != null), $"SageTypes.{field} = \"{value}\" names no type in the base assemblies");
        }
    }
}
