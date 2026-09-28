using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sage.Generators;

// SAGE0023, strict saves: a public component field that has not decided whether it is saved
// (docs/REDESIGN.md §3.4 and §4.5, issue #19).
//
// Today every public field of a component is saved unless it is [Transient] (SaveAttributes.cs), so a
// field added next month is saved without anyone choosing it — and a designer's rebalance of a value
// that came from content never reaches existing saves. Strict mode makes saving a choice: each public
// field of a [Component] says [Property] (saved, and described to the inspector) or [Transient]
// (never saved). A component type that is itself [Transient] is never saved, so its fields are exempt.
//
// Off by default; a project opts in with <SageStrictSaves>true</SageStrictSaves> (surfaced to the
// compiler by src/ and games/ Directory.Build.props). [Property] is found by its metadata name,
// sage_engine.PropertyAttribute (issue #18); in an engine that does not have it yet only [Transient]
// is a decision, and the message says so.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StrictSavesAnalyzer : DiagnosticAnalyzer
{
    private const string Ns = "sage_engine";
    internal const string Property = "SageStrictSaves";

    internal static readonly DiagnosticDescriptor Undecided = new(
        "SAGE0023", "Strict saves: decide whether a component field is saved",
        "{0}.{1} is public, so it is saved; with {2} on, say so: {3}",
        "Sage.Saves", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Undecided);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (!BuildProperty.IsTrue(start.Options, Property)) return;
            var c = start.Compilation;
            var component = c.GetTypeByMetadataName(Ns + ".ComponentAttribute");
            var transient = c.GetTypeByMetadataName(Ns + ".TransientAttribute");
            if (component == null || transient == null) return;
            var property = c.GetTypeByMetadataName(Ns + ".PropertyAttribute");
            string fix = property != null
                ? "mark it [Property] to save it or [Transient] to leave it out of saves"
                : "mark it [Transient] to leave it out of saves ([Property], which saves it, is not in this engine yet: issue #18)";

            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (!Has(type, component) || Has(type, transient)) return;
                foreach (var member in type.GetMembers())
                {
                    if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public || member.IsImplicitlyDeclared) continue;
                    bool saved = member switch
                    {
                        IFieldSymbol f => !f.IsConst,
                        IPropertySymbol p => !p.IsIndexer && p.GetMethod?.DeclaredAccessibility == Accessibility.Public
                                             && p.SetMethod?.DeclaredAccessibility == Accessibility.Public,
                        _ => false,
                    };
                    if (!saved || Has(member, transient) || (property != null && Has(member, property))) continue;
                    symbolContext.ReportDiagnostic(Diagnostic.Create(Undecided,
                        member.Locations.FirstOrDefault() ?? Location.None, type.Name, member.Name, Property, fix));
                }
            }, SymbolKind.NamedType);
        });
    }

    private static bool Has(ISymbol symbol, INamedTypeSymbol attribute) =>
        symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));
}

// An MSBuild property the project surfaced with <CompilerVisibleProperty Include="Name" />.
internal static class BuildProperty
{
    public static bool IsTrue(AnalyzerOptions options, string name) =>
        options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property." + name, out var value)
        && string.Equals(value?.Trim(), "true", System.StringComparison.OrdinalIgnoreCase);
}
