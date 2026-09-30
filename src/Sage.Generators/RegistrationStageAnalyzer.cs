using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sage.Generators;

// SAGE0020: a registration made after the Register stage (docs/REDESIGN.md §3.4, issue #19).
//
// A module registers what it adds — cvars, commands, input actions, entity inputs, record types, prefab
// parts, saved resources, render passes (issue 4h-1) — in Init. SageApp seals each registry at the stage after which registering
// would be too late (RegistrationSeal, issue #12), so a late one already throws at run time; this moves
// the plain cases to the build:
//
//   public void Start(ModuleContext ctx) { cvars.Register("fov", 90f, ...); }   // SAGE0020
//
// "Late" is a registration call written inside a module's Start or OnWorldCreated, a game module's
// CreateRules, or a system's constructor or Run (a system is built in OnWorldCreated). Lambdas and
// local functions inside those bodies count too: code written there cannot run before the method it
// is written in, so it is at least as late. A helper method those bodies call is not followed — this
// is a syntax-level check that never guesses; the run-time seal still catches the rest.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RegistrationStageAnalyzer : DiagnosticAnalyzer
{

    internal static readonly DiagnosticDescriptor Late = new(
        "SAGE0020", "Register in Init, not later",
        "{0} registers {1} in {2}, which runs after the Register stage ({3}); register it in the module's Init{4}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    // The registries and the calls on them that register: what each registers, and why late is wrong
    // (the seal's own words where there is one, RegistrationSeal).
    private static readonly (string Type, string Method, string What, string Why)[] Registrations =
    {
        (SageTypes.CVarRegistry, "Register", "a cvar",
            "cvars are sealed once config.cfg is read, so its saved value would never apply"),
        (SageTypes.CVarRegistry, "RegisterCommand", "a console command",
            "commands are registered once, with the cvars; per world or per system it is registered twice"),
        (SageTypes.ActionRegistry, "Register", "an input action",
            "actions are sealed when content loads, and the bindings built then have none for it"),
        (SageTypes.EntityInputs, "Register", "an entity input",
            "entity inputs are sealed when the first world exists, and its wiring is checked without it"),
        (SageTypes.RecordStore, "Register", "a record type",
            "record types are sealed when records load, and its records were skipped as unknown"),
        (SageTypes.PrefabRegistry, "Register", "a prefab part",
            "prefab parts are sealed when the first world exists, and prefabs may have spawned without it"),
        (SageTypes.SaveSystem, "RegisterResource", "a saved resource",
            "a save written or read before it has left the resource out"),
        (SageTypes.SaveSystem, "AddConverter", "a save converter",
            "a save written or read before it has used the default shape, which does not load back"),
        (SageTypes.Vocabulary, "Register", "a vocabulary entry",
            "vocabularies are sealed when content loads, and the records naming it were read without it"),
        (SageTypes.ModuleManager, "Add", "a module",
            "modules are sealed once Init has run, so it would never run Init"),
        (SageTypes.RenderPassRegistry, "Add", "a render pass",
            "render passes are sealed and ordered when the client starts, so it would never draw"),
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Late);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var module = start.Compilation.GetTypeByMetadataName(SageTypes.IModule);
            if (module == null) return;   // not built against the engine: nothing registers here
            var game = start.Compilation.GetTypeByMetadataName(SageTypes.IGameModule);
            var system = start.Compilation.GetTypeByMetadataName(SageTypes.ISystem);

            var registries = new Dictionary<(INamedTypeSymbol, string), (string What, string Why)>();
            foreach (var (type, method, what, why) in Registrations)
                if (start.Compilation.GetTypeByMetadataName(type) is { } registry)
                    registries[(registry, method)] = (what, why);

            // The interface methods that run after Init.
            var late = new List<IMethodSymbol>();
            foreach (var (type, name) in new[] { (module, "Start"), (module, "OnWorldCreated"), (game, "CreateRules") })
                if (type != null)
                    late.AddRange(type.GetMembers(name).OfType<IMethodSymbol>());

            start.RegisterSyntaxNodeAction(
                node => Check(node, registries, late, system),
                SyntaxKind.InvocationExpression);
        });
    }

    private static void Check(SyntaxNodeAnalysisContext context,
                              Dictionary<(INamedTypeSymbol, string), (string What, string Why)> registries,
                              List<IMethodSymbol> late, INamedTypeSymbol? system)
    {
        var call = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(call, context.CancellationToken).Symbol is not IMethodSymbol called) return;
        var original = (called.ReducedFrom ?? called).OriginalDefinition;
        if (original.ContainingType is not { } registry
            || !registries.TryGetValue((registry, original.Name), out var registration)) return;

        // The member whose body the call is written in, looking through lambdas and local functions.
        SyntaxNode? body = null;
        for (var node = call.Parent; node != null; node = node.Parent)
            if (node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or PropertyDeclarationSyntax
                or FieldDeclarationSyntax or TypeDeclarationSyntax)
            {
                body = node;
                break;
            }
        if (body is not BaseMethodDeclarationSyntax) return;
        if (context.SemanticModel.GetDeclaredSymbol(body, context.CancellationToken) is not IMethodSymbol member) return;
        var type = member.ContainingType;
        if (type == null) return;

        string? where = null, tail = "";
        if (member.MethodKind == MethodKind.Constructor && !member.IsStatic && Implements(type, system))
        {
            where = type.Name + "'s constructor";
            tail = " and pass what the system needs to its constructor (a system is built in OnWorldCreated)";
        }
        else if (system != null && IsImplementationOf(type, member, system.GetMembers("Run")))
        {
            where = type.Name + ".Run";
            tail = " and pass what the system needs to its constructor";
        }
        else
        {
            foreach (var interfaceMember in late)
                if (IsImplementationOf(type, member, ImmutableArray.Create<ISymbol>(interfaceMember)))
                {
                    where = type.Name + "." + interfaceMember.Name;
                    break;
                }
        }
        if (where == null) return;

        context.ReportDiagnostic(Diagnostic.Create(Late, call.GetLocation(),
            original.ContainingType.Name + "." + original.Name, registration.What, where, registration.Why, tail));
    }

    private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol? @interface)
    {
        if (@interface == null) return false;
        foreach (var i in type.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(i, @interface)) return true;
        return false;
    }

    // Whether `member` (or a method it overrides) is what `type` runs for one of `interfaceMembers`.
    private static bool IsImplementationOf(INamedTypeSymbol type, IMethodSymbol member, ImmutableArray<ISymbol> interfaceMembers)
    {
        foreach (var interfaceMember in interfaceMembers)
        {
            if (!Implements(type, interfaceMember.ContainingType)) continue;
            var implementation = type.FindImplementationForInterfaceMember(interfaceMember);
            if (implementation == null) continue;
            // The implementation itself, or an override of it (a module deriving from a module).
            for (var m = member; m != null; m = m.OverriddenMethod)
                if (SymbolEqualityComparer.Default.Equals(m, implementation)) return true;
        }
        return false;
    }
}
