using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sage.Generators;

// Writes each assembly's metadata table (docs/REDESIGN.md §3.4 item 2, issue #18).
//
//   [PrefabPart("light", Plugin = "sage.gameplay.lights")]
//   public sealed class LightPart : IPrefabPart
//   {
//       [Property(Min = 0, Unit = "m", Tooltip = "How far the light reaches")] public float Range = 8f;
//   }
//
// becomes, once per assembly, an IGeneratedMetadata listing every declaration — component, tag, record
// type, saved resource, prefab part — with its id and its fields: type, kind, JSON name, range, unit,
// tooltip, category, enum values, the record type a RecordId names, the asset kind an AssetPath names,
// [Transient], and for lists and nested objects what they hold. Each field gets a getter and a setter as
// plain code, so the inspector edits a component without reflection. Defaults are not copied out of the
// source: the table can make a new instance, and a field's default is what that instance holds.
//
// What it rejects (the attributes only mean something on the right kind of field):
//   SAGE0040  [Property] Min/Max on a field that is not a number or a vector, or Min > Max
//   SAGE0041  [RecordRef] on a field that is not a RecordId (or a list or map of them), or with no type
//   SAGE0042  [AssetKind] on a field that is not an AssetPath (or a list or map of them), or with no kind
//
// Metadata.Reflect reads the same thing by reflection for an assembly built without this, and must agree
// with it (test: TheGeneratedTableMatchesReflectionForEveryEngineDeclaration).
[Generator(LanguageNames.CSharp)]
public sealed class MetadataGenerator : IIncrementalGenerator
{
    private const string Ns = "sage_engine";
    private const int MaxDepth = 3;

    internal static readonly DiagnosticDescriptor BadRange = new(
        "SAGE0040", "[Property] ranges are for numbers",
        "{0}.{1}: {2}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor BadRecordRef = new(
        "SAGE0041", "[RecordRef] is for RecordId fields",
        "{0}.{1}: {2}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    internal static readonly DiagnosticDescriptor BadAssetKind = new(
        "SAGE0042", "[AssetKind] is for AssetPath fields",
        "{0}.{1}: {2}",
        "Sage.Declarations", DiagnosticSeverity.Error, isEnabledByDefault: true);

    // The declaring attributes, in the order a type with more than one is described by (the other
    // generators report a type that has two).
    private static readonly (string Attribute, string Kind)[] Declarations =
    {
        (Ns + ".ComponentAttribute", "Component"),
        (Ns + ".TagAttribute", "Tag"),
        (Ns + ".RecordAttribute", "Record"),
        (Ns + ".SavedResourceAttribute", "SavedResource"),
        (Ns + ".PrefabPartAttribute", "PrefabPart"),
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<Decl>>? all = null;
        for (int i = 0; i < Declarations.Length; i++)
        {
            var (attribute, kind) = Declarations[i];
            int order = i;
            var found = context.SyntaxProvider.ForAttributeWithMetadataName(attribute,
                    static (node, _) => node is TypeDeclarationSyntax,
                    (ctx, _) => Decl.From(ctx, kind, order))
                .Where(static d => d != null)
                .Select(static (d, _) => d!)
                .Collect();
            all = all == null ? found : all.Value.Combine(found).Select(static (pair, _) => pair.Left.AddRange(pair.Right));
        }

        var canEmit = context.CompilationProvider.Select(static (c, _) =>
            c.GetTypeByMetadataName(Ns + ".IGeneratedMetadata") != null);
        var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? "Assembly");

