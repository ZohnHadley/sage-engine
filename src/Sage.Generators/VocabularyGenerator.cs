using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sage.Generators;

// Entries of the open vocabularies, declared (docs/REDESIGN.md §4.3 stage 1, issue #28).
//
//   public sealed class AIConditionAttribute : VocabularyEntryAttribute<IAICondition> { … }   // the engine's
//
//   [AICondition("is_night")]                                                               // a game's
//   public sealed class IsNight : IAICondition { public bool Sense(in AIPerception p) => … }
//
// Any attribute deriving from Sage.Core.VocabularyEntryAttribute<TEntry> declares an entry of TEntry's
// vocabulary, so a plugin opens a vocabulary of its own by writing one attribute class, and this
// generator needs no list of them. Registered like records and parts (RegistrationGenerator): per
// owning plugin, just before that plugin's Init, through an IGeneratedRegistrations of its own —
// `Plugin = "id"`, else the assembly's only [Plugin], else SAGE0100.
//
// What it rejects:
//   SAGE0100  no owning plugin can be inferred
//   SAGE0101  the class cannot be an entry: abstract, generic, private, no public parameterless
//             constructor, or not a TEntry
//   SAGE0102  one id declared twice in one vocabulary in this assembly
//   SAGE0103  an empty id
//   SAGE0104  TEntry is not marked [Vocabulary("name")], so nothing could read it from content
[Generator(LanguageNames.CSharp)]
public sealed class VocabularyGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor NoOwner = new(
        "SAGE0100", "Say which plugin registers this vocabulary entry",
        "{0} is declared with [{1}] but names no plugin, and this assembly has {2}; add Plugin = \"<plugin id>\"",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Unusable = new(
        "SAGE0101", "A vocabulary entry must be constructible and of its vocabulary's type",
        "{0} is declared with [{1}] but {2}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateId = new(
        "SAGE0102", "One id, one entry per vocabulary",
        "[{0}(\"{1}\")] is declared by both {2} and {3}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor EmptyId = new(
        "SAGE0103", "A vocabulary entry needs an id",
        "{0} is declared with [{1}] and an empty id; content names an entry by it",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotAVocabulary = new(
        "SAGE0104", "An entry's type must be a vocabulary",
        "[{0}] declares entries of {1}, which is not marked [Vocabulary(\"name\")]; content could not name them",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var entries = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                static (ctx, _) => Entry.From(ctx))
            .Where(static e => !e.IsEmpty)
            .SelectMany(static (e, _) => e)
            .Collect();
        var plugins = context.SyntaxProvider.ForAttributeWithMetadataName(SageTypes.PluginAttribute,
            static (node, _) => node is ClassDeclarationSyntax,
            static (ctx, _) => ctx.Attributes[0].ConstructorArguments.Length > 0
                ? ctx.Attributes[0].ConstructorArguments[0].Value as string ?? ""
                : "");

        var all = entries
            .Combine(plugins.Collect())
            .Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Assembly"));

        context.RegisterSourceOutput(all, static (spc, input) =>
        {
            var ((found, pluginIds), assemblyName) = input;
            Emit(spc, found, pluginIds, assemblyName);
        });
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<Entry> entries, ImmutableArray<string> pluginIds, string assemblyName)
    {
        if (entries.IsEmpty) return;

        var ids = pluginIds.Where(p => p.Length > 0).Distinct().OrderBy(p => p, System.StringComparer.Ordinal).ToList();
        var byOwner = new SortedDictionary<string, List<Entry>>(System.StringComparer.Ordinal);
        var seen = new Dictionary<(string Vocabulary, string Id), Entry>();
        var reportedVocabularies = new HashSet<string>();

        foreach (var e in entries.OrderBy(e => e.TypeName, System.StringComparer.Ordinal).ThenBy(e => e.Id, System.StringComparer.Ordinal))
        {
            if (!e.EntryIsVocabulary)
            {
                if (reportedVocabularies.Add(e.Attribute))
                    spc.ReportDiagnostic(Diagnostic.Create(NotAVocabulary, e.Location, e.Attribute, e.EntryTypeName));
                continue;
            }
            if (e.Id.Trim().Length == 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(EmptyId, e.Location, e.TypeName, e.Attribute));
                continue;
            }
            if (e.Problem != null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Unusable, e.Location, e.TypeName, e.Attribute, e.Problem));
                continue;
            }
            var key = (e.EntryType, Normalize(e.Id));
            if (seen.TryGetValue(key, out var first))
            {
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateId, e.Location, e.Attribute, e.Id, first.TypeName, e.TypeName));
                continue;
            }
            seen[key] = e;

            string? owner = e.Plugin ?? (ids.Count == 1 ? ids[0] : null);
            if (owner == null)
            {
                string has = ids.Count == 0 ? "no [Plugin]" : $"{ids.Count} plugins ({string.Join(", ", ids)})";
                spc.ReportDiagnostic(Diagnostic.Create(NoOwner, e.Location, e.TypeName, e.Attribute, has));
                continue;
            }
            if (!byOwner.TryGetValue(owner, out var list)) byOwner[owner] = list = new List<Entry>();
            list.Add(e);
        }

        string className = "SageVocabularies_" + Identifier(assemblyName);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Sage.Generators (issue #28): the vocabulary entries this assembly declares.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"[assembly: global::{SageTypes.GeneratedRegistrationsAttribute}(typeof(global::Sage.Generated.{className}))]");
        sb.AppendLine();
        sb.AppendLine("namespace Sage.Generated");
        sb.AppendLine("{");
        sb.AppendLine($"    internal sealed class {className} : global::{SageTypes.IGeneratedRegistrations}");
        sb.AppendLine("    {");
        sb.Append("        public global::System.Collections.Generic.IReadOnlyList<string> Owners { get; } = new string[] { ");
        sb.Append(string.Join(", ", byOwner.Keys.Select(Literal)));
        sb.AppendLine(" };");
        sb.AppendLine();
        sb.AppendLine($"        public void Register(string plugin, global::{SageTypes.RegistrationBuilder} builder)");
        sb.AppendLine("        {");
        if (byOwner.Count > 0)
        {
            sb.AppendLine("            switch (plugin)");
            sb.AppendLine("            {");
            foreach (var entry in byOwner)
            {
                sb.AppendLine($"                case {Literal(entry.Key)}:");
                foreach (var e in entry.Value.OrderBy(e => e.EntryType, System.StringComparer.Ordinal).ThenBy(e => e.Id, System.StringComparer.Ordinal))
                    sb.AppendLine($"                    builder.Vocabulary<{e.EntryType}, {e.FullyQualifiedName}>({Literal(e.Id)});");
                sb.AppendLine("                    break;");
            }
            sb.AppendLine("            }");
        }
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("SageVocabularies.g.cs", sb.ToString());
    }

    // Ids match ignoring case, '_' and '-' (Sage.Core.Vocabulary.Normalize), so duplicates do too.
    private static string Normalize(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (char c in id.Trim())
            if (c != '_' && c != '-') sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static string Identifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    // One declared entry: equatable, so unchanged declarations don't re-run the output step.
    private sealed record Entry(
        string EntryType, string EntryTypeName, bool EntryIsVocabulary, string Attribute, string Id, string? Plugin,
        string TypeName, string FullyQualifiedName, string? Problem, LocationInfo? Where)
    {
        public Location Location => Where?.ToLocation() ?? Location.None;

        public static ImmutableArray<Entry> From(GeneratorSyntaxContext ctx)
        {
            var node = (ClassDeclarationSyntax)ctx.Node;
            if (ctx.SemanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol type) return ImmutableArray<Entry>.Empty;
            var entryBase = ctx.SemanticModel.Compilation.GetTypeByMetadataName(SageTypes.VocabularyEntryAttribute);
            if (entryBase == null) return ImmutableArray<Entry>.Empty;
            var vocabularyAttribute = ctx.SemanticModel.Compilation.GetTypeByMetadataName(SageTypes.VocabularyAttribute);

            var result = ImmutableArray.CreateBuilder<Entry>();
            foreach (var attr in type.GetAttributes())
            {
                // Only the attributes written on this part of a partial class, so each is read once.
                if (attr.ApplicationSyntaxReference is not { } applied || applied.SyntaxTree != node.SyntaxTree
                    || !node.Span.Contains(applied.Span))
                    continue;
                ITypeSymbol? entryType = null;
                for (var t = attr.AttributeClass; t != null; t = t.BaseType)
                    if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, entryBase))
                    {
                        entryType = t.TypeArguments[0];
                        break;
                    }
                if (entryType == null) continue;

                string id = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
                string? plugin = null;
                foreach (var named in attr.NamedArguments)
                    if (named.Key == "Plugin") plugin = named.Value.Value as string;

                bool isVocabulary = vocabularyAttribute != null && entryType.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, vocabularyAttribute));

                string attributeName = attr.AttributeClass!.Name;
                if (attributeName.EndsWith("Attribute")) attributeName = attributeName.Substring(0, attributeName.Length - "Attribute".Length);

                result.Add(new Entry(
                    entryType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), entryType.Name, isVocabulary,
                    attributeName, id, plugin, type.Name,
                    type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), ProblemWith(type, entryType),
                    LocationInfo.From(applied.GetSyntax().GetLocation())));
            }
            return result.ToImmutable();
        }

        private static string? ProblemWith(INamedTypeSymbol type, ITypeSymbol entryType)
        {
            if (type.IsAbstract) return "it is abstract";
            if (type.IsGenericType) return "it is generic";
            if (!type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                return "it has no public parameterless constructor (content creates entries with new())";
            bool isEntry = entryType.TypeKind == TypeKind.Interface
                ? type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, entryType))
                : Derives(type, entryType);
            if (!isEntry) return $"it is not a {entryType.Name}";
            for (var t = type; t != null; t = t.ContainingType)
                if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                    return "it is private or nested in a private type";
            return null;
        }

        private static bool Derives(INamedTypeSymbol type, ITypeSymbol baseType)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
                if (SymbolEqualityComparer.Default.Equals(t, baseType)) return true;
            return false;
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
