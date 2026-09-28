using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sage.Generators;

// Writes each assembly's registration code from its declarations (docs/REDESIGN.md §3.4, issue #16).
//
//   [Record("item", Plugin = "sage.gameplay.items")] public sealed class ItemRecord { … }
//   [SavedResource("journal", Plugin = "sage.gameplay.factions")] public sealed class Journal { … }
//
// becomes, once per assembly, an IGeneratedRegistrations whose Register(plugin, builder) registers
// exactly the declarations that plugin owns. The module manager calls it for each plugin just before
// that plugin's Init, so a declared type is registered by the plugin it belongs to — and a type whose
// plugin is not loaded is not registered at all, which is what choosing plugins means.
//
// Which plugin owns a declaration: `Plugin = "id"` when given; otherwise the assembly's only
// [Plugin] (a game's assembly usually has one); otherwise it is an error to leave it out (SAGE0001).
[Generator(LanguageNames.CSharp)]
public sealed class RegistrationGenerator : IIncrementalGenerator
{
    private const string Ns = "sage_engine";
    private const string RecordAttribute = Ns + ".RecordAttribute";
    private const string SavedResourceAttribute = Ns + ".SavedResourceAttribute";
    private const string PluginAttribute = Ns + ".PluginAttribute";

    private static readonly DiagnosticDescriptor NoOwner = new(
        "SAGE0001", "Say which plugin registers this declaration",
        "{0} is declared with [{1}] but names no plugin, and this assembly has {2}; add Plugin = \"<plugin id>\"",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotConstructible = new(
        "SAGE0002", "A declared type must be constructible",
        "{0} is declared with [{1}] but {2}; the registration creates it with new()",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateId = new(
        "SAGE0003", "One id, one declaration",
        "[{0}(\"{1}\")] is declared by both {2} and {3}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var records = context.SyntaxProvider.ForAttributeWithMetadataName(RecordAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => Declaration.From(ctx, "Record", "Record"));
        var resources = context.SyntaxProvider.ForAttributeWithMetadataName(SavedResourceAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => Declaration.From(ctx, "SavedResource", "SavedResource"));
        var plugins = context.SyntaxProvider.ForAttributeWithMetadataName(PluginAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => ctx.Attributes[0].ConstructorArguments.Length > 0
                ? ctx.Attributes[0].ConstructorArguments[0].Value as string ?? ""
                : "");

        var all = records.Collect()
            .Combine(resources.Collect())
            .Combine(plugins.Collect())
            .Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Assembly"));

        context.RegisterSourceOutput(all, static (spc, input) =>
        {
            var (((recordDecls, resourceDecls), pluginIds), assemblyName) = input;
            Emit(spc, recordDecls.AddRange(resourceDecls), pluginIds, assemblyName);
        });
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<Declaration> declarations,
                             ImmutableArray<string> pluginIds, string assemblyName)
    {
        if (declarations.IsEmpty) return;

        var ids = pluginIds.Where(p => p.Length > 0).Distinct().OrderBy(p => p, System.StringComparer.Ordinal).ToList();
        var byOwner = new SortedDictionary<string, List<Declaration>>(System.StringComparer.Ordinal);
        var seen = new Dictionary<(string Kind, string Id), Declaration>();

        foreach (var d in declarations.OrderBy(d => d.TypeName, System.StringComparer.Ordinal))
        {
            if (d.Problem != null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotConstructible, d.Location, d.TypeName, d.Attribute, d.Problem));
                continue;
            }
            if (seen.TryGetValue((d.Kind, d.Id), out var first))
            {
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateId, d.Location, d.Attribute, d.Id, first.TypeName, d.TypeName));
                continue;
            }
            seen[(d.Kind, d.Id)] = d;

            string? owner = d.Plugin ?? (ids.Count == 1 ? ids[0] : null);
            if (owner == null)
            {
                string has = ids.Count == 0 ? "no [Plugin]" : $"{ids.Count} plugins ({string.Join(", ", ids)})";
                spc.ReportDiagnostic(Diagnostic.Create(NoOwner, d.Location, d.TypeName, d.Attribute, has));
                continue;
            }
            if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = new List<Declaration>();
            list.Add(d);
        }

        string className = "SageRegistrations_" + Identifier(assemblyName);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Sage.Generators (issue #16): the registrations this assembly declares.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"[assembly: global::{Ns}.GeneratedRegistrationsAttribute(typeof(global::Sage.Generated.{className}))]");
        sb.AppendLine();
        sb.AppendLine("namespace Sage.Generated");
        sb.AppendLine("{");
        sb.AppendLine($"    internal sealed class {className} : global::{Ns}.IGeneratedRegistrations");
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
            foreach (var d in entry.Value)
                sb.AppendLine($"                    builder.{d.Method}<{d.FullyQualifiedName}>();");
            sb.AppendLine("                    break;");
        }
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("SageRegistrations.g.cs", sb.ToString());
    }

    private static string Identifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    // One declared type, as the generator needs it: equatable, so unchanged declarations don't
    // re-run the output step (incremental generators cache on value equality).
    private sealed record Declaration(
        string Kind, string Attribute, string Method, string Id, string? Plugin,
        string TypeName, string FullyQualifiedName, string? Problem, LocationInfo? Where)
    {
        public Location Location => Where?.ToLocation() ?? Location.None;

        public static Declaration From(GeneratorAttributeSyntaxContext ctx, string attribute, string method)
        {
            var type = (INamedTypeSymbol)ctx.TargetSymbol;
            var attr = ctx.Attributes[0];
            string id = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
            string? plugin = null;
            foreach (var named in attr.NamedArguments)
                if (named.Key == "Plugin") plugin = named.Value.Value as string;

            string? problem = null;
            if (type.IsAbstract) problem = "it is abstract";
            else if (type.IsGenericType) problem = "it is generic";
            else if (!type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                problem = "it has no public parameterless constructor";
            else if (!IsReachable(type)) problem = "it is private or nested in a private type";

            return new Declaration(attribute, attribute, method, id, plugin, type.Name,
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), problem,
                LocationInfo.From(ctx.TargetNode.GetLocation()));
        }

        // The generated class is internal to the same assembly, so internal is fine; private is not.
        private static bool IsReachable(INamedTypeSymbol type)
        {
            for (var t = type; t != null; t = t.ContainingType)
                if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected
                    or Accessibility.ProtectedAndInternal)
                    return false;
            return true;
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
