#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The ECS vocabulary is Sage's (REDESIGN §3.5, issue #25): no public signature of the base engine names
// a Friflo type, so a game written against Sage keeps compiling when the storage underneath is upgraded
// or swapped. Checked from the built assemblies, by reflection over everything a game can see — public
// and protected members of public types, their base types, interfaces and generic constraints.
//
// The one allowed mention is by design: Sage's IComponent and ITag extend Friflo's (ECS/Api/Entity.cs
// says why), so those two declarations name it, and every component and tag struct lists Friflo's
// interface next to Sage's. Nothing else does.
public class PublicApiTests
{
    private const string Friflo = "Friflo";

    [Fact]
    public void NoPublicTypeInTheBaseEngineExposesFriflo()
    {
        var problems = EngineAssemblies.Base.SelectMany(Exposures).ToList();
        Assert.True(problems.Count == 0, "Friflo in the public API:\n  " + string.Join("\n  ", problems));
    }

    // The client is outside what the tests may reference (it is MonoGame), so it is read from the host's
    // build output (which has it and MonoGame side by side), in a load context of its own.
    [Fact]
    public void NoPublicTypeInTheClientExposesFriflo()
    {
        string config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;   // bin/<config>/net8.0
        string dir = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "src", "Sage.Host", "bin", config, "net8.0");
        string path = Path.Combine(dir, "Sage.Client.dll");
        Assert.True(File.Exists(path), $"{path} is not built: build Sage.sln ({config}) before running the tests");

        var context = new BuildOutputContext(dir);
        try
        {
            var client = context.LoadFromAssemblyPath(path);
            var problems = Exposures(client).ToList();
            Assert.True(problems.Count == 0, "Friflo in the client's public API:\n  " + string.Join("\n  ", problems));
            Assert.True(client.GetExportedTypes().Length > 20, $"the client's types did not load ({client.GetExportedTypes().Length})");
        }
        finally
        {
            context.Unload();
        }
    }

    // And the check has teeth: the implementation does hold Friflo, just not where anyone can see it.
    [Fact]
    public void TheCheckFindsFrifloWhereItIs()
    {
        var entity = typeof(Entity);
        var raw = entity.GetField("Raw", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.StartsWith(Friflo, raw.FieldType.Namespace);
        Assert.NotEmpty(Mentions(raw.FieldType));
        Assert.Contains(Exposures(typeof(Leaky).Assembly), p => p.StartsWith("Sage.Tests.PublicApiTests+Leaky", StringComparison.Ordinal));
    }

    // What the check must catch: a public member typed with a Friflo type.
    public sealed class Leaky
    {
        public Friflo.Engine.ECS.EntityStore? Store;
    }

    // ---- The scan -------------------------------------------------------------------------------------

    private const BindingFlags Visible = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                         BindingFlags.Static | BindingFlags.DeclaredOnly;

    // The two declarations that extend Friflo's markers, and nothing else.
    private static readonly HashSet<string> ByDesign = new(StringComparer.Ordinal)
    {
        "Sage.Simulation.IComponent : Friflo.Engine.ECS.IComponent",
        "Sage.Simulation.ITag : Friflo.Engine.ECS.ITag",
    };

    private static IEnumerable<string> Exposures(Assembly assembly) => AllExposures(assembly).Where(e => !ByDesign.Contains(e));

    private static IEnumerable<string> AllExposures(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            string name = type.FullName ?? type.Name;
            if (type.BaseType is { } baseType)
                foreach (var m in Mentions(baseType)) yield return $"{name} : {m}";
            foreach (var i in type.GetInterfaces().Where(i => !Implied(type, i)))
                foreach (var m in Mentions(i)) yield return $"{name} : {m}";
            if (type.IsGenericTypeDefinition)
                foreach (var m in type.GetGenericArguments().SelectMany(Constraints)) yield return $"{name} where {m}";

            foreach (var member in type.GetMembers(Visible))
            {
                if (!IsVisible(member)) continue;
                foreach (var m in MemberTypes(member).SelectMany(Mentions))
                    yield return $"{name}.{member.Name}: {m}";
            }
        }
    }

    // Friflo's IComponent/ITag, listed because the type implements Sage's, which extends it.
    private static bool Implied(Type type, Type iface) =>
        iface.Namespace?.StartsWith(Friflo, StringComparison.Ordinal) == true &&
        type.GetInterfaces().Any(sage => sage.Namespace == "Sage.Simulation" && sage.GetInterfaces().Contains(iface));

    private static bool IsVisible(MemberInfo member) => member switch
    {
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        PropertyInfo p => p.GetAccessors(nonPublic: true).Any(a => a.IsPublic || a.IsFamily || a.IsFamilyOrAssembly),
        EventInfo e => e.AddMethod is { } add && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly),
        Type nested => nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem,
        _ => false,
    };

    private static IEnumerable<Type> MemberTypes(MemberInfo member)
    {
        switch (member)
        {
            case FieldInfo f:
                yield return f.FieldType;
                break;
            case PropertyInfo p:
                yield return p.PropertyType;
                foreach (var i in p.GetIndexParameters()) yield return i.ParameterType;
                break;
            case EventInfo e when e.EventHandlerType is { } handler:
                yield return handler;
                break;
            case MethodBase m:
                if (m is MethodInfo method) yield return method.ReturnType;
                foreach (var p in m.GetParameters()) yield return p.ParameterType;
                if (m.IsGenericMethodDefinition)
                    foreach (var a in m.GetGenericArguments())
                        foreach (var c in a.GetGenericParameterConstraints()) yield return c;
                break;
        }
    }

    private static IEnumerable<string> Constraints(Type parameter) =>
        parameter.GetGenericParameterConstraints().SelectMany(Mentions).Select(m => $"{parameter.Name} : {m}");

    // Every Friflo type a signature type is built from: itself, its element type, its generic arguments.
    private static IEnumerable<string> Mentions(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var m in Mentions(type.GetElementType()!)) yield return m;
            yield break;
        }
        if (type.IsGenericParameter) yield break;
        if (type.Namespace?.StartsWith(Friflo, StringComparison.Ordinal) == true)
            yield return type.FullName ?? type.Name;
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                foreach (var m in Mentions(argument)) yield return m;
    }

    // Loads the client and MonoGame from the client's build output; the base engine (and Friflo) that the
    // tests already loaded are shared rather than loaded twice.
    private sealed class BuildOutputContext : AssemblyLoadContext
    {
        private readonly string _dir;
        public BuildOutputContext(string dir) : base(isCollectible: true) => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
            string path = Path.Combine(_dir, name.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
