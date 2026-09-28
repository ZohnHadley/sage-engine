using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sage.Generators;

// SAGE0024: a MonoGame type in a simulation assembly (ARCHITECTURE §2 rule 2, docs/REDESIGN.md §3.1,
// issue #19).
//
// The simulation — the engine and a game's rules — must run headless, so it never touches MonoGame.
// Sage.Engine enforces that by not referencing MonoGame at all; a game's simulation project did it by
// habit (Sandbox.csproj references only Sage.Engine), and one package reference would have undone it
// without a word. A project declares itself simulation-only with
//
//   <SageSimulationOnly>true</SageSimulationOnly>
//
// (surfaced to the compiler by src/ and games/ Directory.Build.props), and then any use of a type from
// Microsoft.Xna.Framework is an error at the use. The client half (Sandbox.Client) does not set it.
// A small analyzer rather than Microsoft.CodeAnalysis.BannedApiAnalyzers: one namespace, one rule, and
// no package or banned-symbols file to keep in step.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SimulationOnlyAnalyzer : DiagnosticAnalyzer
{
    internal const string Property = "SageSimulationOnly";
    private const string Banned = "Microsoft.Xna.Framework";

    internal static readonly DiagnosticDescriptor ClientType = new(
        "SAGE0024", "No MonoGame in a simulation assembly",
        "{0} is a MonoGame type ({1}), and this assembly is simulation-only ({2}); what needs a screen goes in the client half",
        "Sage.Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(ClientType);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (!BuildProperty.IsTrue(start.Options, Property)) return;
            // Nothing to find when no reference (and no source) has the namespace at all.
            if (Find(start.Compilation.GlobalNamespace, Banned) == null) return;
            start.RegisterSyntaxNodeAction(Check, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
        });
    }

    private static void Check(SyntaxNodeAnalysisContext context)
    {
        var name = (SimpleNameSyntax)context.Node;
        // Only names that bind to a type: `Microsoft.Xna.Framework.Vector3` is reported once, at
        // `Vector3`, and `var` is reported where it stands for a MonoGame type.
        var symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        var type = symbol as ITypeSymbol;
        if (type == null) return;   // members are reached through their type, which is reported there
        while (type is IArrayTypeSymbol array) type = array.ElementType;
        if (type is not INamedTypeSymbol named || !InBanned(named.ContainingNamespace)) return;

        // Code declared in the namespace itself (a stand-in, in a test) is MonoGame, not a use of it.
        var enclosing = context.SemanticModel.GetEnclosingSymbol(name.SpanStart, context.CancellationToken);
        if (InBanned(enclosing as INamespaceSymbol ?? enclosing?.ContainingNamespace)) return;

        context.ReportDiagnostic(Diagnostic.Create(ClientType, name.GetLocation(),
            named.Name, named.ContainingNamespace.ToDisplayString(), Property));
    }

    private static bool InBanned(INamespaceSymbol? ns)
    {
        for (; ns != null && !ns.IsGlobalNamespace; ns = ns.ContainingNamespace)
            if (ns.ToDisplayString() == Banned) return true;
        return false;
    }

    private static INamespaceSymbol? Find(INamespaceSymbol root, string dotted)
    {
        var ns = root;
        foreach (string part in dotted.Split('.'))
        {
            INamespaceSymbol? next = null;
            foreach (var member in ns.GetNamespaceMembers())
                if (member.Name == part) { next = member; break; }
            if (next == null) return null;
            ns = next;
        }
        return ns;
    }
}
