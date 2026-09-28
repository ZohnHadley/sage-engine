using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sage.Generators;

// Prefab parts and systems, declared (docs/REDESIGN.md §3.3–3.4, issue #17).
//
//   [PrefabPart("light", Plugin = "sage.gameplay.lights")] public sealed class LightPart : IPrefabPart { … }
//   [System("sage.ai.think", Phase.Commands, After = new[] { "sage.character.player_control" })]
//   public sealed class AIThinkSystem : ISystem { … }
//
// Parts are registered like records (RegistrationGenerator): per owning plugin, just before that
// plugin's Init, through the same IGeneratedRegistrations the module manager already runs — this
// generator writes a second one, so the two stay independent files. Owner: `Plugin = "id"`, else the
// assembly's only [Plugin], else SAGE0010.
//
// Systems are not constructed here: a system takes cvars, the renderer and records through its
// constructor, which a module has and generated code does not. What is generated is the list of the
// assembly's declared system types (IGeneratedSystems), so the engine knows every id a loaded assembly
// declares, and the checks a compiler can make: a duplicate id, and an ordering constraint between two
// of this assembly's systems that crosses phases.
[Generator(LanguageNames.CSharp)]
public sealed class PartGenerator : IIncrementalGenerator
{
    private const string Ns = "sage_engine";
    private const string PartAttribute = Ns + ".PrefabPartAttribute";
    private const string SystemAttribute = Ns + ".SystemAttribute";
    private const string PluginAttribute = Ns + ".PluginAttribute";

