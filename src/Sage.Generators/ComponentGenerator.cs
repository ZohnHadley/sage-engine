using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sage.Generators;

// Writes each assembly's component and tag id table (docs/REDESIGN.md §3.4, issues #16 and #20).
//
//   [Component("sage:health", Version = 2)] public struct Health : IComponent { … }
//   [Tag("sage:player_controlled")]         public struct PlayerControlled : ITag { }
//
// becomes, once per assembly, an IGeneratedComponents listing (type, id, version, former names) that
// ComponentSchema reads for every loaded assembly. Unlike [Record] (RegistrationGenerator) nothing is
// registered per plugin: a component type is in the ECS schema whether its plugin is loaded or not, so
// its id is a fact about the assembly.
//
// Separate from RegistrationGenerator on purpose: that one groups declarations by owning plugin, this
// one only lists, and keeping them apart lets each change without the other.
//
// It also enforces the declaration, because the point is that nothing is left unnamed:
//   SAGE0004  an IComponent or ITag struct without [Component] / [Tag]
//   SAGE0005  a malformed declaration (bad id, Version < 1, wrong interface, unreachable, generic)
//   SAGE0006  two components (or two tags) with one id in the assembly
//   SAGE0007  an [Upgrade] method with the wrong signature or version
[Generator(LanguageNames.CSharp)]
public sealed class ComponentGenerator : IIncrementalGenerator
{
    private const string ComponentAttribute = SageTypes.ComponentAttribute;
    private const string TagAttribute = SageTypes.TagAttribute;
    private const string UpgradeAttribute = SageTypes.UpgradeAttribute;
    private const string IComponent = SageTypes.IComponent;
    private const string ITag = SageTypes.ITag;
    private const string JsonObject = "System.Text.Json.Nodes.JsonObject";