        context.RegisterSourceOutput(all!.Value.Combine(canEmit).Combine(assemblyName), static (spc, input) =>
        {
            var ((declarations, emit), name) = input;
            if (emit) Emit(spc, declarations, name);
        });
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<Decl> declarations, string assemblyName)
    {
        // One entry per type: the first attribute in Declarations' order speaks for it.
        var table = declarations
            .GroupBy(d => d.FullyQualifiedName)
            .Select(g => g.OrderBy(d => d.Order).First())
            .OrderBy(d => d.Order).ThenBy(d => d.Id, StringComparer.Ordinal).ThenBy(d => d.FullyQualifiedName, StringComparer.Ordinal)
            .ToList();

        // A nested type shared by two declarations is checked under both; say so once.
        foreach (var problem in table.SelectMany(d => d.Problems).Distinct())
            spc.ReportDiagnostic(problem.ToDiagnostic());

        var usable = table.Where(d => d.Usable).ToList();
        if (usable.Count == 0) return;

        string className = "SageMetadata_" + Identifier(assemblyName);
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by Sage.Generators (issue #18): the metadata of what this assembly declares.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS0618 // an [Obsolete] field is still described");
        sb.AppendLine($"[assembly: global::{Ns}.GeneratedMetadataAttribute(typeof(global::Sage.Generated.{className}))]");
        sb.AppendLine();
        sb.AppendLine("namespace Sage.Generated");
        sb.AppendLine("{");
        sb.AppendLine($"    internal sealed class {className} : global::{Ns}.IGeneratedMetadata");
        sb.AppendLine("    {");
        sb.AppendLine($"        public global::System.Collections.Generic.IReadOnlyList<global::{Ns}.TypeMetadata> Types {{ get; }} =");
        sb.AppendLine($"            new global::{Ns}.TypeMetadata[]");
        sb.AppendLine("            {");
        foreach (var d in usable)
        {
            string create = d.Creatable ? $"static () => new {d.FullyQualifiedName}()" : "null";
            sb.AppendLine($"                new(global::{Ns}.DeclarationKind.{d.Kind}, {Literal(d.Id)}, typeof({d.FullyQualifiedName}), {create},");
            WriteFields(sb, d.Fields, "                    ");
            sb.AppendLine("),");
        }
        sb.AppendLine("            };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        spc.AddSource("SageMetadata.g.cs", sb.ToString());
    }

    private static void WriteFields(StringBuilder sb, EquatableList<Field> fields, string indent)
    {
        if (fields.Count == 0)
        {
            sb.Append(indent).Append($"global::System.Array.Empty<global::{Ns}.FieldMetadata>()");
            return;
        }
        sb.Append(indent).AppendLine($"new global::{Ns}.FieldMetadata[]");
        sb.Append(indent).AppendLine("{");
        foreach (var f in fields)
        {
            WriteField(sb, f, indent + "    ");
            sb.AppendLine(",");
        }
        sb.Append(indent).Append("}");
    }

    private static void WriteField(StringBuilder sb, Field f, string indent)
    {
        sb.Append(indent).Append($"new global::{Ns}.FieldMetadata({Literal(f.Name)}, typeof({f.TypeName}), global::{Ns}.ValueKind.{f.Kind}");
        if (f.Owner != null)
        {
            string target = f.OwnerIsValueType
                ? $"global::System.Runtime.CompilerServices.Unsafe.Unbox<{f.Owner}>(o)"
                : $"(({f.Owner})o)";
            sb.Append($", static o => (({f.Owner})o).{Escape(f.Name)}");
            sb.Append(f.CanSet ? $", static (o, v) => {target}.{Escape(f.Name)} = ({f.TypeName})v!" : ", null");
        }
        sb.AppendLine(")");
        sb.Append(indent).Append("{ ");
        var parts = new List<string> { $"JsonName = {Literal(f.JsonName)}" };
        if (f.Min != null) parts.Add($"Min = {f.Min}");
        if (f.Max != null) parts.Add($"Max = {f.Max}");
        if (f.Unit != null) parts.Add($"Unit = {Literal(f.Unit)}");
        if (f.Tooltip != null) parts.Add($"Tooltip = {Literal(f.Tooltip)}");
        if (f.Category != null) parts.Add($"Category = {Literal(f.Category)}");
        if (f.EnumValues.Count > 0) parts.Add("EnumValues = new string[] { " + string.Join(", ", f.EnumValues.Select(Literal)) + " }");
        if (f.RecordType != null) parts.Add($"RecordType = {Literal(f.RecordType)}");
        if (f.AssetKind != null) parts.Add($"AssetKind = {Literal(f.AssetKind)}");
        if (f.Transient) parts.Add("Transient = true");
        sb.Append(string.Join(", ", parts));
        if (f.Item != null)
        {
            sb.AppendLine(",");
            sb.Append(indent).AppendLine("  Item =");
            WriteField(sb, f.Item, indent + "    ");
            sb.AppendLine();
            sb.Append(indent);
        }
        if (f.Fields.Count > 0)
        {
            sb.AppendLine(",");
            sb.Append(indent).AppendLine("  Fields =");
            WriteFields(sb, f.Fields, indent + "    ");
            sb.AppendLine();
            sb.Append(indent);
        }
        sb.Append(" }");
    }

    private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static string Identifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    // ---- reading a declaration ------------------------------------------------------------------------

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    private sealed record Decl(string Kind, int Order, string Id, string FullyQualifiedName, bool Creatable, bool Usable,
                               EquatableList<Field> Fields, EquatableList<Problem> Problems)
    {
        public static Decl? From(GeneratorAttributeSyntaxContext ctx, string kind, int order)
        {
            if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;
            var attr = ctx.Attributes[0];
            string id = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string ?? "" : "";
            // The other generators report a generic or unreachable declaration; it has no table entry.
            bool usable = !type.IsGenericType && IsReachable(type) && !type.IsStatic;
            bool creatable = usable && !type.IsAbstract
                && (type.IsValueType || type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public));
            var problems = new List<Problem>();
            var fields = usable ? ReadFields(type, 0, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), problems)
                                : EquatableList<Field>.Empty;
            return new Decl(kind, order, id, type.ToDisplayString(TypeFormat), creatable, usable, fields,
                            new EquatableList<Problem>(problems));
        }
    }

    // A field as the generated code writes it. Owner is the declaring type (null for a list item).
    private sealed record Field(
        string Name, string JsonName, string TypeName, string Kind, string? Owner, bool OwnerIsValueType, bool CanSet,
        string? Min, string? Max, string? Unit, string? Tooltip, string? Category,
        EquatableList<string> EnumValues, string? RecordType, string? AssetKind, bool Transient,
        Field? Item, EquatableList<Field> Fields);

    private static EquatableList<Field> ReadFields(ITypeSymbol type, int depth, HashSet<ITypeSymbol> seen, List<Problem> problems)
    {
        var fields = new List<Field>();
        if (!seen.Add(type)) return EquatableList<Field>.Empty;
        try
        {
            string owner = type.ToDisplayString(TypeFormat);
            var chain = new List<ITypeSymbol>();
            for (var t = type; t != null && t.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); t = t.BaseType)
                chain.Add(t);

            // Fields first, then properties — the order reflection lists them in.
            foreach (var t in chain)
                foreach (var field in t.GetMembers().OfType<IFieldSymbol>())
                {
                    if (field.IsStatic || field.IsConst || field.IsImplicitlyDeclared || field.DeclaredAccessibility != Accessibility.Public
                        || field.IsFixedSizeBuffer || HasAttribute(field, "System.Text.Json.Serialization.JsonIgnoreAttribute"))
                        continue;
                    fields.Add(Describe(field.Name, field.Type, field, owner, type.IsValueType, !field.IsReadOnly, depth, seen, problems, type.Name));
                }
            foreach (var t in chain)
                foreach (var property in t.GetMembers().OfType<IPropertySymbol>())
                {
                    if (property.IsStatic || property.IsIndexer || property.DeclaredAccessibility != Accessibility.Public
                        || property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }
                        || property.SetMethod is not { DeclaredAccessibility: Accessibility.Public } setter || setter.IsInitOnly
                        || HasAttribute(property, "System.Text.Json.Serialization.JsonIgnoreAttribute"))
                        continue;
                    fields.Add(Describe(property.Name, property.Type, property, owner, type.IsValueType, true, depth, seen, problems, type.Name));
                }
        }
        finally
        {
            seen.Remove(type);
        }
        return new EquatableList<Field>(fields);
    }

    private static Field Describe(string name, ITypeSymbol type, ISymbol? member, string? owner, bool ownerIsValueType, bool canSet,
                                  int depth, HashSet<ITypeSymbol> seen, List<Problem> problems, string ownerName)
    {
        string kind = KindOf(type, out var element);
        var property = member == null ? null : Attribute(member, Ns + ".PropertyAttribute");
        string? recordType = member == null ? null : Attribute(member, Ns + ".RecordRefAttribute") is { } rr
            ? (rr.ConstructorArguments.Length > 0 ? rr.ConstructorArguments[0].Value as string ?? "" : "") : null;
        string? assetKind = member == null ? null : Attribute(member, Ns + ".AssetKindAttribute") is { } ak
            ? (ak.ConstructorArguments.Length > 0 ? ak.ConstructorArguments[0].Value as string ?? "" : "") : null;

        double? min = null, max = null;
        string? unit = null, tooltip = null, category = null;
        if (property != null)
            foreach (var named in property.NamedArguments)
            {
                switch (named.Key)
                {
                    case "Min" when named.Value.Value is double v && !double.IsNaN(v): min = v; break;
                    case "Max" when named.Value.Value is double v && !double.IsNaN(v): max = v; break;
                    case "Unit": unit = named.Value.Value as string; break;
                    case "Tooltip": tooltip = named.Value.Value as string; break;
                    case "Category": category = named.Value.Value as string; break;
                }
            }

        Field? item = null;
        var nested = EquatableList<Field>.Empty;
        if (element != null)
        {
            item = Describe("item", element, null, null, false, false, depth + 1, seen, problems, ownerName);
            if (recordType != null && item.Kind == "RecordId" && item.RecordType == null) item = item with { RecordType = recordType };
            if (assetKind != null && item.Kind == "AssetPath") item = item with { AssetKind = assetKind };
        }
        else if (kind == "Object" && depth < MaxDepth)
        {
            nested = ReadFields(type, depth + 1, seen, problems);
        }

        // The checks: each attribute only means something on the right kind of field.
        var at = member?.Locations.FirstOrDefault();
        void Report(DiagnosticDescriptor d, string why) =>
            problems.Add(new Problem(d.Id, LocationInfo.From(at ?? Location.None), new EquatableList<string>(new[] { ownerName, name, why })));
        if (member != null)
        {
            if ((min != null || max != null) && kind is not ("Integer" or "Number" or "Vector2" or "Vector3" or "Vector4"))
                Report(BadRange, $"[Property(Min/Max)] is for a number or a vector, and this is {Short(type)}");
            else if (min != null && max != null && min > max)
                Report(BadRange, $"[Property] has Min {min.Value.ToString(CultureInfo.InvariantCulture)} above Max {max.Value.ToString(CultureInfo.InvariantCulture)}");
            string refersTo = item?.Kind ?? kind;
            string? typedTarget = TypedTarget(element ?? type);
            if (recordType != null && typedTarget != null)
                Report(BadRecordRef, $"{Short(element ?? type)} already names its record type; [RecordRef] is for a plain RecordId");
            else if (recordType != null && refersTo != "RecordId")
                Report(BadRecordRef, $"[RecordRef] names the record type a RecordId points at, and this is {Short(type)}");
            else if (recordType != null && recordType.Trim().Length == 0)
                Report(BadRecordRef, "[RecordRef] needs a record type, e.g. [RecordRef(\"item\")]");
            if (assetKind != null && refersTo != "AssetPath")
                Report(BadAssetKind, $"[AssetKind] names the kind of asset an AssetPath points at, and this is {Short(type)}");
            else if (assetKind != null && assetKind.Trim().Length == 0)
                Report(BadAssetKind, "[AssetKind] needs a kind, e.g. [AssetKind(\"texture\")]");
        }

        var enumType = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
            ? n.TypeArguments[0] : type;
        var enumValues = kind == "Enum"
            ? new EquatableList<string>(enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue)
                  .OrderBy(f => unchecked((ulong)Convert.ToInt64(f.ConstantValue, CultureInfo.InvariantCulture)))
                  .Select(f => f.Name))
            : EquatableList<string>.Empty;

        string jsonName = member != null && Attribute(member, "System.Text.Json.Serialization.JsonPropertyNameAttribute") is { } jp
                          && jp.ConstructorArguments.Length > 0 && jp.ConstructorArguments[0].Value is string explicitName
            ? explicitName
            : Camel(name);

        return new Field(name, jsonName, type.ToDisplayString(TypeFormat), kind, owner, ownerIsValueType, canSet,
            min == null ? null : Format(min.Value), max == null ? null : Format(max.Value), unit, tooltip, category,
            enumValues,
            kind == "RecordId" ? TypedTarget(type) ?? recordType : null,
            kind == "AssetPath" ? assetKind : null,
            member != null && Attribute(member, Ns + ".TransientAttribute") != null,
            item, nested);
    }

    // The same rules as Metadata.KindOf at run time. Numbers, text and vectors are themselves; a RecordId
    // and an AssetPath are references; lists and string-keyed maps say what they hold; a class or struct
    // of the game's own is an object with fields; anything from System (a delegate, a Type) is Other.
    private static string KindOf(ITypeSymbol type, out ITypeSymbol? element)
    {
        element = null;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean: return "Bool";
            case SpecialType.System_SByte: case SpecialType.System_Byte: case SpecialType.System_Int16: case SpecialType.System_UInt16:
            case SpecialType.System_Int32: case SpecialType.System_UInt32: case SpecialType.System_Int64: case SpecialType.System_UInt64:
                return "Integer";
            case SpecialType.System_Single: case SpecialType.System_Double: case SpecialType.System_Decimal: return "Number";
            case SpecialType.System_String: return "String";
        }
        if (type.TypeKind == TypeKind.Enum) return "Enum";

        string name = type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        switch (name)
        {
            case "System.Numerics.Vector2": return "Vector2";
            case "System.Numerics.Vector3": return "Vector3";
            case "System.Numerics.Vector4": return "Vector4";
            case "System.Numerics.Quaternion": return "Quaternion";
            case Ns + ".RecordId": return "RecordId";
            case Ns + ".AssetPath": return "AssetPath";
            case "Friflo.Engine.ECS.Entity": return "Entity";
            case "System.Text.Json.JsonElement": return "Json";
        }
        if (TypedTarget(type) != null) return "RecordId";   // RecordRef<T> (issue #22): a RecordId that knows its type
        for (var t = type; t != null; t = t.BaseType)
            if (t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) == "System.Text.Json.Nodes.JsonNode") return "Json";

        if (type is IArrayTypeSymbol { Rank: 1 } array)
        {
            element = array.ElementType;
            return "List";
        }
        if (type is INamedTypeSymbol { IsGenericType: true } generic)
        {
            string definition = generic.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            var args = generic.TypeArguments;
            if (args.Length == 1 && definition is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>"
                    or "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.HashSet<T>"
                    or "System.Collections.Generic.ICollection<T>" or "System.Collections.Generic.IReadOnlyCollection<T>"
                    or "System.Collections.Generic.IEnumerable<T>")
            {
                element = args[0];
                return "List";
            }
            if (args.Length == 2 && args[0].SpecialType == SpecialType.System_String
                && definition is "System.Collections.Generic.Dictionary<TKey, TValue>" or "System.Collections.Generic.IDictionary<TKey, TValue>"
                    or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>")
            {
                element = args[1];
                return "Map";
            }
        }

        if (type.TypeKind is TypeKind.Delegate or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter
            or TypeKind.Error or TypeKind.Dynamic)
            return "Other";
        string? ns = type.ContainingNamespace?.ToDisplayString();
        if (ns != null && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal))) return "Other";
        if (type.AllInterfaces.Any(i => i.ToDisplayString() == "System.Collections.IEnumerable")) return "Other";
        return "Object";
    }

    // For RecordRef<T>, the record type T is declared as ([Record("name")]; "" when T is not a record
    // type); null for anything else.
    private static string? TypedTarget(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        if (type is not INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic
            || generic.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) != Ns + ".RecordRef<T>")
            return null;
        var record = Attribute(generic.TypeArguments[0], Ns + ".RecordAttribute");
        return record?.ConstructorArguments.Length > 0 ? record.ConstructorArguments[0].Value as string ?? "" : "";
    }

    private static string Short(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture) + "d";

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    private static AttributeData? Attribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name);

    private static bool HasAttribute(ISymbol symbol, string name) => Attribute(symbol, name) != null;

    private static bool IsReachable(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
            if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                return false;
        return true;
    }

    // A diagnostic, kept as equatable parts until it is reported.
    private sealed record Problem(string Id, LocationInfo? Where, EquatableList<string> Args)
    {
        public Diagnostic ToDiagnostic()
        {
            var descriptor = Id switch
            {
                "SAGE0040" => BadRange,
                "SAGE0041" => BadRecordRef,
                _ => BadAssetKind,
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