    private static readonly DiagnosticDescriptor NoOwner = new(
        "SAGE0010", "Say which plugin registers this prefab part",
        "{0} is declared with [PrefabPart] but names no plugin, and this assembly has {1}; add Plugin = \"<plugin id>\"",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Unusable = new(
        "SAGE0011", "A declared part or system must be usable as one",
        "{0} is declared with [{1}] but {2}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateId = new(
        "SAGE0012", "One part or system id, one declaration",
        "[{0}(\"{1}\")] is declared by both {2} and {3}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor CrossPhase = new(
        "SAGE0013", "A system orders itself only within its phase",
        "{0} ({1}) runs {2} '{3}', which runs in {4}; before/after orders systems within one phase, and the phase order already decides this",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var parts = context.SyntaxProvider.ForAttributeWithMetadataName(PartAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => Part.From(ctx));
        var systems = context.SyntaxProvider.ForAttributeWithMetadataName(SystemAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => SystemDecl.From(ctx));
        var plugins = context.SyntaxProvider.ForAttributeWithMetadataName(PluginAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => ctx.Attributes[0].ConstructorArguments.Length > 0
                ? ctx.Attributes[0].ConstructorArguments[0].Value as string ?? ""
                : "");

        var all = parts.Collect()
            .Combine(systems.Collect())
            .Combine(plugins.Collect())
            .Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Assembly"));

        context.RegisterSourceOutput(all, static (spc, input) =>
        {
            var (((partDecls, systemDecls), pluginIds), assemblyName) = input;
            Emit(spc, partDecls, systemDecls, pluginIds, assemblyName);
        });
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<Part> parts, ImmutableArray<SystemDecl> systems,
                             ImmutableArray<string> pluginIds, string assemblyName)
    {
        if (parts.IsEmpty && systems.IsEmpty) return;

        var ids = pluginIds.Where(p => p.Length > 0).Distinct().OrderBy(p => p, System.StringComparer.Ordinal).ToList();
        var byOwner = new SortedDictionary<string, List<Part>>(System.StringComparer.Ordinal);
        var seenParts = new Dictionary<string, Part>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.OrderBy(p => p.TypeName, System.StringComparer.Ordinal))
        {
            if (p.Problem != null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Unusable, p.Location, p.TypeName, "PrefabPart", p.Problem));
                continue;
            }
            if (seenParts.TryGetValue(p.Id, out var first))
            {
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateId, p.Location, "PrefabPart", p.Id, first.TypeName, p.TypeName));
                continue;
            }
            seenParts[p.Id] = p;

            string? owner = p.Plugin ?? (ids.Count == 1 ? ids[0] : null);
            if (owner == null)
            {
                string has = ids.Count == 0 ? "no [Plugin]" : $"{ids.Count} plugins ({string.Join(", ", ids)})";
                spc.ReportDiagnostic(Diagnostic.Create(NoOwner, p.Location, p.TypeName, has));
                continue;
            }
            if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = new List<Part>();
            list.Add(p);
        }

        var declared = new Dictionary<string, SystemDecl>(System.StringComparer.Ordinal);
        foreach (var s in systems.OrderBy(s => s.TypeName, System.StringComparer.Ordinal))
        {
            if (s.Problem != null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Unusable, s.Location, s.TypeName, "System", s.Problem));
                continue;
            }
            if (declared.TryGetValue(s.Id, out var first))
            {
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateId, s.Location, "System", s.Id, first.TypeName, s.TypeName));
                continue;
            }
            declared[s.Id] = s;
        }

        // Constraints between two systems this assembly declares can be checked here; one naming a
        // system in another assembly is checked when the system is added (SystemScheduler).
        foreach (var s in declared.Values)
            foreach (var (relation, names) in new[] { ("before", s.Before), ("after", s.After) })
                foreach (string name in Split(names))
                {
                    string id = name.StartsWith("?") ? name.Substring(1) : name;
                    if (declared.TryGetValue(id, out var other) && other.Phase != s.Phase)
                        spc.ReportDiagnostic(Diagnostic.Create(CrossPhase, s.Location, s.TypeName, s.PhaseName, relation, id, other.PhaseName));
                }

        string className = "SagePartsAndSystems_" + Identifier(assemblyName);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Sage.Generators (issue #17): the prefab parts and systems this assembly declares.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"[assembly: global::{Ns}.GeneratedRegistrationsAttribute(typeof(global::Sage.Generated.{className}))]");
        sb.AppendLine();
        sb.AppendLine("namespace Sage.Generated");
        sb.AppendLine("{");
        sb.AppendLine($"    internal sealed class {className} : global::{Ns}.IGeneratedRegistrations, global::{Ns}.IGeneratedSystems");
        sb.AppendLine("    {");
        sb.Append("        public global::System.Collections.Generic.IReadOnlyList<string> Owners { get; } = new string[] { ");
        sb.Append(string.Join(", ", byOwner.Keys.Select(Literal)));
        sb.AppendLine(" };");
        sb.AppendLine();
        sb.AppendLine($"        public void Register(string plugin, global::{Ns}.RegistrationBuilder builder)");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (plugin)");
        sb.AppendLine("            {");
        foreach (var entry in byOwner)
        {
            sb.AppendLine($"                case {Literal(entry.Key)}:");
            foreach (var p in entry.Value)
                sb.AppendLine($"                    builder.PrefabPart<{p.FullyQualifiedName}>();");
            sb.AppendLine("                    break;");
        }
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public global::System.Collections.Generic.IReadOnlyList<global::System.Type> Systems { get; } = new global::System.Type[]");
        sb.AppendLine("        {");
        foreach (var s in declared.Values.OrderBy(s => s.Id, System.StringComparer.Ordinal))
            sb.AppendLine($"            typeof({s.FullyQualifiedName}),");
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("SagePartsAndSystems.g.cs", sb.ToString());
    }

    private static IEnumerable<string> Split(string joined) =>
        joined.Length == 0 ? Enumerable.Empty<string>() : joined.Split('\n');

    private static string Identifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    // Why a type can't be the thing its attribute says, or null.
    private static string? ProblemWith(INamedTypeSymbol type, string mustImplement, bool needsConstructor)
    {
        if (type.IsAbstract) return "it is abstract";
        if (type.IsGenericType) return "it is generic";
        if (!type.AllInterfaces.Any(i => i.ToDisplayString() == mustImplement)) return $"it does not implement {mustImplement.Substring(Ns.Length + 1)}";
        if (needsConstructor && !type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
            return "it has no public parameterless constructor (a part is read from JSON into a new one)";
        for (var t = type; t != null; t = t.ContainingType)
            if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                return "it is private or nested in a private type";
        return null;
    }

    private static string? Named(AttributeData attr, string name)
    {
        foreach (var named in attr.NamedArguments)
            if (named.Key == name) return named.Value.Value as string;
        return null;
    }

    private static string NamedList(AttributeData attr, string name)
    {
        foreach (var named in attr.NamedArguments)
            if (named.Key == name && named.Value.Kind == TypedConstantKind.Array)
                return string.Join("\n", named.Value.Values.Select(v => v.Value as string ?? ""));
        return "";
    }

    // One [PrefabPart], equatable so unchanged declarations don't re-run the output step.
    private sealed record Part(string Id, string? Plugin, string TypeName, string FullyQualifiedName, string? Problem, LocationInfo? Where)
    {
        public Location Location => Where?.ToLocation() ?? Location.None;

        public static Part From(GeneratorAttributeSyntaxContext ctx)
        {
            var type = (INamedTypeSymbol)ctx.TargetSymbol;
            var attr = ctx.Attributes[0];
            string id = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
            string? problem = id.Trim().Length == 0 ? "its id is empty" : ProblemWith(type, Ns + ".IPrefabPart", needsConstructor: true);
            return new Part(id, Named(attr, "Plugin"), type.Name,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), problem,
                LocationInfo.From(ctx.TargetNode.GetLocation()));
        }
    }

    // One [System]: its id, phase and constraints (joined by '\n' to stay equatable).
    private sealed record SystemDecl(string Id, int Phase, string PhaseName, string Before, string After,
                                     string TypeName, string FullyQualifiedName, string? Problem, LocationInfo? Where)
    {
        public Location Location => Where?.ToLocation() ?? Location.None;

        public static SystemDecl From(GeneratorAttributeSyntaxContext ctx)
        {
            var type = (INamedTypeSymbol)ctx.TargetSymbol;
            var attr = ctx.Attributes[0];
            var args = attr.ConstructorArguments;
            string id = args.Length > 0 ? args[0].Value as string ?? "" : "";
            int phase = args.Length > 1 && args[1].Value is int p ? p : -1;
            string phaseName = args.Length > 1 && args[1].Type is INamedTypeSymbol phaseType
                ? phaseType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && f.ConstantValue is int v && v == phase)?.Name ?? phase.ToString()
                : phase.ToString();
            // No constructor requirement: modules build systems, with whatever they need.
            string? problem = id.Trim().Length == 0 ? "its id is empty" : ProblemWith(type, Ns + ".ISystem", needsConstructor: false);
            return new SystemDecl(id, phase, phaseName, NamedList(attr, "Before"), NamedList(attr, "After"), type.Name,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), problem,
                LocationInfo.From(ctx.TargetNode.GetLocation()));
        }
    }

    // A Location is not equatable across runs; its parts are.
    private sealed record LocationInfo(string Path, Microsoft.CodeAnalysis.Text.TextSpan Span,
                                       Microsoft.CodeAnalysis.Text.LinePositionSpan Lines)
    {
        public Location ToLocation() => Location.Create(Path, Span, Lines);

        public static LocationInfo? From(Location location) =>
            location.SourceTree is null ? null
                : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }
}