    internal static readonly DiagnosticDescriptor Undeclared = new(
        "SAGE0004", "Give every component and tag a stable id",
        "{0} is an {1} but has no [{2}(\"namespace:name\")]; content and saves name it by that id (issue #16)",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor Malformed = new(
        "SAGE0005", "A component declaration must be well formed",
        "{0}: {1}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor DuplicateId = new(
        "SAGE0006", "One component id, one type",
        "[{0}(\"{1}\")] is declared by both {2} and {3}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor BadUpgrader = new(
        "SAGE0007", "An upgrader must fit the component's versions",
        "{0}.{1}: {2}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var structs = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is StructDeclarationSyntax s && (s.BaseList != null || s.AttributeLists.Count > 0),
                static (ctx, _) => Declaration.From(ctx))
            .Where(static d => d != null)
            .Select(static (d, _) => d!);

        // Only where the attributes exist: an assembly that does not reference the engine has nothing
        // to name its components with (and the engine's own table is written in the engine).
        var canEmit = context.CompilationProvider.Select(static (c, _) =>
            c.GetTypeByMetadataName(SageTypes.IGeneratedComponents) != null);
        var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Assembly");

        context.RegisterSourceOutput(structs.Collect().Combine(canEmit).Combine(assemblyName), static (spc, input) =>
        {
            var ((declarations, emit), name) = input;
            if (emit) Emit(spc, declarations, name);
        });
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<Declaration> declarations, string assemblyName)
    {
        var table = new List<Declaration>();
        var seen = new Dictionary<(bool, string), Declaration>();

        foreach (var d in declarations.OrderBy(d => d.FullyQualifiedName, StringComparer.Ordinal))
        {
            foreach (var problem in d.Problems) spc.ReportDiagnostic(problem.ToDiagnostic());
            if (d.Id == null || d.Problems.Count > 0) continue;
            if (seen.TryGetValue((d.IsTag, d.Id), out var first))
            {
                spc.ReportDiagnostic(Diagnostic.Create(DuplicateId, d.Where?.ToLocation() ?? Location.None,
                    d.IsTag ? "Tag" : "Component", d.Id, first.TypeName, d.TypeName));
                continue;
            }
            seen[(d.IsTag, d.Id)] = d;
            table.Add(d);
        }
        if (table.Count == 0) return;

        string className = "SageComponents_" + Identifier(assemblyName);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Sage.Generators (issue #16): the components and tags this assembly declares.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine($"[assembly: global::{SageTypes.GeneratedComponentsAttribute}(typeof(global::Sage.Generated.{className}))]");
        sb.AppendLine();
        sb.AppendLine("namespace Sage.Generated");
        sb.AppendLine("{");
        sb.AppendLine($"    internal sealed class {className} : global::{SageTypes.IGeneratedComponents}");
        sb.AppendLine("    {");
        sb.AppendLine($"        public global::System.Collections.Generic.IReadOnlyList<global::{SageTypes.ComponentDeclaration}> Declarations {{ get; }} =");
        sb.AppendLine($"            new global::{SageTypes.ComponentDeclaration}[]");
        sb.AppendLine("            {");
        foreach (var d in table)
        {
            string former = d.FormerNames.Count == 0
                ? "global::System.Array.Empty<string>()"
                : "new string[] { " + string.Join(", ", d.FormerNames.Select(Literal)) + " }";
            sb.AppendLine($"                new(typeof({d.FullyQualifiedName}), {Literal(d.Id!)}, {d.Version}, {former}, {(d.IsTag ? "true" : "false")}),");
        }
        sb.AppendLine("            };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("SageComponents.g.cs", sb.ToString());
    }

    // `namespace:name`, both halves in the record id alphabet: ComponentDeclaration.IsValidId's rule.
    public static bool IsValidId(string id)
    {
        int colon = id.IndexOf(':');
        if (colon <= 0 || colon == id.Length - 1 || id.IndexOf(':', colon + 1) >= 0) return false;
        foreach (char c in id)
            if (!(c == ':' || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-'))
                return false;
        return true;
    }

    private static string Identifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    // One struct that is a component, a tag, or claims to be one — equatable, so an unchanged struct
    // does not re-run the output step.
    private sealed record Declaration(
        string TypeName, string FullyQualifiedName, bool IsTag, string? Id, int Version,
        EquatableList<string> FormerNames, EquatableList<Problem> Problems, LocationInfo? Where)
    {
        public static Declaration? From(GeneratorSyntaxContext ctx)
        {
            if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) is not INamedTypeSymbol type) return null;
            // A partial struct is seen once per part; only its first part speaks for it.
            var firstPart = type.DeclaringSyntaxReferences.FirstOrDefault();
            if (firstPart != null && (firstPart.SyntaxTree != ctx.Node.SyntaxTree || firstPart.Span != ctx.Node.Span)) return null;

            bool isComponent = type.AllInterfaces.Any(i => i.ToDisplayString() == IComponent);
            bool isTag = type.AllInterfaces.Any(i => i.ToDisplayString() == ITag);
            AttributeData? component = null, tag = null;
            foreach (var attr in type.GetAttributes())
            {
                string? name = attr.AttributeClass?.ToDisplayString();
                if (name == ComponentAttribute) component = attr;
                else if (name == TagAttribute) tag = attr;
            }
            if (!isComponent && !isTag && component == null && tag == null) return null;

            var where = LocationInfo.From(((StructDeclarationSyntax)ctx.Node).Identifier.GetLocation());
            var problems = new List<Problem>();
            string typeName = type.Name;
            string fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            var declared = component ?? tag;
            if (declared == null)
            {
                problems.Add(new Problem(Undeclared.Id, where, new EquatableList<string>(new[]
                    { typeName, isTag ? "ITag" : "IComponent", isTag ? "Tag" : "Component" })));
                return new Declaration(typeName, fqn, isTag, null, 1, EquatableList<string>.Empty, new EquatableList<Problem>(problems), where);
            }

            bool declaredTag = declared == tag;
            string id = declared.ConstructorArguments.Length > 0 ? declared.ConstructorArguments[0].Value as string ?? "" : "";
            int version = 1;
            var former = new List<string>();
            foreach (var named in declared.NamedArguments)
            {
                if (named.Key == "Version" && named.Value.Value is int v) version = v;
                else if (named.Key == "FormerNames" && !named.Value.IsNull)
                    foreach (var item in named.Value.Values)
                        if (item.Value is string s) former.Add(s);
            }

            void Malformed(string why) =>
                problems.Add(new Problem(ComponentGenerator.Malformed.Id, where, new EquatableList<string>(new[] { typeName, why })));

            if (component != null && tag != null) Malformed("it has both [Component] and [Tag]; it is one or the other");
            else if (declaredTag && !isTag) Malformed("[Tag] is for a struct that implements ITag");
            else if (!declaredTag && !isComponent) Malformed("[Component] is for a struct that implements IComponent");
            if (!IsValidId(id))
                Malformed($"'{id}' is not a component id: namespace:name, lower-case letters, digits, _ . - (e.g. \"sage:health\")");
            if (version < 1) Malformed("Version starts at 1");
            if (type.IsGenericType) Malformed("a component cannot be generic");
            if (!IsReachable(type)) Malformed("it is private or nested in a private type, so the id table cannot name it");

            foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
            {
                var upgrade = member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == UpgradeAttribute);
                if (upgrade == null) continue;
                var at = LocationInfo.From(member.Locations.FirstOrDefault() ?? Location.None) ?? where;
                void Bad(string why) =>
                    problems.Add(new Problem(BadUpgrader.Id, at, new EquatableList<string>(new[] { typeName, member.Name, why })));

                if (!member.IsStatic || !member.ReturnsVoid || member.Parameters.Length != 1
                    || member.Parameters[0].RefKind != RefKind.Ref
                    || member.Parameters[0].Type.ToDisplayString() != JsonObject)
                    Bad("an [Upgrade] method is `static void Name(ref JsonObject o)`");
                int from = upgrade.ConstructorArguments.Length > 0 && upgrade.ConstructorArguments[0].Value is int f ? f : 0;
                if (from < 1 || from >= version)
                    Bad($"[Upgrade({from})] upgrades from a version before this one (1 to {version - 1}); bump Version first");
            }

            return new Declaration(typeName, fqn, declaredTag, id, version, new EquatableList<string>(former),
                                   new EquatableList<Problem>(problems), where);
        }

        private static bool IsReachable(INamedTypeSymbol type)
        {
            for (var t = type; t != null; t = t.ContainingType)
                if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected
                    or Accessibility.ProtectedAndInternal)
                    return false;
            return true;
        }
    }

