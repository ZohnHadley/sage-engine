#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    // engine's components live in two of them, and each has to be found. Sage.Physics3D declares none:
    // its data (colliders, bodies, the character) is the simulation's, so a 2D backend shares it (#30).
    [Fact]
    public void TheEcsSchemaHasTheComponentsOfEveryBaseAssembly()
    {
        using var app = HeadlessApp.Bare().Build();
        var components = EcsSchema.ComponentTypes();

        Assert.Contains(typeof(PawnIntent), components);            // Sage.Simulation
        Assert.Contains(typeof(CharacterController), components);   // Sage.Simulation, since #30
        Assert.Contains(typeof(Sage.Gameplay.Attributes), components);   // Sage.Gameplay
        foreach (var assembly in new[] { EngineAssemblies.Simulation, EngineAssemblies.Gameplay })
            Assert.Contains(components, t => t.Assembly == assembly);
        Assert.DoesNotContain(components, t => t.Assembly == EngineAssemblies.Physics3D);
    }

    // Gameplay reaches physics only through IPhysicsWorld (issue #30), so a 2D backend can stand in for
    // Bepu. Read from the compiled assembly's metadata, which records every type the code uses: no Bepu
    // assembly at all, and from Sage.Physics3D only the two modules the composition lists name
    // (BasePlugins, GameplayModules.All). Anything else from the backend is a regression.
    [Fact]
    public void GameplayReachesPhysicsOnlyThroughTheFacade()
    {
        var gameplay = EngineAssemblies.Gameplay;
        var references = gameplay.GetReferencedAssemblies().Select(r => r.Name!).ToList();
        Assert.DoesNotContain("BepuPhysics", references);
        Assert.DoesNotContain("BepuUtilities", references);

        using var stream = System.IO.File.OpenRead(gameplay.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            if (type.ResolutionScope.Kind != System.Reflection.Metadata.HandleKind.AssemblyReference) continue;
            var assembly = metadata.GetAssemblyReference((System.Reflection.Metadata.AssemblyReferenceHandle)type.ResolutionScope);
            string name = metadata.GetString(assembly.Name);
            Assert.False(name.StartsWith("Bepu", StringComparison.Ordinal), $"Sage.Gameplay uses {metadata.GetString(type.Name)} from {name}");
            if (name == "Sage.Physics3D") used.Add($"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}");
        }
        Assert.Equal(new[] { "Sage.Physics3D.CharacterModule", "Sage.Physics3D.PhysicsModule" }, used);
    }

    // The generators find the engine by name (SageTypes). A name that no longer resolves makes a
    // generator or an analyzer quietly do nothing, so each one is looked up here.
    [Fact]
    public void EveryTypeTheGeneratorsNameExists()
    {
        // The base, and Friflo (the storage Sage's IComponent extends; SAGE0050 names its namespace).
        var where = EngineAssemblies.Base.Append(typeof(IComponent).GetInterfaces().Single().Assembly).ToList();
        var names = typeof(SageTypes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (f.Name, Value: (string)f.GetRawConstantValue()!))
            .ToList();
        Assert.True(names.Count > 40, $"only {names.Count} names");

        foreach (var (field, value) in names)
        {
            if (value.Contains('<')) continue;   // RecordRef<T> as Roslyn displays it; RecordRefMetadata resolves it
            if (field is nameof(SageTypes.Core) or nameof(SageTypes.Simulation) or nameof(SageTypes.FrifloNamespace))
            {
                Assert.Contains(where.SelectMany(a => a.GetTypes()), t => t.Namespace == value || t.Namespace?.StartsWith(value + ".") == true);
                continue;
            }
            Assert.True(where.Any(a => a.GetType(value) != null), $"SageTypes.{field} = \"{value}\" names no type in the base assemblies");
        }
    }
}
