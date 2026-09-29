using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sage.Generators;

// SAGE0025: a base assembly that depends on a kit (docs/REDESIGN.md §0.5 and §3.1, issue #24).
//
// The base engine is what every genre shares; a kit (Sage.Kits.Rpg, later Shooter, Platformer…) is
// built on it with public API only. The day Sage.Gameplay names a spellbook screen, a platformer has
// to ship the RPG kit to compile — so it is a build error, in both shapes it can take: a reference to a
// Sage.Kits.* assembly (reported once, for the project) and each use of a type from one (at the use).
// A project says it is part of the base with
//
//   <SageBaseAssembly>true</SageBaseAssembly>
//
// which src/Directory.Build.props sets for Sage.Core, Simulation, Physics3D, Gameplay, Client and
// Editor. A kit, a game and a host do not set it.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class KitReferenceAnalyzer : DiagnosticAnalyzer
{
    internal const string Property = "SageBaseAssembly";
    private const string Kits = "Sage.Kits";

    private const string Title = "A base assembly never references a kit";
    private const string Message = "{0} is from the kit {1}, and this is a base assembly ({2}); the base engine is built without kits — move what needs the kit into the kit, or make the kit plug into the base";

    // The reference itself, reported once for the project when the compilation ends.
    internal static readonly DiagnosticDescriptor KitReference = new(
        "SAGE0025", Title, Message, "Sage.Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    // Each use of a kit's type or member, at the use.
    internal static readonly DiagnosticDescriptor KitUse = new(
        "SAGE0025", Title, Message, "Sage.Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(KitReference, KitUse);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (!BuildProperty.IsTrue(start.Options, Property)) return;

            bool any = false;
            foreach (var reference in start.Compilation.References)
            {
                if (start.Compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly || !IsKit(assembly)) continue;
                any = true;
                string name = assembly.Identity.Name;
                start.RegisterCompilationEndAction(end => end.ReportDiagnostic(
                    Diagnostic.Create(KitReference, Location.None, "The project reference", name, Property)));
            }
            if (any) start.RegisterSyntaxNodeAction(Check, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
        });
    }

    private static void Check(SyntaxNodeAnalysisContext context)
    {
        var name = (SimpleNameSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol is INamespaceSymbol) return;   // `using Sage.Kits.Rpg;` is not a use; the reference says it
        var owner = symbol?.ContainingAssembly;
        if (owner == null || !IsKit(owner)) return;
        // A member reached through its type (`Spellbook.Open`) is reported at the type, once; one
        // reached without naming it (an extension method, `using static`) is reported where it stands.
        if (symbol is not ITypeSymbol && name.Parent is MemberAccessExpressionSyntax access && access.Name == name
            && context.SemanticModel.GetSymbolInfo(access.Expression, context.CancellationToken).Symbol is ITypeSymbol) return;
        context.ReportDiagnostic(Diagnostic.Create(KitUse, name.GetLocation(),
            symbol!.Name, owner.Identity.Name, Property));
    }

    private static bool IsKit(IAssemblySymbol assembly)
    {
        string name = assembly.Identity.Name;
        return name == Kits || name.StartsWith(Kits + ".", System.StringComparison.Ordinal);
    }
}
