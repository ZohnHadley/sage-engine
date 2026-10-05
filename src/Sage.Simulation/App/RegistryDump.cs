#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// Everything an app registered, as JSON (docs/REDESIGN.md §4.8 "Registry dump", issue #18): every
// console command, cvar, record type, component, tag, saved resource, prefab part, system, entity input
// and output, game event (issue #298), input action and vocabulary entry (issue #28), each with the plugin
// that registered it (the ledger, issue #12) and, for the declarations, their fields from the metadata table.
//
//   Sage.Host -game games/Sandbox -dump-registry build/registry.json    the host: boots, writes, quits
//   RegistryDump.Write(app.Engine, path)                                a test or a tool, headless
//
// What reads it: tools/check_docs.py, which used to find command and cvar names with regular expressions
// over call shapes — and silently lost 24 record types the day [Record] grew a Plugin argument. A name
// in here was registered by running code, so renaming the API that registers it cannot hide it.
//
// The format is versioned (`format`); a reader checks it. Fields are written as the metadata table has
// them, so a JSON Schema (#21) can be generated from this file as well as from the table itself.
public static class RegistryDump
{
    public const int Format = 1;

    public static void Write(Engine engine, string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, Build(engine));
    }

    public static string Build(Engine engine)
    {
        var ledger = engine.Registrations;
        var json = engine.Records.Json;
        var root = new JsonObject
        {
            ["format"] = Format,
            ["engine"] = BuildInfo.EngineVersion.ToString(),
            ["config"] = BuildInfo.Config.ToString(),
            ["game"] = engine.Modules.Game is { } game ? engine.Modules.Plugin(game).Id : null,
        };

        root["plugins"] = Array(engine.Modules.Modules.Select(m =>
        {
            var info = engine.Modules.Plugin(m);
            return new JsonObject { ["id"] = info.Id, ["version"] = info.Version.ToString(), ["kind"] = info.Kind.ToString(), ["name"] = m.Name };
        }));

        root["commands"] = Array(engine.CVars.Commands.Select(c => new JsonObject
        {
            ["name"] = c.Name, ["flags"] = c.Flags.ToString(), ["help"] = c.Help, ["owner"] = ledger.OwnerOf("command", c.Name),
        }));

        root["cvars"] = Array(engine.CVars.CVars.Select(c => new JsonObject
        {
            ["name"] = c.Name, ["type"] = c.TypeName, ["default"] = c.DefaultString, ["flags"] = c.Flags.ToString(),
            ["help"] = c.Help, ["owner"] = ledger.OwnerOf("cvar", c.Name),
        }));

        root["recordTypes"] = Array(engine.Records.TypeNames.Select(name =>
        {
            var o = new JsonObject { ["name"] = name, ["owner"] = ledger.OwnerOf("record type", name) };
            if (engine.Records.TypeOf(name) is { } type) Describe(o, Metadata.Of(type), json);
            return o;
        }));

        var declarations = engine.Components.Declarations.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
        root["components"] = Array(declarations.Where(d => !d.IsTag).Select(d =>
        {
            var o = new JsonObject
            {
                ["id"] = d.Id, ["version"] = d.Version, ["assembly"] = d.Type.Assembly.GetName().Name,
                ["transient"] = d.Type.IsDefined(typeof(TransientAttribute), false),
                ["formerNames"] = Array(d.FormerNames.Select(n => (JsonNode?)n)),
            };
            Describe(o, Metadata.Of(d.Type), json);
            return o;
        }));
        root["tags"] = Array(declarations.Where(d => d.IsTag).Select(d => new JsonObject
        {
            ["id"] = d.Id, ["assembly"] = d.Type.Assembly.GetName().Name, ["clrType"] = d.Type.FullName,
            ["transient"] = d.Type.IsDefined(typeof(TransientAttribute), false),
        }));

        root["savedResources"] = Array(engine.Saves.Resources.Select(r =>
        {
            var o = new JsonObject { ["id"] = r.Name, ["version"] = r.Version, ["owner"] = ledger.OwnerOf("saved resource", r.Name) };
            Describe(o, Metadata.Of(r.Type), json);
            return o;
        }));

        root["prefabParts"] = Array(engine.Prefabs.Parts.Select(p =>
        {
            var o = new JsonObject
            {
                ["id"] = p.Id, ["owner"] = p.Owner, ["after"] = Array(p.After.Select(a => (JsonNode?)a)),
                ["shorthand"] = p.Shorthand is { } s ? Metadata.Camel(s) : null,
            };
            Describe(o, Metadata.Of(p.Type), json);
            return o;
        }));

        root["systems"] = Array(engine.SystemCatalog.All.Select(d => new JsonObject
        {
            ["id"] = d.Id, ["phase"] = d.Phase.ToString(), ["clrType"] = d.Type.FullName,
            ["assembly"] = d.Type.Assembly.GetName().Name,
            ["before"] = Array(d.Before.Select(b => (JsonNode?)b)), ["after"] = Array(d.After.Select(a => (JsonNode?)a)),
            // Where it runs, and whose code added it: a system is declared by an assembly and added per world.
            ["added"] = Array(engine.Worlds.SelectMany(w => w.Systems.Where(s => s.Id == d.Id).Select(s => new JsonObject
            {
                ["world"] = w.Name, ["owner"] = s.Owner, ["replacedBy"] = s.ReplacedBy, ["enabled"] = s.Enabled,
            }))),
        }));

        // An input routed to components (issue #91) lists them, each with its own owner; `owner` is the
        // global handler's, or the first component's when there is none.
        root["entityInputs"] = Array(engine.Inputs.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n =>
        {
            var components = engine.Inputs.ComponentsTaking(n);
            var entry = new JsonObject
            {
                ["name"] = n,
                ["owner"] = ledger.OwnerOf("entity input", n)
                            ?? (components.Count > 0 ? ledger.OwnerOf("entity input", $"{n}@{components[0]}") : null),
            };
            if (components.Count > 0)
            {
                entry["global"] = engine.Inputs.HasGlobal(n);
                entry["components"] = Array(components.Select(c => new JsonObject
                {
                    ["component"] = c, ["owner"] = ledger.OwnerOf("entity input", $"{n}@{c}"),
                }));
            }
            return entry;
        }));
        root["entityOutputs"] = Array(engine.Outputs.Names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n => new JsonObject
        {
            ["name"] = n, ["description"] = engine.Outputs.Describe(n), ["owner"] = ledger.OwnerOf("entity output", n),
        }));
        // Game events (04 §3.2): the `[GameEvent]` structs of every assembly this app is made of. Nothing
        // registers an event — a reader or a sender names the type — so they are found by their
        // attribute, which is what a document's "the `Used` event" has to match (TODO #62, issue #298).
        root["gameEvents"] = Array(GameEventTypes(engine).Select(t => new JsonObject
        {
            ["name"] = EventName(t), ["clrType"] = t.FullName, ["assembly"] = t.Assembly.GetName().Name,
        }));
        root["inputActions"] = Array(engine.Actions.All.Select(a => new JsonObject
        {
            ["name"] = a.Name, ["kind"] = a.Kind.ToString(), ["owner"] = ledger.OwnerOf("input action", a.Name),
        }));

        // The open vocabularies (issue #28): each with its JSON key, and every entry with the plugin
        // that registered it and the fields content may write.
        root["vocabularies"] = Array(engine.Vocabularies.All.Select(v => new JsonObject
        {
            ["name"] = v.Name, ["key"] = v.Key, ["default"] = v.Default, ["entryType"] = v.EntryType.FullName,
            ["entries"] = Array(v.Entries.Select(e =>
            {
                var o = new JsonObject { ["id"] = e.Id, ["owner"] = e.Owner };
                Describe(o, Metadata.Of(e.Type), json);
                return (JsonNode)o;
            })),
        }));

        // Relaxed escaping: help text with a § or an apostrophe should read as written.
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
    }

    // The engine's own assembly, every module's and every component declaration's: what the app is made of.
    private static IEnumerable<Type> GameEventTypes(Engine engine)
    {
        var assemblies = new HashSet<System.Reflection.Assembly> { typeof(Engine).Assembly };
        foreach (var module in engine.Modules.Modules) assemblies.Add(module.GetType().Assembly);
        foreach (var declaration in engine.Components.Declarations) assemblies.Add(declaration.Type.Assembly);
        return assemblies
            .SelectMany(LoadableTypes)
            .Where(t => t.IsValueType && t.IsDefined(typeof(GameEventAttribute), false))
            .OrderBy(EventName, StringComparer.Ordinal)
            .ThenBy(t => t.FullName, StringComparer.Ordinal);
    }

    private static IEnumerable<Type> LoadableTypes(System.Reflection.Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }

    // As C# spells it: `Used`, `Added<T>`.
    internal static string EventName(Type type)
    {
        if (!type.IsGenericTypeDefinition) return type.Name;
        int tick = type.Name.IndexOf('`');
        string stem = tick >= 0 ? type.Name.Substring(0, tick) : type.Name;
        return $"{stem}<{string.Join(", ", type.GetGenericArguments().Select(a => a.Name))}>";
    }

    // A declaration's type and fields, into its entry.
    private static void Describe(JsonObject into, TypeMetadata meta, JsonSerializerOptions json)
    {
        into["clrType"] = meta.Type.FullName;
        into["metadata"] = meta.Generated ? "generated" : "reflection";
        into["fields"] = Array(meta.Fields.Select(f => Field(f, meta, json)));
    }

    // One field, as the metadata table has it. The same shape nests for a list's item and an object's
    // fields, which is what a JSON Schema generator (#21) walks.
    public static JsonObject Field(FieldMetadata f, TypeMetadata? owner, JsonSerializerOptions json)
    {
        var o = new JsonObject { ["name"] = f.JsonName };
        if (f.Get != null) o["member"] = f.Name;     // a list's item is not a member of anything
        o["type"] = f.TypeName;
        o["kind"] = f.Kind.ToString();
        if (owner != null && f.Get != null && Default(owner.DefaultOf(f), json) is { } value) o["default"] = value;
        if (f.Min != null) o["min"] = f.Min;
        if (f.Max != null) o["max"] = f.Max;
        if (f.Unit != null) o["unit"] = f.Unit;
        if (f.Tooltip != null) o["tooltip"] = f.Tooltip;
        if (f.Category != null) o["category"] = f.Category;
        if (f.EnumValues.Count > 0) o["enum"] = Array(f.EnumValues.Select(v => (JsonNode?)v));
        if (f.RecordType != null) o["recordType"] = f.RecordType;
        if (f.AssetKind != null) o["assetKind"] = f.AssetKind;
        if (f.Transient) o["transient"] = true;
        if (f.Item != null) o["item"] = Field(f.Item, null, json);
        if (f.Fields.Count > 0) o["fields"] = Array(f.Fields.Select(n => Field(n, null, json)));
        return o;
    }

    // A default as content would write it, or null when it has no JSON form worth quoting (a handle,
    // an empty reference, a float at its limit that JSON cannot spell). JSON Schemas quote it too (#21).
    internal static JsonNode? Default(object? value, JsonSerializerOptions json)
    {
        if (value is null or Entity) return null;
        if (value is float f && (float.IsNaN(f) || float.IsInfinity(f) || f is float.MaxValue or float.MinValue)) return null;
        if (value is double d && (double.IsNaN(d) || double.IsInfinity(d))) return null;
        try
        {
            return JsonSerializer.SerializeToNode(value, value.GetType(), json);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static JsonArray Array(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(item);
        return array;
    }
}
