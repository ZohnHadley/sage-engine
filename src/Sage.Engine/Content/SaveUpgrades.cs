#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;

namespace sage_engine;

// What an [Upgrade] method uses to rewrite an old saved shape (issue #20, REDESIGN §4.5). Field names
// in a save are the C# member names (`LocalPosition`), and the save dialect reads them ignoring case,
// so these find a field the same way: the exact spelling first, then ignoring case.
public static class JsonUpgrades
{
    // `o.RenameField("Hp", "Value")`: the field keeps its value under a new name. Nothing to rename is
    // not an error — an upgrader runs on every old save, including ones that never had the field.
    public static JsonObject RenameField(this JsonObject o, string from, string to)
    {
        if (Find(o, from) is not { } key) return o;
        var value = o[key];
        o.Remove(key);
        o[to] = value;
        return o;
    }

    // `o.RemoveField("LastHitTime")`: for a field the type no longer has and whose value means nothing
    // now. Saying so is the point: without it the load stops and names the field (SaveSerializer).
    public static JsonObject RemoveField(this JsonObject o, string name)
    {
        if (Find(o, name) is { } key) o.Remove(key);
        return o;
    }

    // `o.MoveField("Speed", "Movement.Speed")`: a dotted path either side, into or out of a nested
    // object. Objects on the way to `to` are made as needed.
    public static JsonObject MoveField(this JsonObject o, string from, string to)
    {
        var (source, sourceKey) = Walk(o, from, create: false);
        if (source is null || Find(source, sourceKey) is not { } key) return o;
        var value = source[key];
        source.Remove(key);

        var (target, targetKey) = Walk(o, to, create: true);
        target![targetKey] = value;
        return o;
    }

    private static string? Find(JsonObject o, string name)
    {
        if (o.ContainsKey(name)) return name;
        foreach (var (key, _) in o)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return key;
        return null;
    }

    private static (JsonObject? Parent, string Key) Walk(JsonObject root, string path, bool create)
    {
        string[] parts = path.Split('.');
        var current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string? key = Find(current, parts[i]);
            if (key != null && current[key] is JsonObject next) { current = next; continue; }
            if (!create) return (null, "");
            var made = new JsonObject();
            current[key ?? parts[i]] = made;
            current = made;
        }
        return (current, parts[^1]);
    }
}

// Runs a type's [Upgrade] methods on saved JSON (issue #20). Found by reflection, once per type, when an
// old entry is first read — a current save never needs them.
internal static class Upgraders
{
    private delegate void Upgrade(ref JsonObject o);

    private static readonly Dictionary<Type, SortedList<int, MethodInfo>> Cache = new();
    private static readonly object Lock = new();

    // The upgraders a type declares, by the version each upgrades *from*. Throws on a malformed one:
    // Sage.Generators rejects them at build time (SAGE0007), so this only fires for an assembly
    // built without it.
    public static SortedList<int, MethodInfo> Of(Type type)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(type, out var found)) return found;
            var list = new SortedList<int, MethodInfo>();
            foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<UpgradeAttribute>() is not { } attr) continue;
                var parameters = method.GetParameters();
                if (method.ReturnType != typeof(void) || parameters.Length != 1
                    || parameters[0].ParameterType != typeof(JsonObject).MakeByRefType())
                    throw new InvalidOperationException(
                        $"{type.Name}.{method.Name}: an [Upgrade] method is `static void Name(ref JsonObject o)`");
                if (list.ContainsKey(attr.FromVersion))
                    throw new InvalidOperationException($"{type.Name} has two [Upgrade({attr.FromVersion})] methods");
                list.Add(attr.FromVersion, method);
            }
            Cache[type] = list;
            return list;
        }
    }

    // Brings `data` from `saved` up to `current`. A step with no upgrader is a version bump that needed
    // no rewrite (a field added, say). Returns the (possibly replaced) object.
    public static JsonObject Run(Type type, JsonObject data, int saved, int current)
    {
        foreach (var (from, method) in Of(type))
        {
            if (from < saved || from >= current) continue;
            var step = method.CreateDelegate<Upgrade>();
            step(ref data);
        }
        return data;
    }
}