    // A diagnostic, kept as equatable parts until it is reported.
    private sealed record Problem(string Id, LocationInfo? Where, EquatableList<string> Args)
    {
        public Diagnostic ToDiagnostic()
        {
            var descriptor = Id switch
            {
                "SAGE0004" => Undeclared,
                "SAGE0005" => Malformed,
                _ => BadUpgrader,
            };
            return Diagnostic.Create(descriptor, Where?.ToLocation() ?? Location.None, Args.Cast<object>().ToArray());
        }
    }

    private sealed record LocationInfo(string Path, Microsoft.CodeAnalysis.Text.TextSpan Span,
                                       Microsoft.CodeAnalysis.Text.LinePositionSpan Lines)
    {
        public Location ToLocation() => Location.Create(Path, Span, Lines);

        public static LocationInfo? From(Location location) =>
            location.SourceTree is null ? null
                : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }

    // A list compared by its items, which is what an incremental generator's cache needs.
    private sealed class EquatableList<T> : IEquatable<EquatableList<T>>, IReadOnlyList<T>
    {
        public static readonly EquatableList<T> Empty = new(Array.Empty<T>());
        private readonly T[] _items;

        public EquatableList(IEnumerable<T> items) { _items = items.ToArray(); }

        public int Count => _items.Length;
        public T this[int index] => _items[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public bool Equals(EquatableList<T>? other) => other != null && _items.SequenceEqual(other._items);
        public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

        public override int GetHashCode()
        {
            int hash = 17;
            foreach (var item in _items) hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }
    }
}
