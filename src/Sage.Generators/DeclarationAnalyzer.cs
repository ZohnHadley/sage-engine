using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sage.Generators;

// What the declaration generators leave unchecked (docs/REDESIGN.md §3.4, issue #19).
//
// Most half-declared types are already errors: an attribute on the wrong kind of type is the compiler's
// CS0592 (every declaration attribute names its targets), a type the registration cannot construct is
// SAGE0002, a malformed component SAGE0005, a part that is not an IPrefabPart SAGE0011, a bad component
// upgrader SAGE0007. Two gaps remain, both of which used to do nothing without a word:
//
//   SAGE0021  [Record("")] / [SavedResource(" ")] (a type no content can name, a resource saved under
//             no key), or [SavedResource] with Version < 1
//   SAGE0022  an [Upgrade] method nothing runs (on a type that is neither a declared component, tag nor
//             saved resource), a saved resource's upgrader with the wrong shape or version, or two
//             upgraders from one version (a run-time throw when the first old save loads)
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeclarationAnalyzer : DiagnosticAnalyzer
{
    private const string Ns = "sage_engine";
    private const string JsonObject = "System.Text.Json.Nodes.JsonObject";

    internal static readonly DiagnosticDescriptor BadName = new(
        "SAGE0021", "A record type or saved resource needs a usable name",
        "{0} is declared with [{1}] but {2}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor BadUpgrader = new(
        "SAGE0022", "An [Upgrade] method must be one something runs",
        "{0}.{1}: {2}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(BadName, BadUpgrader);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var c = start.Compilation;
            var record = c.GetTypeByMetadataName(Ns + ".RecordAttribute");
            var resource = c.GetTypeByMetadataName(Ns + ".SavedResourceAttribute");
            var component = c.GetTypeByMetadataName(Ns + ".ComponentAttribute");
            var tag = c.GetTypeByMetadataName(Ns + ".TagAttribute");
            var upgrade = c.GetTypeByMetadataName(Ns + ".UpgradeAttribute");
            if (record == null && resource == null && upgrade == null) return;
            start.RegisterSymbolAction(
                symbol => Check(symbol, record, resource, component, tag, upgrade),
                SymbolKind.NamedType);
        });
    }

    private static void Check(SymbolAnalysisContext context, INamedTypeSymbol? record, INamedTypeSymbol? resource,
                              INamedTypeSymbol? component, INamedTypeSymbol? tag, INamedTypeSymbol? upgrade)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        AttributeData? declaredResource = null;
        bool declaredComponent = false;
        foreach (var attr in type.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (Is(cls, record)) CheckName(context, type, attr, "Record", "type");
            else if (Is(cls, resource))
            {
                declaredResource = attr;
                CheckName(context, type, attr, "SavedResource", "name");
                if (Version(attr) < 1)
                    context.ReportDiagnostic(Diagnostic.Create(BadName, Where(type, attr), type.Name, "SavedResource",
                        "its Version is below 1; versions start at 1"));
            }
            else if (Is(cls, component) || Is(cls, tag)) declaredComponent = true;
        }

        if (upgrade == null) return;
        // An undeclared IComponent / ITag is SAGE0004 already; its upgraders are not the problem.
        declaredComponent |= type.AllInterfaces.Any(i => i.ToDisplayString() is "Friflo.Engine.ECS.IComponent" or "Friflo.Engine.ECS.ITag");
        var seen = new Dictionary<int, IMethodSymbol>();
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            var attr = method.GetAttributes().FirstOrDefault(a => Is(a.AttributeClass, upgrade));
            if (attr == null) continue;
            var at = method.Locations.FirstOrDefault() ?? Location.None;
            int from = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int f ? f : 0;
            void Bad(string why) => context.ReportDiagnostic(Diagnostic.Create(BadUpgrader, at, type.Name, method.Name, why));

            if (declaredResource == null && !declaredComponent)
            {
                Bad("[Upgrade] runs only on a [Component], [Tag] or [SavedResource] type when an old save is read; on " +
                    "this type nothing ever calls it. Move it to the type whose saved shape changed");
                continue;
            }
            if (seen.TryGetValue(from, out var first))
                Bad($"a second [Upgrade({from})] (the first is {first.Name}); one upgrader per version it upgrades from");
            else seen[from] = method;

            // A component's upgraders are the component generator's (SAGE0007); a saved resource's are here.
            if (declaredResource == null) continue;
            if (!method.IsStatic || !method.ReturnsVoid || method.Parameters.Length != 1
                || method.Parameters[0].RefKind != RefKind.Ref
                || method.Parameters[0].Type.ToDisplayString() != JsonObject)
                Bad("an [Upgrade] method is `static void Name(ref JsonObject o)`");
            int version = Version(declaredResource);
            if (from < 1 || from >= version)
                Bad($"[Upgrade({from})] upgrades from a version before this one (1 to {version - 1}); bump Version first");
        }
    }

    private static void CheckName(SymbolAnalysisContext context, INamedTypeSymbol type, AttributeData attr, string name, string what)
    {
        string value = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
        string? problem = value.Trim().Length == 0 ? $"its {what} is empty"
            : value.Any(char.IsWhiteSpace) ? $"its {what} '{value}' has whitespace in it"
            : null;
        if (problem != null)
            context.ReportDiagnostic(Diagnostic.Create(BadName, Where(type, attr), type.Name, name,
                problem + (name == "Record" ? "; content names a record type by it (e.g. \"item\")" : "; a save stores the resource under it (e.g. \"journal\")")));
    }

    private static int Version(AttributeData attr)
    {
        foreach (var named in attr.NamedArguments)
            if (named.Key == "Version" && named.Value.Value is int v) return v;
        return 1;
    }

    private static bool Is(INamedTypeSymbol? a, INamedTypeSymbol? b) => b != null && SymbolEqualityComparer.Default.Equals(a, b);

    private static Location Where(INamedTypeSymbol type, AttributeData attr) =>
        attr.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? type.Locations.FirstOrDefault() ?? Location.None;
}
