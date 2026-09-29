using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sage.Generators;

// SAGE0050: Friflo named outside the ECS implementation (docs/REDESIGN.md §3.5, issue #25).
//
// The ECS vocabulary is Sage's: `Entity`, `IComponent`, `ITag`, `Tags`, `Query<…>`, `EntityCommands`,
// all in Sage.Simulation. Friflo is the storage underneath, and a game, a mod or another engine assembly
// that names a Friflo type, member or namespace ties itself to Friflo 3.x's API and breaks the day the
// ECS is upgraded or swapped. The reference cannot simply be hidden — Sage's IComponent extends
// Friflo's, so every game compiles against Friflo's assembly — so naming it is the build error instead.
// The one project that implements the vocabulary says so with
//
//   <SageEcsImplementation>true</SageEcsImplementation>
//
// (Sage.Simulation's csproj), and inside it Friflo is written behind `using F = Friflo.Engine.ECS;`.
// Deliberate exceptions elsewhere take a scoped `#pragma warning disable SAGE0050` with a reason.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EcsVocabularyAnalyzer : DiagnosticAnalyzer
{
    internal const string Property = "SageEcsImplementation";
    private const string Friflo = SageTypes.FrifloNamespace;

    internal static readonly DiagnosticDescriptor FrifloUse = new(
        "SAGE0050", "Friflo is the ECS's storage, not its vocabulary",
        "{0} is Friflo's; name the ECS through Sage.Simulation (Entity, IComponent, ITag, Tags, Query<...>, EntityCommands) " +
        "so a game does not break when the storage underneath changes (only a project with {1}=true implements it)",
        "Sage.Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(FrifloUse);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (BuildProperty.IsTrue(start.Options, Property)) return;
            // Nothing to find in a compilation that cannot see Friflo.
            bool seesFriflo = false;
            foreach (var reference in start.Compilation.References)
                if (start.Compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol a && IsFriflo(a.Identity.Name))
                    seesFriflo = true;
            if (!seesFriflo) return;
            start.RegisterSyntaxNodeAction(Check, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
        });
    }

    private static void Check(SyntaxNodeAnalysisContext context)
    {
        var name = (SimpleNameSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol == null) return;

        if (symbol is INamespaceSymbol ns)
        {
            // A namespace is reported only where it is imported (`using Friflo.Engine.ECS;`, or an alias
            // of it), once, at its last part; written in front of a type, the type is what is reported.
            if (!IsFriflo(ns.ToDisplayString())) return;
            if (name.Parent is QualifiedNameSyntax q && q.Right == name) { if (q.Parent is UsingDirectiveSyntax) Report(context, name, symbol); }
            else if (name.Parent is UsingDirectiveSyntax) Report(context, name, symbol);
            return;
        }

        if (symbol.ContainingAssembly is not { } owner || !IsFriflo(owner.Identity.Name)) return;

        // A member is reached through a type or a value, and either was a Friflo type written somewhere
        // (a Sage API never hands one out): that is where it is reported. Only a member written bare
        // (`using static`) is reported where it stands.
        if (symbol is not ITypeSymbol && name.Parent is MemberAccessExpressionSyntax access && access.Name == name) return;
        if (symbol is not ITypeSymbol && name.Parent is MemberBindingExpressionSyntax) return;

        Report(context, name, symbol);
    }

    private static void Report(SyntaxNodeAnalysisContext context, SimpleNameSyntax name, ISymbol symbol) =>
        context.ReportDiagnostic(Diagnostic.Create(FrifloUse, name.GetLocation(),
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), Property));

    private static bool IsFriflo(string name) =>
        name == Friflo || name.StartsWith(Friflo + ".", System.StringComparison.Ordinal);
}
